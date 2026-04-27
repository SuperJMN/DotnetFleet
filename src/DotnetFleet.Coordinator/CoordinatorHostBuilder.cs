using System.Text;
using static BCrypt.Net.BCrypt;
using DotnetFleet.Coordinator.Auth;
using DotnetFleet.Coordinator.Data;
using DotnetFleet.Coordinator.Endpoints;
using DotnetFleet.Coordinator.Services;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Serilog;

namespace DotnetFleet.Coordinator;

public class CoordinatorStartupOptions
{
    public int Port { get; set; } = 5000;
    public string? JwtSecret { get; set; }
    public string? RegistrationToken { get; set; }
    public string? DataDir { get; set; }
    public string? AdminPassword { get; set; }
    public string? Urls { get; set; }
    public bool NoMdns { get; set; }
}

public static class CoordinatorHostBuilder
{
    public static WebApplication Build(CoordinatorStartupOptions? options, string[] args)
    {
        options ??= new CoordinatorStartupOptions();

        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseSerilog((ctx, services, lc) => lc
            .ReadFrom.Configuration(ctx.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"));

        // Apply CLI overrides to configuration
        ApplyOverrides(builder.Configuration, options);

        // ── EF Core ──────────────────────────────────────────────────────────
        builder.Services.AddDbContextFactory<FleetDbContext>(opt =>
            opt.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                          ?? "Data Source=fleet.db"));

        builder.Services.AddSingleton<IFleetStorage, EfFleetStorage>();
        builder.Services.AddSingleton<IWorkerSelector, CapabilityWorkerSelector>();

        // ── Auth ─────────────────────────────────────────────────────────────
        builder.Services.AddSingleton<JwtService>();

        var jwtSecret = builder.Configuration["Jwt:Secret"]
            ?? throw new InvalidOperationException("Jwt:Secret must be configured.");

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwtOptions =>
            {
                jwtOptions.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                    ValidateIssuer = true,
                    ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "DotnetFleet",
                    ValidateAudience = true,
                    ValidAudience = builder.Configuration["Jwt:Audience"] ?? "DotnetFleet",
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("Admin", policy => policy.RequireRole("Admin"))
            .AddPolicy("Worker", policy => policy.RequireRole("Worker"));

        // ── Services ─────────────────────────────────────────────────────────
        builder.Services.AddSingleton<LogBroadcaster>();
        builder.Services.AddSingleton<Endpoints.WorkerLivenessFilter>();
        builder.Services.AddSingleton<JobAssignmentSignal>();
        builder.Services.AddSingleton<IDurationEstimator, EwmaDurationEstimator>();
        builder.Services.AddHostedService<PollingBackgroundService>();
        builder.Services.AddHostedService<JobAssignmentService>();
        builder.Services.AddHostedService<StaleJobReaperService>();

        // ── mDNS LAN auto-discovery ──────────────────────────────────────────
        if (!options.NoMdns)
        {
            var instance = $"fleet-{Environment.MachineName}".ToLowerInvariant();
            var asmVersion = typeof(CoordinatorHostBuilder).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            builder.Services.AddSingleton<IHostedService>(_ => new MdnsAdvertiser(options.Port, instance, asmVersion));
        }

        // ── CORS ─────────────────────────────────────────────────────────────
        builder.Services.AddCors(opt => opt.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

        // ── URL binding ──────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(options.Urls))
        {
            builder.WebHost.UseUrls(options.Urls);
        }
        else if (options.Port != 5000 || !builder.Configuration.GetSection("urls").Exists())
        {
            builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");
        }

        var app = builder.Build();

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();

        // ── Endpoints ────────────────────────────────────────────────────────
        app.MapHealthEndpoints();
        app.MapAuthEndpoints();
        app.MapProjectEndpoints();
        app.MapJobEndpoints();
        app.MapWorkerEndpoints();
        app.MapSecretEndpoints();

        return app;
    }

    public static async Task InitializeDatabaseAsync(WebApplication app)
    {
        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<FleetDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Secrets" (
                "Id"        TEXT NOT NULL CONSTRAINT "PK_Secrets" PRIMARY KEY,
                "Name"      TEXT NOT NULL,
                "Value"     TEXT NOT NULL,
                "ProjectId" TEXT,
                "CreatedAt" INTEGER NOT NULL,
                "UpdatedAt" INTEGER NOT NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_Secrets_ProjectId\" ON \"Secrets\" (\"ProjectId\")");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Secrets_ProjectId_Name\" ON \"Secrets\" (\"ProjectId\", \"Name\")");

        var hasGitToken = (await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('Projects') WHERE name='GitToken'")
            .ToListAsync()).FirstOrDefault() > 0;
        if (!hasGitToken)
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Projects\" ADD COLUMN \"GitToken\" TEXT NULL");
        }

