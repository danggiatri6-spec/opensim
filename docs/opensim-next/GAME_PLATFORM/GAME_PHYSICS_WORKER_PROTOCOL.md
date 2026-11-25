# Game Physics Worker Protocol

Status: Architecture specification / implementation not yet started  
Target branch: `opensim-next-dev`  
Date: 2026-11-25

## 1. Purpose and non-goals

The Game Physics Worker is an isolated server-side process that owns one live physics world for one Game Instance. It is an implementation boundary beneath the Game Runtime, not a Region, Scene, HTTP service, script host, or general-purpose plugin host.

The first implementation must prioritize crash containment, explicit ownership, bounded resource use, and clean teardown over multi-world throughput.

The worker is responsible for:
- creating and destroying one physics world;
- applying validated body, shape, constraint, force, impulse, and query operations;
- advancing the world at the configured fixed simulation rate;
- producing collision/trigger events and authoritative physics snapshots;
- enforcing per-world physics limits as a second line of defense.

The Game Runtime remains responsible for:
- creator permissions and game rules;
- validating all creator-authored configuration and player commands;
- game state, scoring, inventory, rewards, damage policy, and economy;
- deciding which physics events have gameplay meaning;
- player authentication, interest management, and Viewer replication.

The worker must not:
- create or register an OpenSim Region or `Scene`;
- access the database directly;
- accept arbitrary LSL, C#, native libraries, shell commands, URLs, or creator API keys;
- expose a public network listener;
- make payment, entitlement, reward, or moderation decisions.

## 2. Process and world ownership

Initial topology:

```text
OpenSim host
  Game Runtime / Game Instance supervisor
    Worker supervisor
      Physics Worker process (one Game Instance)
        Physics adapter
          Backend world (initial candidate: BulletS unmanaged API)
```

Rules:

1. One worker process owns exactly one live physics world in the first production prototype.
2. One worker owns its backend handles, simulation thread, command queue, event queue, and native resources.
3. No native pointer, `GCHandle`, engine object, or backend-specific class crosses IPC.
4. The host owns the worker process lifetime. The worker cannot spawn child processes.
5. The worker receives a declarative world description at creation. It does not read creator project files or databases itself.
6. A worker is not reused for another Game Instance after shutdown in the initial version. Process reuse may be considered only after teardown and isolation tests are established.
7. A crashed worker invalidates its live world. Version 1 does not silently claim seamless recovery or deterministic replay.

This boundary deliberately avoids depending on Region lifecycle or passing a fake `Scene` to a physics module.

## 3. IPC transport

### 3.1 Local-only transport

Use OS-local IPC, never a TCP listener:

- Windows: named pipe with an ACL restricted to the OpenSim service identity.
- Linux/macOS: Unix domain socket inside a private runtime directory with owner-only permissions.

The host creates the endpoint before starting the worker and passes only the endpoint identity through a restricted launch mechanism. Do not put authentication secrets in command-line arguments or logs.

The worker must reject:
- connections from any process identity not permitted by the OS endpoint;
- a second simultaneous host connection;
- messages before protocol handshake;
- messages with invalid version, sequence, length, or world identity.

A random per-launch capability token may be used as an additional handshake check. Generate at least 256 bits of entropy, keep it in memory only, compare in constant time, and never log it. OS access control remains mandatory; the token is not a replacement for ACLs.

### 3.2 Framing and encoding

Protocol version 1 uses:
- 4-byte unsigned big-endian frame length;
- UTF-8 JSON payload;
- one JSON envelope per frame;
- no newline-delimited framing;
- no compression in the first prototype.

Initial limits:
- inbound command frame: 1 MiB maximum;
- outbound event frame: 1 MiB maximum;
- outbound snapshot frame: 4 MiB maximum;
- maximum queued commands: 4,096 per world;
- maximum queued gameplay events: 4,096 per world.

These are defensive starting limits, not performance guarantees. Oversized frames are rejected before JSON deserialization. Queue overflow must be explicit: commands are rejected with a capacity error, never silently dropped. Snapshot publication may coalesce stale snapshots; event publication may not silently discard collision/trigger events.

JSON is chosen for inspectability and initial implementation simplicity. A later version may negotiate a compact binary codec without changing the logical message contract.

## 4. Envelope and sequencing

Every message uses a versioned envelope.

Request example:

