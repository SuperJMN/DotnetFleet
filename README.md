# DotnetFleet

A self-hosted CI/CD system built on top of [DotnetDeployer](https://github.com/SuperJMN/DotnetDeployer). Deploy your .NET projects on your own infrastructure — no GitHub Actions or Azure DevOps required.

## Architecture

```
┌─────────────────────────────────────────────────────┐
│              DotnetFleet.Coordinator                │
│                                                     │
│  ASP.NET Core Minimal API   • job queue & polling   │
│  EF Core / SQLite           • repo-cache metadata   │
│  JWT (User + Worker roles)  • SSE log fan-out       │
└─────────────────────────────────────────────────────┘
        ▲                              ▲
        │ HTTP (REST + SSE)            │ HTTP (REST)
        │                              │
        ▼                              ▼
┌──────────────────────────┐   ┌──────────────────────────┐
│ DotnetFleet (Avalonia)   │   │ DotnetFleet.Worker × N   │
│                          │   │                          │
│ Login → Projects →       │   │ register → login → poll  │
│ Trigger Deploy →         │   │ → clone/fetch → run dnx  │
│ Live Logs                │   │ → stream logs            │
└──────────────────────────┘   └──────────────────────────┘
```

Coordinator and Workers are now **separate processes**. The coordinator no longer
hosts an in-process worker; you run one or more `DotnetFleet.Worker` instances
and they connect to the coordinator over HTTP.

### Components

| Project | Role |
|---|---|
| `DotnetFleet.Tool` | **CLI tool** (`fleet`) — install via `dotnet tool install -g DotnetFleet.Tool` |
| `DotnetFleet.Core` | Domain models, interfaces, enums |
| `DotnetFleet.Coordinator` | ASP.NET Core API + queue + polling + EF Core SQLite |
| `DotnetFleet.Worker` | Standalone Generic Host: git clone/fetch + `dnx dotnetdeployer.tool -y` |
| `DotnetFleet.Api.Client` | Typed HTTP client used by the desktop app |
| `DotnetFleet` | Avalonia 12 shared ViewModels + Views |
| `DotnetFleet.Desktop` | Desktop host (Windows / Linux / macOS) |
| `DotnetFleet.Browser` | WebAssembly host (optional) |
| `DotnetFleet.Tests` | xUnit unit tests |

## How It Works

1. **Add a project** — paste a Git URL and branch name in the desktop app.
2. **Trigger a deploy** — click *Deploy Now* or let automatic polling detect new commits.
3. **A worker claims the job** — `ClaimNextJobAsync` is atomic (Serializable tx); only one worker wins, even under contention.
4. **The worker clones (or fetches) the repo** with full history, then runs `dnx dotnetdeployer.tool -y` inside it.
5. **Live logs** — the coordinator streams log lines via SSE; the desktop app shows them in real time.
6. **Repo cache** — cloned repos are kept on disk for faster subsequent deploys; an LRU eviction policy prevents disk exhaustion.

## Coordinator ↔ Worker Protocol

All worker traffic flows over plain HTTP/JSON to the coordinator. There is no
direct DB or queue access from the worker side.

| Step | Endpoint | Auth | Purpose |
|---|---|---|---|
| 1. Register | `POST /api/workers/register` | `X-Registration-Token` header *or* Admin JWT | Issues `{workerId, secret}` once; persisted to `worker-credentials.json`. |
| 2. Login | `POST /api/workers/login` | anonymous (body has id+secret) | Returns a Worker JWT carrying the `worker_id` claim. |
| 3. Heartbeat | `POST /api/workers/{id}/heartbeat` | Worker JWT | Marks the worker `Online` and refreshes `LastSeenAt`. |
| 4. Claim | `POST /api/queue/next` | Worker JWT | Atomically claims the oldest queued job for this worker. |
| 5. Status / Logs | `POST /api/jobs/{id}/status`, `POST /api/jobs/{id}/logs` | Worker JWT | Stream progress + log lines back. |

The Worker JWT carries a `worker_id` claim. Every worker-scoped endpoint
verifies that this claim matches the route id, so a stolen token cannot act on
behalf of another worker.

## Getting Started

### Prerequisites

- .NET 10 SDK
- Git

### Quick Install (recommended)

```bash
dotnet tool install -g DotnetFleet.Tool
```

### 1. Start the Coordinator

```bash
fleet coordinator --port 5000
```

On first run the coordinator auto-generates a JWT secret and a worker registration
token, saves them to `~/.fleet/coordinator/config.json`, and prints a banner:

```
  ╔══════════════════════════════════════════════════════════════╗
  ║                   DotnetFleet Coordinator                    ║
  ╠══════════════════════════════════════════════════════════════╣
  ║  Listening on:       http://0.0.0.0:5000                     ║
  ║  Admin credentials:  admin / admin                           ║
  ║  Registration token: f7a2b9c1d4e5...                         ║
  ╠══════════════════════════════════════════════════════════════╣
  ║  Connect workers with:                                       ║
  ║    fleet worker --coordinator http://<host>:5000 \            ║
  ║                 --token f7a2b9c1d4e5...                      ║
  ╚══════════════════════════════════════════════════════════════╝
```

### 2. Connect one (or more) Workers

Copy the command from the coordinator banner and run it on the same or a
different machine:

```bash
fleet worker --coordinator http://myserver:5000 --token <token>
```

The worker self-registers, persists credentials to
`~/.fleet/worker-<hostname>/worker.json`, then loops on `claim → run → report`.

To run several workers:

```bash
fleet worker --coordinator http://myserver:5000 --token <token> --name worker-1
fleet worker --coordinator http://myserver:5000 --token <token> --name worker-2
```

### 3. Run the Desktop App

Download the latest release for your platform from
[GitHub Releases](https://github.com/SuperJMN/DotnetFleet/releases), or build
from source:

```bash
cd src/DotnetFleet.Desktop
dotnet run
```

Enter the coordinator endpoint (e.g., `http://localhost:5000`) on the Connect
screen, then log in.

### CLI Reference

```
fleet coordinator [options]          # Run coordinator in foreground
fleet coordinator install [options]  # Install as systemd service (Linux, requires sudo)
fleet coordinator uninstall          # Remove systemd service
fleet coordinator status             # Show service status

  --port, -p <port>           HTTP port (default: 5000)
  --data-dir <path>           Data directory (default: ~/.fleet/coordinator)
  --token, -t <token>         Registration token (auto-generated)
  --jwt-secret <secret>       JWT signing secret (auto-generated)
  --admin-password <pass>     Admin password (default: admin)
  --urls <urls>               ASP.NET Core URLs override

fleet worker [options]               # Run worker in foreground
fleet worker install [options]       # Install as systemd service (Linux, requires sudo)
fleet worker uninstall [--name <n>]  # Remove systemd service
fleet worker status [--name <n>]     # Show service status

  --coordinator, -c <url>     Coordinator URL (required)
  --token, -t <token>         Registration token (required on first run)
  --name, -n <name>           Worker display name (default: hostname)
  --data-dir <path>           Data directory (default: ~/.fleet/worker-{name})
  --poll-interval <secs>      Polling interval in seconds (default: 10)
  --max-disk <gb>             Max disk usage in GB (default: 10)

fleet version
```

### Service Installation (Linux)

For production environments, install the coordinator and workers as **systemd services** so they start
automatically on boot and restart on failure:

```bash
# Install coordinator as a service
sudo fleet coordinator install --port 5000
#  → Creates and starts fleet-coordinator.service
#  → Shows the registration token for workers

# Install a worker as a service
sudo fleet worker install --coordinator http://myserver:5000 --token <token> --name build-01

# Check status
sudo fleet coordinator status
sudo fleet worker status --name build-01

# View logs
journalctl -u fleet-coordinator -f
journalctl -u fleet-worker-build-01 -f

# Uninstall
sudo fleet coordinator uninstall
sudo fleet worker uninstall --name build-01
```

> **Note**: Service installation currently supports Linux with systemd. On other platforms, use the
> foreground commands (`fleet coordinator`, `fleet worker`) with a process manager of your choice.

### Development (from source)

If you prefer to run from the Git checkout:

```bash
# Coordinator
cd src/DotnetFleet.Coordinator
dotnet run

# Worker (in a separate terminal)
cd src/DotnetFleet.Worker
export Worker__CoordinatorBaseUrl=http://localhost:5000
export Worker__RegistrationToken=change-me
dotnet run

# Desktop
cd src/DotnetFleet.Desktop
dotnet run
```

Or use the helper script to run everything at once:

```bash
./scripts/run-fleet.sh          # 1 coordinator + 1 worker + GUI
WORKERS=3 ./scripts/run-fleet.sh  # 1 coordinator + 3 workers + GUI
```

## Configuration

### Coordinator (`appsettings.json` or `DotnetFleet__*` env vars)

| Setting | Default | Description |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | `Data Source=fleet.db` | EF Core / SQLite connection string |
| `Jwt:Secret` | *(must set)* | JWT signing secret — **change in production** |
| `Jwt:Issuer` / `Jwt:Audience` | `DotnetFleet` | JWT issuer / audience |
| `Workers:RegistrationToken` | *(unset)* | Shared secret for `POST /api/workers/register`; if unset, only Admin can register |

### Worker (`appsettings.json` or `Worker__*` env vars; `FLEET_` prefix also supported)

| Setting | Default | Description |
|---|---|---|
| `Worker:CoordinatorBaseUrl` | `http://localhost:5000` | Coordinator URL |
| `Worker:RegistrationToken` | *(unset)* | Used only for first-time self-registration |
| `Worker:Id` / `Worker:Secret` | *(from credentials file)* | Skip self-registration if both provided |
| `Worker:CredentialsFile` | `worker-credentials.json` | Where to persist credentials after registration |
| `Worker:Name` | `Environment.MachineName` | Display name in the workers list |
| `Worker:PollIntervalSeconds` | `10` | Queue polling cadence |
| `Worker:HeartbeatIntervalSeconds` | `30` | Heartbeat cadence |
| `Worker:RepoStoragePath` | *(unset)* | Where cloned repos live (defaults to working dir) |
| `Worker:MaxDiskUsageBytes` | `10 GiB` | LRU eviction threshold |

## Worker Behaviour

Each worker runs an independent loop:

1. **Bootstrap** — load credentials from config / credentials file, otherwise self-register against the coordinator.
2. **Login** — exchange `(workerId, secret)` for a JWT (cached, refreshed on 401 or near-expiry).
3. **Heartbeat** — `POST /heartbeat` on a timer so the coordinator knows it's alive.
4. **Poll** — `POST /api/queue/next` every `PollIntervalSeconds`; claims are atomic.
5. **Execute** — clone or fetch with full history, then run `dnx dotnetdeployer.tool -y`.
6. **Stream** stdout/stderr line by line and report final status.

### Disk Management

Cached repos live under the worker's `RepoStoragePath`. When total disk usage
exceeds `MaxDiskUsageBytes`, the worker evicts least-recently-used repos until
enough space is reclaimed. Limits are configurable per worker via the desktop
app's *Workers* tab.

## Automatic Polling

Set `pollingIntervalMinutes > 0` on any project. The coordinator will check the
latest commit SHA on the configured branch using `git ls-remote` and enqueue a
deploy whenever it changes.

## API Overview

| Method | Path | Auth | Description |
|---|---|---|---|
| `POST` | `/api/auth/login` | anonymous | Obtain User JWT |
| `GET` / `POST` / `DELETE` | `/api/projects[/...]` | User | CRUD projects |
| `POST` | `/api/projects/{id}/deploy` | User | Trigger deploy |
| `GET` | `/api/jobs/{id}/logs` | User | Live log stream (SSE) |
| `GET` | `/api/workers` | Admin | List workers |
| `POST` | `/api/workers/register` | RegistrationToken / Admin | Worker self-registration |
| `POST` | `/api/workers/login` | anonymous | Worker login (returns Worker JWT) |
| `POST` | `/api/workers/{id}/heartbeat` | Worker | Liveness ping |
| `POST` | `/api/queue/next` | Worker | Atomic job claim |

## Running Tests

```bash
dotnet test
```

## Requirements for Target Repos

Each repo you deploy must contain a `deployer.yaml` file at the root. See the
[DotnetDeployer documentation](https://github.com/SuperJMN/DotnetDeployer) for
the format.

The worker uses `dnx dotnetdeployer.tool -y` — no local tool manifest or global
installation required; .NET 10's `dnx` downloads and caches the tool
automatically.
