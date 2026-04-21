using DotnetFleet.Coordinator;
using Serilog;

// Bootstrap Serilog early
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var app = CoordinatorHostBuilder.Build(new CoordinatorStartupOptions(), args);
    await CoordinatorHostBuilder.InitializeDatabaseAsync(app);
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
