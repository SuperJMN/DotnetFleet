# DotnetFleet on Windows

This guide covers running DotnetFleet from a Windows workstation or server, including native Windows Service installation.

## Requirements

- Windows 10/11 or Windows Server.
- .NET 10 SDK.
- Git.
- Network access from each worker to the coordinator URL.

Install the CLI:

```powershell
dotnet tool install -g DotnetFleet.Tool
fleet version
```

You can also use `dnx dotnetfleet.tool ...` for one-off commands.

## Foreground Mode

Use foreground mode while testing because logs stay in the terminal.

Coordinator:

```powershell
fleet coordinator --port 5000
```

Worker on the same machine:

```powershell
fleet worker
```

Worker connected to a coordinator on another machine:

```powershell
fleet worker --coordinator http://192.168.1.29:5000 --token <registration-token>
```

The URL must include `http://` or `https://`.

## Install As Windows Services

DotnetFleet installs native Windows Services through the Service Control Manager. If you run the command from a non-elevated terminal, Windows will show a UAC elevation prompt. For unattended scripts, start PowerShell as Administrator first.

Install a coordinator:

```powershell
fleet coordinator install --port 5000
```

Install a worker on the same machine as the coordinator:

```powershell
fleet worker install --name build-01
```

Install a worker for a remote coordinator:

```powershell
fleet worker install `
  --coordinator http://192.168.1.29:5000 `
  --token <registration-token> `
  --name $env:COMPUTERNAME
```

After first registration, the worker persists its own credentials and no longer needs the registration token for normal restarts.

## Service Names

| Component | Service name | Display name |
|---|---|---|
| Coordinator | `fleet-coordinator` | `DotnetFleet Coordinator` |
| Worker | `fleet-worker-{name}` | `DotnetFleet Worker ({name})` |

In Task Manager, open the **Services** tab and search by the service name, for example `fleet-worker-DESKTOP-NMC4AGI`. In **Details**, the process appears as `fleet.exe`.

You can also use:

```powershell
Get-Service fleet-coordinator
Get-Service fleet-worker-*
services.msc
sc.exe query fleet-worker-DESKTOP-NMC4AGI
```

## Data And Logs

Installed Windows services use `%ProgramData%\DotnetFleet`.

| Item | Default path |
|---|---|
| Service-local CLI | `%ProgramData%\DotnetFleet\tools\fleet.exe` |
| Coordinator data | `%ProgramData%\DotnetFleet\coordinator\` |
| Worker data | `%ProgramData%\DotnetFleet\worker-{name}\` |
| Coordinator logs | `%ProgramData%\DotnetFleet\coordinator\logs\` |
| Worker logs | `%ProgramData%\DotnetFleet\worker-{name}\logs\` |
| Worker credentials | `%ProgramData%\DotnetFleet\worker-{name}\worker.json` |
| Worker repo cache | `%ProgramData%\DotnetFleet\worker-{name}\fleet-repos\` |

To use another location:

```powershell
fleet worker install `
  --coordinator http://myserver:5000 `
  --token <registration-token> `
  --name build-01 `
  --data-dir D:\DotnetFleet\worker-build-01
```

## Manage Services

```powershell
Get-Service fleet-worker-*
Restart-Service fleet-worker-build-01
Stop-Service fleet-worker-build-01
Start-Service fleet-worker-build-01
```

DotnetFleet commands:

```powershell
fleet coordinator status
fleet worker status --name build-01
fleet worker uninstall --name build-01
fleet coordinator uninstall
```

`install`, `uninstall`, and `update` request UAC elevation when needed.

## Update

Update the service-local tool and restart local DotnetFleet services:

```powershell
fleet update
```

Restart services without changing the tool:

```powershell
fleet update --skip-tool-update
```

Pin a version:

```powershell
fleet update --version 0.0.82
```

Manual update:

```powershell
Stop-Service fleet-coordinator
Stop-Service fleet-worker-build-01
dotnet tool update --tool-path "$env:ProgramData\DotnetFleet\tools" DotnetFleet.Tool
Start-Service fleet-coordinator
Start-Service fleet-worker-build-01
```

Data under `%ProgramData%\DotnetFleet` is preserved across updates.

## Troubleshooting

### The service is not visible in Task Manager

Search the **Services** tab for `fleet-worker-{name}`, not only `DotnetFleet`. The display name is `DotnetFleet Worker ({name})`, and the process in **Details** is `fleet.exe`.

Verify through SCM:

```powershell
Get-Service fleet-worker-*
Get-CimInstance Win32_Service -Filter "Name LIKE 'fleet-worker-%'" |
  Select-Object Name,DisplayName,State,StartMode,ProcessId,PathName
```

### The command says Administrator privileges are required

Current versions request UAC automatically. If UAC cannot be shown, for example from a non-interactive SSH session or scheduled task, run the command from an Administrator PowerShell.

### The worker starts but does not appear online

Check that the coordinator is reachable from this Windows machine:

```powershell
Invoke-WebRequest http://192.168.1.29:5000/health -UseBasicParsing
```

Check the worker logs:

```powershell
Get-ChildItem "$env:ProgramData\DotnetFleet\worker-$env:COMPUTERNAME\logs"
Get-Content "$env:ProgramData\DotnetFleet\worker-$env:COMPUTERNAME\logs\worker-*.log" -Tail 100
```

### The worker fails first registration

Make sure the first install has a valid registration token:

```powershell
fleet worker install `
  --coordinator http://myserver:5000 `
  --token <registration-token> `
  --name $env:COMPUTERNAME
```

After registration, `worker.json` contains the worker ID and secret. Back it up if you want to preserve the worker identity.

### The coordinator is remote and I only have admin credentials

The CLI first-registration path expects the coordinator registration token. If you only have admin credentials, log into the coordinator and retrieve the registration token from the coordinator host's `config.json`, or register the worker through the admin API and place the returned credentials in `worker.json`.

### Windows Firewall

For a coordinator running on Windows, allow inbound TCP traffic to the chosen port, usually `5000`. Workers only need outbound access to the coordinator.

```powershell
New-NetFirewallRule `
  -DisplayName "DotnetFleet Coordinator" `
  -Direction Inbound `
  -Protocol TCP `
  -LocalPort 5000 `
  -Action Allow
```
