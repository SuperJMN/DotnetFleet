using System.Text.Json;
using DotnetFleet.Tool;

namespace DotnetFleet.Tests;

public class WindowsServiceManagerTests : IDisposable
{
    private readonly string tempRoot;

    public WindowsServiceManagerTests()
    {
        tempRoot = Path.Combine(Path.GetTempPath(), "fleet-windows-service-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void BuildCoordinatorImagePath_QuotesExecutableAndArguments()
    {
        var opts = new ServiceInstaller.CoordinatorInstallOptions(
            Port: 6123,
            DataDir: @"C:\ProgramData\DotnetFleet\coordinator data",
            Token: "token with spaces",
            JwtSecret: null,
            AdminPassword: "admin pass",
            Urls: "http://0.0.0.0:6123",
            NoMdns: true);

        var imagePath = WindowsServiceManager.BuildCoordinatorImagePath(
            @"C:\Program Files\DotnetFleet\tools\fleet.exe",
            opts);

        imagePath.Should().Be(
            "\"C:\\Program Files\\DotnetFleet\\tools\\fleet.exe\" coordinator --port 6123 --data-dir \"C:\\ProgramData\\DotnetFleet\\coordinator data\" --token \"token with spaces\" --admin-password \"admin pass\" --urls \"http://0.0.0.0:6123\" --no-mdns");
    }

    [Fact]
    public void TryDiscoverCoordinatorFromImagePath_LoadsTokenAndPortFromQuotedDataDir()
    {
        var dataDir = Path.Combine(tempRoot, "coordinator data");
        Directory.CreateDirectory(dataDir);
        WriteConfig(Path.Combine(dataDir, "config.json"), token: "token-abc", port: 7654);

        var imagePath =
            $"\"C:\\ProgramData\\DotnetFleet\\tools\\fleet.exe\" coordinator --port 5000 --data-dir \"{dataDir}\"";

        var result = WindowsServiceManager.TryDiscoverCoordinatorFromImagePath(imagePath);

        result.Should().NotBeNull();
        result!.Url.Should().Be("http://localhost:7654");
        result.Token.Should().Be("token-abc");
        result.Source.Should().Contain("Windows service ImagePath");
    }

    [Fact]
    public void Split_PreservesBackslashesInQuotedWindowsPaths()
    {
        var args = ServiceCommandLine.Split(
            "\"C:\\ProgramData\\DotnetFleet\\tools\\fleet.exe\" coordinator --data-dir \"C:\\ProgramData\\DotnetFleet\\coordinator\"");

        args[0].Should().Be(@"C:\ProgramData\DotnetFleet\tools\fleet.exe");
        args[3].Should().Be(@"C:\ProgramData\DotnetFleet\coordinator");
    }

    [Fact]
    public void GetDefaultWindowsDataDir_UsesProgramData()
    {
        var dataDir = WindowsServiceManager.GetDefaultWindowsDataDir(
            "worker-build-01",
            @"C:\ProgramData");

        dataDir.Should().Be(@"C:\ProgramData\DotnetFleet\worker-build-01");
    }

    [Fact]
    public void FindFleetServiceNames_ParsesLocalizedScQueryOutput()
    {
        const string output = """
            NOMBRE_DE_SERVICIO: Appinfo
            NOMBRE_PARA_MOSTRAR: Información de la aplicación

            NOMBRE_SERVICIO: fleet-coordinator
            NOMBRE_MOSTRAR : DotnetFleet Coordinator

            NOMBRE_DE_SERVICIO: fleet-worker-smoke-worker
            NOMBRE_PARA_MOSTRAR: DotnetFleet Worker (smoke-worker)
            """;

        var services = WindowsServiceManager.FindFleetServiceNames(output);

        services.Should().Equal("fleet-coordinator", "fleet-worker-smoke-worker");
    }

    [Fact]
    public void FindServiceImagePath_ParsesLocalizedScQueryConfigOutput()
    {
        const string output = """
            NOMBRE_SERVICIO: fleet-coordinator
                    TIPO               : 10  WIN32_OWN_PROCESS
                    TIPO_INICIO        : 2   AUTO_START
                    NOMBRE_RUTA_BINARIO: "C:\ProgramData\DotnetFleet\tools\fleet.exe" coordinator --port 57130 --data-dir "C:\ProgramData\DotnetFleet\coordinator"
                    NOMBRE_INICIO_SERVICIO: LocalSystem
            """;

        var imagePath = WindowsServiceManager.FindServiceImagePath(output);

        imagePath.Should().Be("\"C:\\ProgramData\\DotnetFleet\\tools\\fleet.exe\" coordinator --port 57130 --data-dir \"C:\\ProgramData\\DotnetFleet\\coordinator\"");
    }

    private static void WriteConfig(string path, string token, int port)
    {
        var obj = new
        {
            jwtSecret = "jwt",
            registrationToken = token,
            port
        };
        File.WriteAllText(path, JsonSerializer.Serialize(obj));
    }
}
