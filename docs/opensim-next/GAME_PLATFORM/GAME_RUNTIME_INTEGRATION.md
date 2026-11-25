# Game Runtime Integration — W6

Status: W5 process-isolation prototype verified on Linux and Windows; W6 integration contract and implementation gates defined  
Target branch: `opensim-next-dev`  
Date: 2026-11-25

## 1. Purpose

W6 connects creator-owned immutable Game Project revisions to live Game Instances. The Game Runtime is the authority for instance lifecycle and gameplay policy. A Game Physics Worker is a private child process owned by one Game Instance and is not itself a Region, Scene, project revision, or player session.

W6 must preserve the existing OpenSim Region lifecycle and physics behavior. It must not create a fake `Scene`, register a synthetic Region, or modify the existing Region physics engine path.

## 2. Core ownership boundaries

- `IGameProjectService` owns creator authorization, project metadata, immutable revision retrieval, and manifest validation.
- `IGameRuntimeService` owns Game Instance authorization and lifecycle.
- `GamePhysicsWorkerSupervisor` owns one worker process, local IPC, deadlines, health checks, request correlation, and process cleanup.
- The Worker owns one live physics world and its backend resources.
- The Game Runtime owns players, permissions, game rules, scoring, rewards, economy, moderation, and Viewer interest management/replication.

A project revision is declarative and immutable. Starting an instance references a specific project ID and revision number; it does not mutate or copy the revision. A live instance is ephemeral in W6 and is not persisted as project data.

## 3. Instance lifecycle

Initial state machine:

```text
Starting -> Running -> Stopping -> Stopped
    |          |           |
    +----------+-----------+----> Faulted
```

Rules:

1. Only the authenticated project owner may start, inspect, or stop their instance in the first release.
2. The service must retrieve the project and revision through `IGameProjectService`; client-supplied owner IDs are never trusted.
3. Reject missing, foreign-owned, archived, or invalid revisions before starting a process.
4. Re-parse and validate the stored manifest at runtime. Storage content is not trusted merely because it passed validation when originally saved.
5. Resolve `Settings.DefaultWorldID` to exactly one world in the selected revision.
6. Generate a new host-owned Game Instance ID and physics World ID. Never accept native handles or world IDs from Creator input.
7. Reserve the instance in the host registry before launching the worker so concurrent start requests cannot create duplicate records for the same reservation.
8. Start the Worker, complete the authenticated handshake, send `CreateWorld`, then send `StartSimulation`. Mark the instance Running only after both operations succeed.
9. If startup fails at any point, terminate and reap the child, clean up IPC resources, release the reservation, and return a bounded WorkerUnavailable error.
10. Stop transitions to Stopping, asks the worker to stop the world and exit, and enforces a deadline. On timeout, kill and reap the child and record Faulted; do not report a successful graceful stop.
11. A heartbeat/process failure transitions the instance to Faulted and prevents new physics commands or stale event application.
12. Stop and dispose are idempotent. The host must not hold its global registry lock while awaiting IPC or process exit.

## 4. Worker world creation mapping

W6 uses the Balanced profile by default. Creator-authored project data cannot raise worker safety ceilings or select arbitrary native backends.

Initial `CreateWorld` payload is derived from trusted server-side data:

- schema version;
- host-generated Game Instance ID;
- project ID and immutable revision number for diagnostics;
- resolved default world ID, dimensions, and bounded world metadata;
- Balanced profile, fixed 30 Hz timestep, and default gravity;
- worker-enforced body, event, and catch-up limits.

Do not pass raw asset contents, arbitrary URLs, scripts, creator API keys, payment credentials, or database connection strings to the worker. Asset resolution and static geometry baking remain separate future work. W6 does not instantiate all project assets as physics bodies.

## 5. Runtime API contract

The first service surface should be deliberately small:

- `StartInstanceAsync(authenticatedOwnerID, projectID, revision)`
- `StopInstanceAsync(authenticatedOwnerID, instanceID)`
- `GetInstanceAsync(authenticatedOwnerID, instanceID)`
- `GetOwnedInstancesAsync(authenticatedOwnerID, offset, limit)`

Return typed errors. Do not expose worker process IDs, IPC endpoint names, capability tokens, native backend details, or raw stack traces to Creator clients.

Initial live instance registry is in-memory. A host restart invalidates all live instances; W6 does not promise transparent recovery or durable live-state restoration. Project revisions remain durable and independent.

## 6. Build and deployment integration

The OpenSim solution is generated from `prebuild.xml` with `net8_0`. The standalone Worker Protocol and Supervisor are also .NET 8. W6 may therefore add them to the Prebuild dependency graph rather than introducing a cross-runtime bridge.

Required deployment artifacts:

- `GamePhysicsWorker.Protocol.dll`
- `GamePhysicsWorker.Supervisor.dll`
- `GamePhysicsWorker.dll` and its runtime dependencies

The worker assembly path is server configuration, not Creator input. The Worker is launched through `dotnet` with a restricted local IPC endpoint. Continue to use Windows current-user-only Named Pipes and owner-only Unix socket directories; no public TCP listener.

## 7. Configuration

Initial configuration keys:

- `GameRuntimeService.Enabled` — default false until explicitly configured.
- `GameRuntimeService.WorkerAssemblyPath` — server-owned absolute or base-directory-relative path.
- `GameRuntimeService.StartupTimeoutSeconds`
- `GameRuntimeService.RequestTimeoutSeconds`
- `GameRuntimeService.ShutdownTimeoutSeconds`
- `GameRuntimeService.HeartbeatIntervalSeconds`
- `GameRuntimeService.HeartbeatTimeoutSeconds`

Reject missing worker artifacts and invalid timeout values during service initialization. Do not silently fall back to an in-process physics backend.

## 8. W6 implementation gates

### W6-A — Contracts and validation
- Define typed Game Instance state, result, error, and service contracts.
- Test owner-only access and project/revision resolution.
- Re-validate stored manifest and resolve the default world without creating a Region or Scene.

### W6-B — Worker lifecycle adapter
- Add a narrow factory/session boundary around the existing Worker Supervisor.
- Add the Worker Protocol, Worker, and Supervisor projects to the Prebuild dependency graph.
- Configure worker artifact path and bounded lifecycle deadlines.
- Test worker startup, CreateWorld, StopWorld, process exit, and cleanup through the service.

### W6-C — Runtime registry and failure propagation
- Add concurrent-safe in-memory instance registry and per-instance lifecycle synchronization.
- Test simultaneous start/stop, duplicate reservation, startup failure rollback, heartbeat failure, and worker crash.
- Ensure worker A failure cannot mutate worker B state.

### W6-D — Service composition and acceptance
- Register the service through the existing OpenSim service configuration pattern.
- Keep disabled by default until explicitly enabled.
- Run the full Linux and Windows CI matrix.
- Verify existing Region/Scene physics behavior and SL login compatibility remain unchanged.

No W6 gate is considered complete merely because the standalone Worker tests pass. The runtime service must be exercised through its own integration runner.


## 7. W6 implementation and verification status

- [x] Runtime service implementation with owner/revision validation and runtime manifest re-validation.
- [x] Host-generated instance/world IDs and duplicate-start reservation.
- [x] Worker factory adapter over the existing process supervisor.
- [x] Worker world creation followed by explicit fixed-step simulation start.
- [x] Typed start failures and startup rollback.
- [x] Service-level tests for ownership, archived projects, malformed manifests, duplicate starts, stop/restart, worker faults, and startup rollback.
- [x] Linux and Windows CI for the existing service-level test runner.
- [x] Real child-process acceptance test: authenticated launch, world creation, simulation start, graceful stop, and process reap (Linux CI).
- [x] Config-driven service plugin and CI deployment bundle.
- [ ] Automatic host service-registry/API endpoint registration and Creator-facing access path.
- [x] Fault-stop cleanup, typed stop errors, and faulted child-process reaping tests.
- [ ] Full host-shutdown acceptance test with multiple live instances.

The real-process acceptance runner uses the production Worker executable and Supervisor over the platform IPC transport. Linux child-process lifecycle passed; Windows Named Pipes supervisor tests also passed. The standalone runtime assembly is intentionally not yet registered as a default OpenSim service. It remains disabled unless explicitly constructed with `GameRuntimeOptions.Enabled = true`; service registration follows after deployment paths and shutdown ownership are proven.

## 8. Runtime configuration and failure behavior

The standalone runtime accepts these bounded host settings: `Enabled` (default `false`), `WorkerAssemblyPath` (required only when enabled), startup/request/shutdown deadlines, heartbeat interval/timeout, and `MaximumActiveInstances` (default 16, maximum 256). A disabled runtime can be constructed without a worker deployment path and returns `WorkerUnavailable` for start requests.

A graceful stop returns a typed `WorkerUnavailable` result if the worker cannot stop cleanly; the instance is marked `Faulted`, its reservation is released, and the session is disposed. Caller cancellation remains cancellation (rather than being converted to a success or generic error) after cleanup is attempted. Faulted child processes are explicitly killed and awaited during supervisor disposal.

## 9. Deployment bundle

CI publishes a `opensim-next-game-runtime` artifact containing `GameRuntime.dll`, the Supervisor and Protocol assemblies, required OpenSim contract assemblies, and a `worker/` directory with the Worker executable and its .NET runtime metadata. Configure `WorkerAssemblyPath` to the absolute path of `worker/GamePhysicsWorker.dll` under the extracted bundle directory. Keep the bundle directory private to the OpenSim service identity; the Worker IPC endpoint remains local and is not a public listener.

### Config-driven service composition

`GameRuntimeServicePlugin` is the config-driven `IGameRuntimeService` entry point. It accepts `IConfigSource`, reads `[GameRuntimeService]`, and initializes `GameProjectService` plus the Worker factory only when enabled. The disabled default does not initialize project storage or launch child processes.

Example configuration (keep disabled until the deployment bundle and service access path are deliberately enabled):

```ini
[GameRuntimeService]
Enabled = false
WorkerAssemblyPath = "worker/GamePhysicsWorker.dll"
StartupTimeoutSeconds = 15
RequestTimeoutSeconds = 5
ShutdownTimeoutSeconds = 10
HeartbeatIntervalSeconds = 5
HeartbeatTimeoutSeconds = 3
MaximumActiveInstances = 16
```