```json
{
  "protocol": "opensim.game-physics",
  "version": 1,
  "kind": "request",
  "worldId": "7dca2a5d-6f9e-4a16-93f2-3a8e12a8f011",
  "requestId": "8cba3c18-8b8e-4cb6-920b-1f6b1d5d9200",
  "sequence": 12,
  "operation": "CreateWorld",
  "payload": {}
}
```

Response example:

```json
{
  "protocol": "opensim.game-physics",
  "version": 1,
  "kind": "response",
  "worldId": "7dca2a5d-6f9e-4a16-93f2-3a8e12a8f011",
  "requestId": "8cba3c18-8b8e-4cb6-920b-1f6b1d5d9200",
  "sequence": 12,
  "ok": true,
  "error": null,
  "payload": {}
}
```

Requirements:
- `protocol` and `version` are mandatory.
- `worldId` is a host-generated UUID and is fixed for the worker lifetime.
- `requestId` correlates request and response.
- `sequence` is monotonically increasing per host-to-worker connection.
- duplicate or out-of-order command sequences are rejected; commands are not automatically retried by the worker.
- responses echo the request ID and sequence.
- worker-originated events use a separate monotonically increasing `eventSequence`.
- all numeric values must be finite; NaN and infinities are invalid.
- unknown required operations fail with `UnsupportedOperation`; unknown optional metadata may be ignored only where explicitly allowed.

## 5. Worker lifecycle state machine

```text
Starting
  -> Handshaking
  -> Ready
  -> WorldCreating
  -> Running
  -> Stopping
  -> Stopped

Any active state -> Faulted -> process exit
```

The host supervisor is the authority for process state. The worker reports its internal state, but the host does not trust a worker response to prove that the OS process is alive.

Lifecycle operations:

| Operation | Direction | Purpose |
|---|---|---|
| `Hello` | host → worker | protocol negotiation and authenticated handshake |
| `Ready` | worker → host | reports supported protocol/backend capabilities |
| `CreateWorld` | host → worker | create the one world from validated declarative settings |
| `WorldCreated` | worker → host | returns effective profile and world limits |
| `GetHealth` | host → worker | lightweight health and queue-depth report |
| `StopWorld` | host → worker | stop accepting mutations and drain the simulation |
| `WorldStopped` | worker → host | confirms backend teardown completed |
| `Shutdown` | host → worker | exit process after world teardown |
| `Fault` | worker → host | fatal backend or protocol failure |

The host must enforce deadlines for handshake, world creation, health response, world stop, and process exit. On stop timeout, the supervisor terminates the process and records a failed teardown. It must not reuse that process.

## 6. World creation contract

`CreateWorld` carries only validated, bounded, declarative data:

- schema version and Game Instance ID;
- physics profile identifier: `Lightweight`, `Balanced`, or `Advanced`;
- fixed timestep and maximum catch-up steps;
- gravity vector;
- world bounds;
- collision group/mask definitions;
- allowed primitive and convex shape descriptors;
- static geometry descriptors or host-provided opaque asset IDs already resolved by a trusted asset service;
- body, constraint, trigger, and event capacity limits;
- deterministic seed only if a backend feature explicitly supports it; do not promise cross-platform deterministic simulation.

The worker revalidates all fields and applies hard upper bounds regardless of creator settings. Creator values may lower a limit but cannot raise the worker's configured safety ceiling.

Initial Balanced profile targets from `GAME_PHYSICS_ARCHITECTURE.md`:
- 30 Hz fixed simulation;
- at most 3 catch-up steps per host scheduling interval;
- gravity 9.81 m/s² downward by default;
- 128 dynamic rigid bodies per instance, excluding static geometry;
- 32 active constraints;
- 256 trigger overlap pairs per step;
- 256 collision events per step;
- snapshots up to 15 Hz, subject to interest filtering;
- sleeping enabled;
- box, sphere, capsule, and convex hull shapes;
- triangle mesh primarily for static geometry.

These values are initial admission-control targets and must be measured on supported server hardware before production guarantees are published.

## 7. Runtime command model

All mutations enter a bounded command queue and are applied on a simulation tick boundary. The worker's physics simulation thread is the sole owner of backend mutation calls unless a backend adapter explicitly proves a safe alternative.

Initial command families:

