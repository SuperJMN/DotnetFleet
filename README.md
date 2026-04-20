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
- `dnx` tool runner (ships with .NET 10)
- Git

### 1. Run the Coordinator

```bash
cd src/DotnetFleet.Coordinator
dotnet run
```

The coordinator starts on `http://localhost:5000`. It creates `fleet.db`
(SQLite) in the working directory and seeds an admin user.

**Default credentials**: `admin` / `admin`

To allow workers to self-register, set a registration token (any opaque string):

```bash
export DotnetFleet__Workers__RegistrationToken=change-me
dotnet run
```

### 2. Run one (or more) Workers

In a separate terminal:

```bash
cd src/DotnetFleet.Worker
export Worker__CoordinatorBaseUrl=http://localhost:5000
export Worker__RegistrationToken=change-me   # same value as above
dotnet run
```

On first start, the worker self-registers, persists its credentials to
`worker-credentials.json`, then loops on `claim → run → report`. Stop and
restart it freely — credentials are reused from disk.

To run several workers on the same machine, give each one its own working
directory (so each gets its own `worker-credentials.json` and repo cache):

```bash
mkdir -p /var/lib/fleet/worker-1 /var/lib/fleet/worker-2
cd /var/lib/fleet/worker-1 && dotnet run --project /path/to/src/DotnetFleet.Worker &
cd /var/lib/fleet/worker-2 && dotnet run --project /path/to/src/DotnetFleet.Worker &
```

### 3. Run the Desktop App

```bash
cd src/DotnetFleet.Desktop
dotnet run
```

Enter the coordinator endpoint (e.g., `http://localhost:5000`) on the Connect
screen, then log in.

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
