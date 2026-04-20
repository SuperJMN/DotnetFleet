using DotnetFleet.Core.Interfaces;
using DotnetFleet.WorkerService;
using DotnetFleet.WorkerService.Bootstrap;
using DotnetFleet.WorkerService.Coordinator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

const string TokenHttpClient = "WorkerTokenManager";

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "FLEET_");

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/worker-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger, dispose: true);

var workerSection = builder.Configuration.GetSection(WorkerOptions.SectionName);
builder.Services.Configure<WorkerOptions>(workerSection);

var bootOptions = workerSection.Get<WorkerOptions>() ?? new WorkerOptions();

using (var bootLoggerFactory = LoggerFactory.Create(b => b.AddSerilog(Log.Logger)))
{
    var bootLogger = bootLoggerFactory.CreateLogger("Bootstrap");
    await WorkerBootstrap.EnsureCredentialsAsync(bootOptions, bootLogger);
}

builder.Services.PostConfigure<WorkerOptions>(o =>
{
    o.Id = bootOptions.Id;
    o.Secret = bootOptions.Secret;
});

builder.Services.AddTransient<WorkerAuthHandler>();

builder.Services.AddHttpClient(TokenHttpClient, (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
    client.BaseAddress = new Uri(opts.CoordinatorBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
});

builder.Services.AddSingleton<WorkerTokenManager>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new WorkerTokenManager(
        factory.CreateClient(TokenHttpClient),
        sp.GetRequiredService<IOptions<WorkerOptions>>(),
        sp.GetRequiredService<ILogger<WorkerTokenManager>>());
});

builder.Services
    .AddHttpClient<IWorkerCoordinatorClient, HttpWorkerCoordinatorClient>((sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
        client.BaseAddress = new Uri(opts.CoordinatorBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
    })
    .AddHttpMessageHandler<WorkerAuthHandler>();

builder.Services
    .AddHttpClient<IWorkerJobSource, RemoteWorkerJobSource>((sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
        client.BaseAddress = new Uri(opts.CoordinatorBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
    })
    .AddHttpMessageHandler<WorkerAuthHandler>();

builder.Services.AddHostedService<RemoteWorkerBackgroundService>();

try
{
    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Worker terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
