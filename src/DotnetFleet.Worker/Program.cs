using DotnetFleet.WorkerService;
using Microsoft.Extensions.Hosting;
using Serilog;

try
{
    var host = await WorkerHostBuilder.BuildAsync(new WorkerStartupOptions(), args);
    await host.RunAsync();
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
