using System.Text;
using static BCrypt.Net.BCrypt;
using DotnetFleet.Coordinator.Auth;
using DotnetFleet.Coordinator.Data;
using DotnetFleet.Coordinator.Endpoints;
using DotnetFleet.Coordinator.Services;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using DotnetFleet.WorkerService;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

// Bootstrap Serilog early
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // ── EF Core ──────────────────────────────────────────────────────────────
    // Use AddDbContextFactory so EfFleetStorage can create one DbContext per
    // operation — safe for concurrent background services (singletons).
    builder.Services.AddDbContextFactory<FleetDbContext>(options =>
        options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                          ?? "Data Source=fleet.db"));

    builder.Services.AddSingleton<IFleetStorage, EfFleetStorage>();

    // ── Auth ─────────────────────────────────────────────────────────────────
    builder.Services.AddSingleton<JwtService>();

    var jwtSecret = builder.Configuration["Jwt:Secret"]
        ?? throw new InvalidOperationException("Jwt:Secret must be configured.");

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
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
        .AddPolicy("Worker", policy => policy.RequireAuthenticatedUser()); // workers use JWT too

    // ── Services ─────────────────────────────────────────────────────────────
    builder.Services.AddSingleton<LogBroadcaster>();
    builder.Services.AddHostedService<PollingBackgroundService>();

    // ── CORS ─────────────────────────────────────────────────────────────────
    builder.Services.AddCors(opt => opt.AddDefaultPolicy(p =>
        p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

    // ── Embedded worker (all-in-one mode) ────────────────────────────────────
    if (builder.Configuration.GetValue<bool>("EmbedWorker", defaultValue: true))
    {
        builder.Services.AddSingleton<IWorkerJobSource, LocalWorkerJobSource>();
        builder.Services.AddHostedService<WorkerBackgroundService>();
    }

    var app = builder.Build();

    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();

    // ── DB init & seed ────────────────────────────────────────────────────────
    {
        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<FleetDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

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

        // Register embedded worker in DB if not already registered
        if (app.Configuration.GetValue<bool>("EmbedWorker", defaultValue: true))
        {
            var workers = await storage.GetWorkersAsync();
            if (!workers.Any(w => w.IsEmbedded))
            {
                var worker = new Worker
                {
                    Name = "Embedded Worker",
                    SecretHash = HashPassword("embedded"),
                    IsEmbedded = true,
                    Status = WorkerStatus.Online,
                    RepoStoragePath = Path.Combine(
                        app.Configuration["Worker:RepoStoragePath"] ?? "fleet-repos")
                };
                await storage.AddWorkerAsync(worker);
                Log.Information("Registered embedded worker with id {Id}", worker.Id);

                // Persist worker id for the background service to use
                app.Configuration["Worker:EmbeddedWorkerId"] = worker.Id.ToString();
            }
            else
            {
                var embedded = workers.First(w => w.IsEmbedded);
                app.Configuration["Worker:EmbeddedWorkerId"] = embedded.Id.ToString();
            }
        }
    }

    // ── Endpoints ─────────────────────────────────────────────────────────────
    app.MapAuthEndpoints();
    app.MapProjectEndpoints();
    app.MapJobEndpoints();
    app.MapWorkerEndpoints();

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
