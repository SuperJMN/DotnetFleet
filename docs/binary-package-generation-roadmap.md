# Roadmap: Binary Package Generation from Fleet

## Goal

Let a user ask Fleet to generate installable binary packages, wait for workers to run DotnetDeployer, and download the resulting files from the Fleet GUI.

Fleet remains responsible for distributed orchestration:

- project registry
- job lifecycle
- worker assignment
- logs and phases
- artifact storage in the coordinator
- GUI download experience

Fleet must not reimplement DotnetDeployer or DotnetPackaging logic.

## Required Upstream Contract

Fleet should depend on a published DotnetDeployer tool that supports:

```bash
dotnetdeployer --package-only \
  --package-project <project-from-deployer-yaml> \
  --package-target <type>:<arch> \
  --output-dir <job-output-dir>
```

The worker should invoke DotnetDeployer as a process, not as an in-process library. Process execution gives Fleet clean cancellation, log capture, environment isolation, and simple tool versioning.

## Target Flow

```text
GUI selects platform / format / architecture
Coordinator records package job(s)
Worker claims job
Worker clones or updates repo
Worker invokes DotnetDeployer package-only into an empty job output folder
Worker uploads generated files to coordinator
Coordinator stores files under project/build folder
GUI lists and downloads artifacts
```

Coordinator storage layout:

```text
<DataDir>/artifacts/
  <project-id>/
    <build-or-job-id>/
      MyApp-1.2.3-win-x64-setup.exe
      MyApp-1.2.3-win-x64.msix
```

## Implementation Phases

Current implementation status:

- Phase 1-4 have a working vertical slice implemented.
- `DotnetDeployer.Tool` 1.0.43 is published to NuGet with `--package-only` and DotnetPackaging 10.1.13.
- Package job inputs are stored in `DeploymentJob.PackageRequestJson`.
- Artifact metadata, including SHA-256, is currently derived from files on disk instead of persisted in a dedicated table.
- Coordinator download routes use authenticated relative paths rather than artifact IDs.
- The GUI uses Zafiro's `IFileSystemPicker` for native save dialogs and streams artifact downloads directly to the selected file.

### Phase 1: Domain and Storage

- Add job kind support:
  - `Deploy`
  - `Package`
- Store package job inputs:
  - package project path from `deployer.yaml`
  - selected targets as `<type>:<arch>`
  - optional batch/build id
- Add artifact metadata:
  - `ProjectId`
  - `JobId`
  - `FileName`
  - `RelativePath`
  - `SizeBytes`
  - `CreatedAt`
- Add coordinator artifact root option, defaulting under `DataDir/artifacts`.

### Phase 2: Coordinator API

- Add endpoint to list packageable projects from `deployer.yaml`.
  - Implemented as `GET /api/projects/{id}/package-projects`.
- Add endpoint to enqueue package jobs:
  - `POST /api/projects/{id}/packages`
- Add endpoints to list and download artifacts:
  - `GET /api/jobs/{id}/artifacts`
  - `GET /api/jobs/{id}/artifacts/{relativePath}`
- Add worker upload endpoint:
  - `POST /api/queue/jobs/{id}/artifacts`
- Ensure finished-job cleanup removes artifact metadata and files.
  - Implemented for package artifact folders under the coordinator artifact root.

### Phase 3: Worker Execution

- Extend `DeployerRunner` so callers can pass arguments.
- For deploy jobs, keep the existing command behavior.
- For package jobs:
  - create an empty output directory for the job
  - invoke DotnetDeployer with `--package-only`
  - pass one or more `--package-target` values
  - upload every generated file from the output directory
  - fail the job if DotnetDeployer succeeds but produces no files
- Keep log streaming and phase parsing unchanged.

### Phase 4: GUI

- Add a `Generate Package` action in project detail.
- Add a selector:
  - package project from `deployer.yaml`
  - platform group
  - format
  - architecture checkboxes
- Supported initial catalog:
  - Windows: `exe-setup`, `exe-sfx`, `msix`
  - Linux: `deb`, `rpm`, `appimage`, `flatpak`
  - macOS: `dmg`
  - Android: `apk`, `aab`
- In job detail, show an artifacts section with:
  - filename
  - size
  - SHA-256
  - download action

### Phase 5: End-to-End Validation

- Publish DotnetDeployer with `--package-only` to NuGet.
  - Done: `DotnetDeployer.Tool` 1.0.43.
- On a worker machine, verify:

```bash
dotnet dnx dotnetdeployer.tool --package-only --help
```

- Run one package job from Fleet and confirm:
  - job logs show DotnetDeployer invocation
  - output files are uploaded to coordinator storage
  - GUI downloads the artifact
  - cancellation still kills the process tree

## Acceptance Criteria

- Fleet can create package jobs without creating deployment jobs.
- Workers remain fungible from Fleet's perspective; capability failures are reported by DotnetDeployer/tooling, not pre-filtered by Fleet.
- The coordinator stores artifacts under project/build-specific folders.
- The GUI can download completed artifacts through authenticated coordinator endpoints.
- Existing deploy jobs keep their current behavior.

## Manual Release Dependency

Fleet should not depend on local DotnetDeployer source for production behavior. Before enabling the end-to-end feature, publish a DotnetDeployer version containing `--package-only` to NuGet.

When GitHub is unavailable but NuGet is available:

1. Pack and push DotnetPackaging if Fleet's target Deployer version depends on new DotnetPackaging packages.
2. Pack and push DotnetDeployer.
3. Verify the tool through NuGet with `dotnet dnx dotnetdeployer.tool --package-only --help`.
4. Implement or enable Fleet's worker integration against that published tool.