| Operation | Meaning |
|---|---|
| `CreateBody` / `DestroyBody` | create or remove a rigid body |
| `SetBodyMode` | static, dynamic, or kinematic |
| `SetTransform` | set an authorized transform |
| `SetVelocity` | set linear/angular velocity |
| `ApplyForce` / `ApplyImpulse` | apply bounded force or impulse |
| `SetMaterial` | friction, restitution, damping |
| `SetCollisionFilter` | collision group and mask |
| `CreateConstraint` / `DestroyConstraint` | supported P0/P1 constraint operations |
| `CreateTrigger` / `DestroyTrigger` | trigger volume lifecycle |
| `Raycast` | bounded query against the current world |
| `SetKinematicTarget` | move a kinematic body |
| `RequestSnapshot` | request an immediate state snapshot |
| `StepBarrier` | test-only deterministic tick barrier |

Every object is addressed by a host-generated opaque ID, not a native pointer. The worker rejects duplicate IDs, stale IDs, references to another world, invalid transforms, unsupported shapes, and capacity overflows.

Commands carry an optional `applyAtTick`. If omitted, the worker applies the command at the next available tick. A command targeting a tick already completed is rejected as stale. The host remains responsible for deciding whether a late player command is acceptable under game rules.

## 8. Simulation scheduling and backpressure

- The worker owns the fixed-step accumulator and simulation cadence.
- The initial Balanced profile targets 30 Hz.
- The worker must cap catch-up work and report when it falls behind; it must not run an unbounded catch-up loop.
- The host must not issue a blocking request for every render frame.
- Snapshots are state updates and may be coalesced: if a newer snapshot is ready, an older unsent snapshot can be replaced.
- Collision and trigger events are gameplay-relevant events and must be sequenced. If the event queue reaches its hard limit, the worker reports an overflow fault and the host marks the instance degraded/failed rather than silently losing events.
- Health telemetry includes simulation tick, simulation duration, queue depths, active body/constraint counts, snapshot age, and dropped/coalesced snapshot count.
- The worker does not decide which clients receive which snapshot. Game Runtime performs interest management and client replication.

## 9. Event and snapshot contracts

Worker events include:
- `CollisionBegin`, `CollisionEnd`;
- `TriggerEnter`, `TriggerExit`;
- `BodySleep`, `BodyWake`;
- `ConstraintBroken` when supported;
- `WorldWarning` and `WorldFault`.

Each event includes world ID, event sequence, simulation tick, object IDs, and bounded event data. Contact arrays are capped. The worker reports raw physics facts; Game Runtime decides damage, score, pickup, quest progress, and other gameplay effects.

Snapshots include:
- world ID, simulation tick, monotonic snapshot sequence, simulation timestamp;
- each interested body's opaque ID, transform, linear/angular velocity, sleep state;
- optional bounded interpolation metadata.

The worker may produce a full snapshot for tests and a filtered snapshot for production. Interest filtering is controlled by Game Runtime; the worker must not infer player identity or visibility permissions.

## 10. Error taxonomy

Stable error codes:

- `ProtocolMismatch`
- `AuthenticationFailed`
- `InvalidEnvelope`
- `InvalidSequence`
- `InvalidWorldState`
- `InvalidArgument`
- `UnsupportedOperation`
- `ObjectNotFound`
- `ObjectAlreadyExists`
- `CapacityExceeded`
- `QueueFull`
- `DeadlineExceeded`
- `BackendUnavailable`
- `BackendOperationFailed`
- `EventOverflow`
- `WorkerStopping`
- `InternalError`

Error responses must not contain native pointers, stack traces, file paths, secrets, or untrusted payload fragments. Detailed diagnostics go to restricted server logs with correlation IDs.

## 11. Crash handling and recovery policy

The host supervisor monitors process exit and heartbeat deadlines.

On worker crash:
1. mark the Game Instance physics state as unavailable;
2. stop accepting player commands that require physics;
3. do not accept stale events or snapshots from the dead process;
4. mark pending commands as failed;
5. notify Game Runtime so it can pause or terminate the game according to creator-configured failure policy;
6. preserve diagnostic correlation IDs and bounded logs;
7. start a replacement worker only as part of an explicit instance recovery flow.

Version 1 does not promise transparent physics recovery. Recreating a world from a project revision alone is not enough to reconstruct its live state. Any future checkpoint/recovery feature must define complete body, constraint, trigger, and game-state checkpoint semantics and must not assume deterministic replay across CPU architectures or backend versions.

## 12. Security and resource isolation