        var hasCancellationRequestedAt = (await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('DeploymentJobs') WHERE name='CancellationRequestedAt'")
            .ToListAsync()).FirstOrDefault() > 0;
        if (!hasCancellationRequestedAt)
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"DeploymentJobs\" ADD COLUMN \"CancellationRequestedAt\" INTEGER NULL");
        }

        var hasWorkerVersion = (await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('Workers') WHERE name='Version'")
            .ToListAsync()).FirstOrDefault() > 0;
        if (!hasWorkerVersion)
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Workers\" ADD COLUMN \"Version\" TEXT NULL");
        }

        // Capability columns (issue #14): added so the coordinator can pick the
        // best-suited worker for each job. Each ALTER is gated by a pragma_table_info
        // probe so re-runs and pre-existing databases stay safe.
        await EnsureWorkerColumnAsync(db, "ProcessorCount", "INTEGER NOT NULL DEFAULT 0");
        await EnsureWorkerColumnAsync(db, "TotalMemoryMb", "INTEGER NOT NULL DEFAULT 0");
        await EnsureWorkerColumnAsync(db, "OperatingSystem", "TEXT NULL");
        await EnsureWorkerColumnAsync(db, "Architecture", "TEXT NULL");
        await EnsureWorkerColumnAsync(db, "CpuModel", "TEXT NULL");

        var hasJobVersion = (await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('DeploymentJobs') WHERE name='Version'")
            .ToListAsync()).FirstOrDefault() > 0;
        if (!hasJobVersion)
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"DeploymentJobs\" ADD COLUMN \"Version\" TEXT NULL");
        }

        // Smart-scheduler columns: AssignedAt + EstimatedDurationMs (issue: smart scheduling).
        await EnsureJobColumnAsync(db, "AssignedAt", "INTEGER NULL");
        await EnsureJobColumnAsync(db, "EstimatedDurationMs", "INTEGER NULL");

        // EWMA stats table (per project + worker, used by JobAssignmentService).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "JobDurationStats" (
                "ProjectId"   TEXT NOT NULL,
                "WorkerId"    TEXT NOT NULL,
                "EwmaMs"      REAL NOT NULL,
                "Samples"     INTEGER NOT NULL,
                "LastUpdated" INTEGER NOT NULL,
                CONSTRAINT "PK_JobDurationStats" PRIMARY KEY ("ProjectId", "WorkerId")
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_JobDurationStats_WorkerId\" ON \"JobDurationStats\" (\"WorkerId\")");

        // ── Startup reconciliation ─────────────────────────────────────────
        // Jobs that were Running or Assigned during the last shutdown can never
        // complete because the worker context is lost. Fail them so they don't
        // sit around forever.
        var orphanedJobs = await db.DeploymentJobs
            .Where(j => j.Status == JobStatus.Running || j.Status == JobStatus.Assigned)
            .ToListAsync();

        var now = DateTimeOffset.UtcNow;
        foreach (var job in orphanedJobs)
        {
            job.Status = JobStatus.Failed;
            job.FinishedAt = now;
            job.ErrorMessage = "Failed during coordinator restart — job state could not be recovered.";
        }

        // Workers will re-register via heartbeat; start them all as Offline.
        var activeWorkers = await db.Workers
            .Where(w => w.Status != WorkerStatus.Offline)
            .ToListAsync();

        foreach (var w in activeWorkers)
            w.Status = WorkerStatus.Offline;

        if (orphanedJobs.Count > 0 || activeWorkers.Count > 0)
        {
            await db.SaveChangesAsync();
            Log.Information("Startup reconciliation: failed {Jobs} orphaned job(s), reset {Workers} worker(s) to Offline",
                orphanedJobs.Count, activeWorkers.Count);
        }

        var storage = app.Services.GetRequiredService<IFleetStorage>();
        if (!await storage.AnyUserExistsAsync())
        {
            var adminPassword = app.Configuration["Seed:AdminPassword"] ?? "admin";
            var admin = new User
            {
                Username = "admin",
                PasswordHash = HashPassword(adminPassword),
                Role = UserRole.Admin
            };
            await storage.AddUserAsync(admin);
            Log.Information("Seeded default admin user (username: admin)");
        }
    }

    private static async Task EnsureWorkerColumnAsync(FleetDbContext db, string columnName, string columnDefinition)
    {
        var exists = (await db.Database
            .SqlQueryRaw<long>($"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('Workers') WHERE name='{columnName}'")
            .ToListAsync()).FirstOrDefault() > 0;
        if (!exists)
        {
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"Workers\" ADD COLUMN \"{columnName}\" {columnDefinition}");
        }
    }

    private static async Task EnsureJobColumnAsync(FleetDbContext db, string columnName, string columnDefinition)
    {
        var exists = (await db.Database
            .SqlQueryRaw<long>($"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('DeploymentJobs') WHERE name='{columnName}'")
            .ToListAsync()).FirstOrDefault() > 0;
        if (!exists)
        {
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"DeploymentJobs\" ADD COLUMN \"{columnName}\" {columnDefinition}");
        }
    }

    private static void ApplyOverrides(ConfigurationManager config, CoordinatorStartupOptions options)
    {
        var overrides = new Dictionary<string, string?>();

        if (!string.IsNullOrEmpty(options.JwtSecret))
            overrides["Jwt:Secret"] = options.JwtSecret;

        if (!string.IsNullOrEmpty(options.RegistrationToken))
            overrides["Workers:RegistrationToken"] = options.RegistrationToken;

        if (!string.IsNullOrEmpty(options.AdminPassword))
            overrides["Seed:AdminPassword"] = options.AdminPassword;

        if (!string.IsNullOrEmpty(options.DataDir))
            overrides["ConnectionStrings:DefaultConnection"] = $"Data Source={Path.Combine(options.DataDir, "fleet.db")}";

        if (overrides.Count > 0)
            config.AddInMemoryCollection(overrides);
    }
}
