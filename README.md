# DotnetFleet

A self-hosted CI/CD system built on top of [DotnetDeployer](https://github.com/SuperJMN/DotnetDeployer). Deploy your .NET projects on your own infrastructure — no GitHub Actions or Azure DevOps required.

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                DotnetFleet.Coordinator               │
│                                                     │
│  ASP.NET Core Minimal API  ┌────────────────────┐   │
│  EF Core / SQLite          │  Embedded Worker   │   │
│  JWT Authentication        │  (default mode)    │   │
│  SSE log streaming         └────────────────────┘   │
└─────────────────────────────────────────────────────┘
              ▲
              │  HTTP (REST + SSE)
              ▼
┌─────────────────────────────────────────────────────┐
│            DotnetFleet (Avalonia Desktop)            │
│                                                     │
│  Login → Projects → Trigger Deploy → Live Logs      │
└─────────────────────────────────────────────────────┘
```

### Components

| Project | Role |
|---|---|
| `DotnetFleet.Core` | Domain models, interfaces, enums |
| `DotnetFleet.Coordinator` | ASP.NET Core API + queue + polling + EF Core SQLite |
| `DotnetFleet.Worker` | Git clone/fetch + `dnx dotnetdeployer.tool -y` execution |
| `DotnetFleet.Api.Client` | Typed HTTP client used by the desktop app |
| `DotnetFleet` | Avalonia 12 shared ViewModels + Views |
| `DotnetFleet.Desktop` | Desktop host (Windows / Linux / macOS) |
| `DotnetFleet.Browser` | WebAssembly host (optional) |
| `DotnetFleet.Tests` | xUnit unit tests |

## How It Works

1. **Add a project** — paste a Git URL and branch name in the desktop app.
2. **Trigger a deploy** — click *Deploy Now* or let automatic polling detect new commits.
3. **Worker picks up the job** — clones (or fetches) the repo with full history, then runs `dnx dotnetdeployer.tool -y` inside it.
4. **Live logs** — the coordinator streams log lines via SSE; the desktop app shows them in real time.
5. **Repo cache** — cloned repos are kept on disk for faster subsequent deploys; an LRU eviction policy prevents disk exhaustion.

## Getting Started

### Prerequisites

- .NET 10 SDK
- `dnx` tool runner (ships with .NET 10)
- Git

### Run the Coordinator (all-in-one mode)

```bash
cd src/DotnetFleet.Coordinator
dotnet run
```

The coordinator starts on `http://localhost:5000` with an embedded worker. It creates `fleet.db` (SQLite) and a `fleet-repos/` directory for cached clones in the working directory.

**Default credentials**: `admin` / `admin`

### Run the Desktop App

```bash
cd src/DotnetFleet.Desktop
dotnet run
```

Enter the coordinator endpoint (e.g., `http://localhost:5000`) on the Connect screen, then log in.

### Configuration

The coordinator is configured via environment variables or `appsettings.json`:

| Variable | Default | Description |
|---|---|---|
| `DotnetFleet__EmbedWorker` | `true` | Run a worker in-process |
| `DotnetFleet__ReposDirectory` | `fleet-repos` | Directory for cloned repos |
| `DotnetFleet__Jwt__Secret` | *(generated)* | JWT signing secret — **change in production** |

## Worker Behaviour

The embedded worker (or a standalone worker process):

1. **Registers** with the coordinator and gets a `workerId`.
2. **Polls** `GET /api/queue/next` every 10 seconds for a queued job.
3. **Clones or fetches** the target repository with full history (required by GitVersion).
4. **Invokes** `dnx dotnetdeployer.tool -y` in the repo root. DotnetDeployer reads `deployer.yaml` and performs the actual deploy (NuGet push, GitHub Release, etc.).
5. **Streams** stdout/stderr lines to the coordinator line by line.
6. **Reports** success or failure when the process exits.

### Disk Management

Cached repos live in `fleet-repos/`. When total disk usage exceeds the configured limit, the worker evicts the least-recently-used repos until enough space is reclaimed.

Configure the limit per worker via the Workers tab in the desktop app.

## Automatic Polling

Set `pollingIntervalMinutes > 0` on any project. The coordinator will check the latest commit SHA on the configured branch using `git ls-remote` and enqueue a deploy whenever it changes.

## API Overview

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/auth/login` | Obtain JWT |
| `GET` | `/api/projects` | List projects |
| `POST` | `/api/projects` | Add project |
| `DELETE` | `/api/projects/{id}` | Remove project |
| `POST` | `/api/projects/{id}/deploy` | Trigger deploy |
| `GET` | `/api/projects/{id}/jobs` | Job history |
| `GET` | `/api/jobs/{id}` | Job status |
| `GET` | `/api/jobs/{id}/logs` | Live log stream (SSE) |
| `GET` | `/api/workers` | List workers |

## Running Tests

```bash
dotnet test tests/DotnetFleet.Tests
```

## Requirements for Target Repos

Each repo you deploy must contain a `deployer.yaml` file at the root. See the [DotnetDeployer documentation](https://github.com/SuperJMN/DotnetDeployer) for the format.

The worker uses `dnx dotnetdeployer.tool -y` — no local tool manifest or global installation required; .NET 10's `dnx` downloads and caches the tool automatically.