- Run workers under a restricted service identity where supported.
- Apply OS process resource limits: memory, CPU scheduling priority/quota where available, process count, open handles/file descriptors, and wall-clock deadlines.
- Use a private temporary directory; deny unnecessary filesystem access.
- Do not provide network access to the worker in version 1.
- Do not pass database credentials, creator API keys, payment secrets, or user access tokens.
- Validate all IPC data as untrusted input even though the endpoint is local.
- Bound geometry vertex/index counts, shape complexity, body counts, constraint counts, query distance, ray count, and event payload size.
- Reject malformed or excessively expensive mesh data before backend allocation.
- Avoid unbounded logging and never log per-contact details at normal production log levels.
- A worker fault must not crash or stall the OpenSim host process.

Process isolation is not a substitute for host-side validation, nor does it make unsafe native code harmless. It provides a containment boundary for native faults and process-global backend state.

## 13. Observability

Every worker lifecycle has a correlation ID and world ID. Metrics should include:
- worker start and stop duration;
- process exit code and fault category;
- CPU and resident memory;
- simulation tick rate and step duration percentiles;
- catch-up count and lag;
- active bodies, constraints, and triggers;
- command/event queue depth and rejection count;
- snapshot rate and age;
- IPC frame sizes and latency.

Do not include creator secrets, user tokens, or arbitrary asset contents in telemetry.

## 14. Implementation phases and acceptance gates

### W0 — Protocol contracts
- Define typed host/worker envelopes, error codes, and lifecycle states.
- Implement frame codec with strict length limits.
- Unit-test malformed, truncated, oversized, invalid UTF-8, and unknown-version frames.
- No physics backend integration.

### W1 — Process supervisor and echo worker
- Launch and stop a child process through local IPC.
- Authenticate handshake and enforce one host connection.
- Test startup timeout, worker crash, malformed messages, host shutdown, and forced termination.
- Verify no public TCP listener is opened.

### W2 — Backend adapter prototype
- Introduce a narrow `IPhysicsWorldAdapter` contract inside the worker process.
- Start with a fake deterministic adapter to test protocol and lifecycle without native dependencies.
- Then test BulletS unmanaged backend only inside the worker.
- Never pass backend types across the IPC boundary.

### W3 — One-world simulation
- Create one world, add/remove bodies, step at fixed rate, emit collision events and snapshots.
- Test resource ceilings and queue backpressure.
- Verify graceful stop joins all worker-owned threads before backend teardown.

### W4 — Failure containment
- Kill the worker during stepping and during teardown.
- Verify the host remains responsive and marks the instance unavailable.
- Verify no stale worker messages are applied after replacement.
- Verify repeated create/stop cycles do not leak processes, handles, or temporary endpoints.

### W5 — Two-instance isolation
- Run two separate worker processes with different worlds and profiles.
- Destroy or crash worker A while worker B continues stepping and publishing snapshots.
- Measure CPU/memory overhead and startup latency.
- Do not optimize toward multiple worlds per process until this gate passes.

### W6 — Game Runtime integration
- Add a server-side Game Physics service facade.
- Map Game Instance lifecycle to worker lifecycle.
- Keep project revision persistence separate from live physics state.
- Add Viewer replication only after the worker protocol and authoritative event flow are stable.

## 15. Initial acceptance criteria

The first prototype is accepted only when all of the following are demonstrated:

1. Worker and host negotiate the same supported protocol version.
2. A worker can create one world and reject a second world creation request.
3. Invalid, oversized, out-of-order, and unauthorized messages are rejected without process crash.
4. The fake adapter can create bodies, advance ticks, return snapshots, and emit events through IPC.
5. Host shutdown results in worker exit within a defined deadline.
6. Forced worker termination does not terminate or deadlock the host.
7. Worker A can be stopped or killed while worker B continues simulation unaffected.
8. No native pointer or backend object appears in serialized messages.
9. Queue and per-world resource limits are enforced.
10. Existing Region physics behavior is unchanged by the worker prototype.

## 16. Open decisions

The following decisions remain open until a prototype supplies measurements:

- .NET worker executable target and packaging model across Windows/Linux/macOS;
- whether the first IPC codec remains JSON or moves to a compact binary codec after profiling;
- exact OS resource-limit APIs and deployment defaults;
- BulletS backend version and platform availability;
- whether a later worker may host multiple worlds after native isolation is proven;
- whether production should support worker pooling for startup latency while retaining one-world-per-process ownership.

No implementation or runtime test is implied by this specification.
