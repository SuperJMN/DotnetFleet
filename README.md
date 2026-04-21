<div align="center">

# 🚀 DotnetFleet

**Self-hosted CI/CD for .NET — up and running in 3 commands.**

Deploy your .NET projects on your own infrastructure.
No GitHub Actions. No Azure DevOps. Just your machines.

Built on top of [DotnetDeployer](https://github.com/SuperJMN/DotnetDeployer).

</div>

---

## Quick Start

> **Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Git](https://git-scm.com/).

**1. Install the CLI**

```bash
dotnet tool install -g DotnetFleet.Tool
```

**2. Start a coordinator**

```bash
fleet coordinator
```

**3. Connect a worker**

```bash
fleet worker --coordinator http://localhost:5000 --token <token>
```

Copy the token from the coordinator banner. That's it — you have a working CI/CD pipeline. Add projects from the [Desktop App](#desktop-app), trigger deploys, and watch live logs.

---

## Table of Contents

- [How It Works](#how-it-works)
- [Installation](#installation)
  - [CLI Tool](#cli-tool)
  - [Desktop App](#desktop-app)
- [Getting Started](#getting-started)
  - [Step 1 — Start the Coordinator](#step-1--start-the-coordinator)
  - [Step 2 — Connect Workers](#step-2--connect-workers)
  - [Step 3 — Add Projects and Deploy](#step-3--add-projects-and-deploy)
- [Production Setup](#production-setup)
  - [Install as systemd Services](#install-as-systemd-services)
  - [Custom Storage Paths](#custom-storage-paths)
  - [Security](#security)
- [CLI Reference](#cli-reference)
- [Configuration](#configuration)
- [Architecture](#architecture)
- [Coordinator ↔ Worker Protocol](#coordinator--worker-protocol)
- [API Reference](#api-reference)
- [Development](#development)
- [Running Tests](#running-tests)

---

## How It Works

```
                    You add a project
                          │
                          ▼
               ┌─────────────────────┐
               │    Coordinator      │  Queues jobs, streams logs,
               │    (fleet coord.)   │  auto-polls for new commits
               └────────┬────────────┘
                        │
              ┌─────────┼─────────┐
              ▼         ▼         ▼
          ┌────────┐┌────────┐┌────────┐
          │Worker 1││Worker 2││Worker N│  Clone/fetch repo,
          └────────┘└────────┘└────────┘  run DotnetDeployer,
                                          stream logs back
```

1. **Add a project** — paste a Git URL and branch in the Desktop App (or call the API).
2. **Trigger a deploy** — click *Deploy Now* or let automatic polling detect new commits.
3. **A worker claims the job** — atomic claim ensures only one worker wins, even under contention.
4. **Clone and deploy** — the worker clones (or fetches) the repo with full history, then runs `dnx dotnetdeployer.tool -y`.
5. **Live logs** — the coordinator streams log lines via SSE; the Desktop App shows them in real time.
6. **Repo cache** — cloned repos stay on disk for faster subsequent deploys; an LRU policy prevents disk exhaustion.

### Requirements for Target Repos

Each repo you deploy must contain a **`deployer.yaml`** at the root. See the [DotnetDeployer docs](https://github.com/SuperJMN/DotnetDeployer) for the format.

> The worker runs `dnx dotnetdeployer.tool -y` — .NET 10's `dnx` downloads and caches the tool automatically. No global install or tool manifest needed.

---

## Installation

### CLI Tool

```bash
dotnet tool install -g DotnetFleet.Tool
```

This gives you the `fleet` command. Verify with:

```bash
fleet version
```

To update later:

```bash
dotnet tool update -g DotnetFleet.Tool
```

### Desktop App

Download the latest release for your platform from [GitHub Releases](https://github.com/SuperJMN/DotnetFleet/releases):

| Platform | Format |
|---|---|
| Linux x64 / arm64 | AppImage |
| Linux x64 | .deb |
| Windows x64 | Installer (.exe) |
| macOS x64 / arm64 | .dmg |

Or build from source:

```bash
cd src/DotnetFleet.Desktop
dotnet run
```

---

## Getting Started

### Step 1 — Start the Coordinator

```bash
fleet coordinator --port 5000
```

On first run the coordinator auto-generates a **JWT secret** and a **registration token**, saves them to `~/.fleet/coordinator/config.json`, and prints a banner:

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

Secrets are generated once and reused across restarts — you'll always get the same token unless you explicitly override it.

### Step 2 — Connect Workers

Copy the command from the coordinator banner and run it **on the same machine or any other machine on the network**:

```bash
fleet worker --coordinator http://myserver:5000 --token <token>
```

The worker self-registers on first run, persists its credentials to `~/.fleet/worker-<hostname>/worker.json`, and immediately starts polling for jobs.

**Scale out** by adding more workers — each one is an independent process:

```bash
fleet worker --coordinator http://myserver:5000 --token <token> --name build-01
fleet worker --coordinator http://myserver:5000 --token <token> --name build-02
fleet worker --coordinator http://myserver:5000 --token <token> --name build-03
```

> The `--token` is only needed on first run. After registration the worker authenticates with its own credentials.

### Step 3 — Add Projects and Deploy

1. Open the **Desktop App** and connect to `http://localhost:5000`.
2. Log in with `admin` / `admin`.
3. Add a project with a Git URL (e.g., `https://github.com/you/your-app.git`) and branch.
4. Click **Deploy Now** — watch live logs as the worker clones the repo and runs DotnetDeployer.

To auto-deploy on new commits, set a **polling interval** (in minutes) on the project. The coordinator checks for new commits with `git ls-remote` and enqueues a deploy whenever the SHA changes.

---

## Production Setup

### Install as systemd Services

For production, install the coordinator and workers as **systemd services** so they start on boot and restart on failure:

```bash
# Install the coordinator
sudo fleet coordinator install --port 5000
#  → Creates and enables fleet-coordinator.service
#  → Prints the registration token

# Install a worker
sudo fleet worker install \
  --coordinator http://myserver:5000 \
  --token <token> \
  --name build-01

# Check status
sudo fleet coordinator status
sudo fleet worker status --name build-01

# View logs
journalctl -u fleet-coordinator -f
journalctl -u fleet-worker-build-01 -f

# Uninstall when needed
sudo fleet coordinator uninstall
sudo fleet worker uninstall --name build-01
```

The services run as the calling user (via `SUDO_USER`), write unit files to `/etc/systemd/system/`, and use these service names:

| Component | Service name |
|---|---|
| Coordinator | `fleet-coordinator` |
| Worker | `fleet-worker-{name}` |

> **Note:** Service installation requires Linux with systemd. On other platforms, use the foreground commands with a process manager of your choice (e.g., `launchd`, `sc.exe`, or Docker).

### Custom Storage Paths

By default, each component stores data under `~/.fleet/`:

| Component | Default path |
|---|---|
| Coordinator | `~/.fleet/coordinator/` |
| Worker `build-01` | `~/.fleet/worker-build-01/` |
| Cloned repos | `<worker-data-dir>/fleet-repos/` |

You can redirect **everything** to a different location with `--data-dir`:

```bash
# Store repos on an external drive
sudo fleet worker install \
  --coordinator http://myserver:5000 \
  --token <token> \
  --name build-01 \
  --data-dir /mnt/external-drive/fleet
#  → Repos will be cloned to /mnt/external-drive/fleet/fleet-repos/
```

For even more control, set `Worker:RepoStoragePath` to an **absolute path** in `appsettings.json` — the repos will go directly there, regardless of `--data-dir`:

```json
{
  "Worker": {
    "RepoStoragePath": "/mnt/external-drive/repos"
  }
}
```

You can also set a disk budget per worker:

```bash
fleet worker --coordinator http://myserver:5000 --token <token> --max-disk 50
```

When total cached repo size exceeds the limit (in GB), the worker automatically evicts the least-recently-used repos.

### Security

| Topic | Details |
|---|---|
| **Admin password** | Default is `admin`. Change it: `fleet coordinator --admin-password <pass>` |
| **JWT secret** | Auto-generated on first run, stored in `config.json`. Override: `--jwt-secret <secret>` |
| **Registration token** | Shared out-of-band with workers. Rotate it by restarting the coordinator with `--token <new-token>` |
| **Worker auth** | Each worker gets a unique `(id, secret)` pair at registration. The JWT carries a `worker_id` claim, and every endpoint verifies it matches — a stolen token cannot act on behalf of another worker |
| **HTTPS** | Use a reverse proxy (nginx, Caddy) to terminate TLS in front of the coordinator |

---

## CLI Reference

```
fleet coordinator [options]            Run coordinator in foreground
fleet coordinator install [options]    Install as systemd service (sudo)
fleet coordinator uninstall            Remove systemd service (sudo)
fleet coordinator status               Show service status

  --port, -p <port>             HTTP port (default: 5000)
  --data-dir <path>             Data directory (default: ~/.fleet/coordinator)
  --token, -t <token>           Registration token (auto-generated)
  --jwt-secret <secret>         JWT signing secret (auto-generated)
  --admin-password <pass>       Admin password (default: admin)
  --urls <urls>                 ASP.NET Core URLs override

fleet worker [options]                 Run worker in foreground
fleet worker install [options]         Install as systemd service (sudo)
fleet worker uninstall [--name <n>]    Remove systemd service (sudo)
fleet worker status [--name <n>]       Show service status

  --coordinator, -c <url>       Coordinator URL (required)
  --token, -t <token>           Registration token (required on first run)
  --name, -n <name>             Worker display name (default: hostname)
  --data-dir <path>             Data directory (default: ~/.fleet/worker-{name})
  --poll-interval <secs>        Polling interval in seconds (default: 10)
  --max-disk <gb>               Max disk usage in GB (default: 10)

fleet version                          Show version
```

---

## Configuration

All settings can be provided via CLI flags, `appsettings.json`, or **environment variables** (using `__` as separator, e.g., `Worker__PollIntervalSeconds=5`).

### Coordinator

| Setting | Default | Description |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | `Data Source=fleet.db` | SQLite connection string |
| `Jwt:Secret` | *(auto-generated)* | JWT signing secret — **change in production** |
| `Jwt:Issuer` / `Jwt:Audience` | `DotnetFleet` | JWT issuer / audience |
| `Workers:RegistrationToken` | *(auto-generated)* | Shared secret for worker self-registration |

### Worker

| Setting | Default | Description |
|---|---|---|
| `Worker:CoordinatorBaseUrl` | `http://localhost:5000` | Coordinator URL |
| `Worker:RegistrationToken` | *(unset)* | Used only for first-time self-registration |
| `Worker:Id` / `Worker:Secret` | *(from credentials file)* | Skip self-registration if both provided |
| `Worker:CredentialsFile` | `worker.json` | Where to persist credentials after registration |
| `Worker:Name` | `Environment.MachineName` | Display name shown in the workers list |
| `Worker:PollIntervalSeconds` | `10` | Queue polling cadence |
| `Worker:HeartbeatIntervalSeconds` | `30` | Heartbeat cadence |
| `Worker:RepoStoragePath` | `fleet-repos` | Where cloned repos live (relative to data-dir, or absolute) |
| `Worker:MaxDiskUsageBytes` | `10 GiB` | LRU eviction threshold |

---

## Architecture

```
┌─────────────────────────────────────────────────────┐
│              DotnetFleet.Coordinator                 │
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

Coordinator and Workers are **separate processes**. You run one or more `DotnetFleet.Worker` instances and they connect to the coordinator over HTTP.

### Components

| Project | Role |
|---|---|
| `DotnetFleet.Tool` | **CLI tool** (`fleet`) — install via `dotnet tool install -g DotnetFleet.Tool` |
| `DotnetFleet.Core` | Domain models, interfaces, enums |
| `DotnetFleet.Coordinator` | ASP.NET Core API + queue + polling + EF Core SQLite |
| `DotnetFleet.Worker` | Standalone Generic Host: git clone/fetch + `dnx dotnetdeployer.tool -y` |
| `DotnetFleet.Api.Client` | Typed HTTP client used by the Desktop App |
| `DotnetFleet` | Avalonia 12 shared ViewModels + Views |
| `DotnetFleet.Desktop` | Desktop host (Windows / Linux / macOS) |
| `DotnetFleet.Browser` | WebAssembly host (optional) |
| `DotnetFleet.Tests` | xUnit unit tests |

### Worker Lifecycle

Each worker runs an independent loop:

1. **Bootstrap** — load credentials from config / credentials file, or self-register against the coordinator.
2. **Login** — exchange `(workerId, secret)` for a JWT (cached, refreshed on 401 or near-expiry).
3. **Heartbeat** — `POST /heartbeat` on a timer so the coordinator knows it's alive.
4. **Poll** — `POST /api/queue/next` every `PollIntervalSeconds`; claims are atomic.
5. **Execute** — clone or fetch with full history, then run `dnx dotnetdeployer.tool -y`.
6. **Stream** — stdout/stderr line by line, report final status.

### Disk Management

Cached repos live under the worker's `RepoStoragePath`. When total disk usage exceeds `MaxDiskUsageBytes`, the worker evicts least-recently-used repos until enough space is reclaimed. Limits are configurable per worker via the Desktop App's *Workers* tab or `--max-disk`.

---

## Coordinator ↔ Worker Protocol

All worker traffic flows over plain HTTP/JSON. There is no direct DB or queue access from the worker side.

| Step | Endpoint | Auth | Purpose |
|---|---|---|---|
| 1. Register | `POST /api/workers/register` | `X-Registration-Token` header *or* Admin JWT | Issues `{workerId, secret}` once |
| 2. Login | `POST /api/workers/login` | anonymous (body has id+secret) | Returns a Worker JWT |
| 3. Heartbeat | `POST /api/workers/{id}/heartbeat` | Worker JWT | Marks the worker Online |
| 4. Claim | `POST /api/queue/next` | Worker JWT | Atomically claims the oldest queued job |
| 5. Logs | `POST /api/jobs/{id}/logs` | Worker JWT | Stream log lines back |
| 6. Status | `POST /api/jobs/{id}/status` | Worker JWT | Report job completion |

---

## API Reference

| Method | Path | Auth | Description |
|---|---|---|---|
| `POST` | `/api/auth/login` | anonymous | Obtain User JWT |
| `GET` / `POST` / `DELETE` | `/api/projects[/...]` | User | CRUD projects |
| `POST` | `/api/projects/{id}/deploy` | User | Trigger deploy |
| `GET` | `/api/jobs/{id}/logs` | User | Live log stream (SSE) |
| `GET` | `/api/workers` | Admin | List workers |
| `PUT` | `/api/workers/{id}/config` | Admin | Update worker config |
| `POST` | `/api/workers/register` | RegistrationToken / Admin | Worker self-registration |
| `POST` | `/api/workers/login` | anonymous | Worker login |
| `POST` | `/api/workers/{id}/heartbeat` | Worker | Liveness ping |
| `POST` | `/api/queue/next` | Worker | Atomic job claim |

---

## Development

### From Source

```bash
# Coordinator
cd src/DotnetFleet.Coordinator
dotnet run

# Worker (separate terminal)
cd src/DotnetFleet.Worker
export Worker__CoordinatorBaseUrl=http://localhost:5000
export Worker__RegistrationToken=change-me
dotnet run

# Desktop App
cd src/DotnetFleet.Desktop
dotnet run
```

### One-Liner (everything at once)

```bash
./scripts/run-fleet.sh              # 1 coordinator + 1 worker + Desktop App
WORKERS=3 ./scripts/run-fleet.sh    # 1 coordinator + 3 workers + Desktop App
NO_GUI=1 ./scripts/run-fleet.sh     # headless (no Desktop App)
```

---

## Running Tests

```bash
dotnet test
```
