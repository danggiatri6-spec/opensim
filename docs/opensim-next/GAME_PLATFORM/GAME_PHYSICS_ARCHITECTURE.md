# OpenSim-next Game Physics Architecture

Status: Approved architecture baseline — Standard (Balanced) is the default profile
Branch: opensim-next-dev

## 1. Decision

OpenSim-next Game Runtime uses **Standard (Balanced)** as the default physics profile. The system is additive: it must not silently change existing OpenSim/Second Life Region physics, object behavior, LSL/OSSL behavior, or compatibility.

The Game Runtime owns game-instance physics state. Existing OpenSim Region physics continues to serve existing Regions. A game instance must not share mutable physics state with an unrelated Region or another game instance.

## 2. Goals and non-goals

Goals:
- Creator-friendly gravity, rigid bodies, collision response, friction, restitution, damping, triggers, kinematic objects, raycasts, and common constraints.
- Later vehicle and character movement capabilities.
- Server-authoritative gameplay outcomes.
- Low Viewer hardware requirements through interpolation and presentation-only prediction.
- Explicit CPU, memory, object, collision-complexity, and network budgets per instance.

Non-goals for the first milestone:
- Replacing OpenSim physics engines or rewriting Bullet/ODE.
- Deterministic lockstep or bit-identical simulation across platforms.
- Making every decorative object dynamic.
- Arbitrary creator native code in the simulator.
- Changing legacy Region defaults or implementing cloud rendering.

## 3. Existing OpenSim foundation and critical integration constraint

The active source tree contains:
- `OpenSim/Region/PhysicsModules/SharedBase/PhysicsScene.cs`: shared physics scene abstraction.
- `OpenSim/Region/PhysicsModules/BulletS/BSScene.cs`: BulletSim implementation with a Bullet world, constraints, simulation-step state, and a physics thread.
- `OpenSim/Region/PhysicsModules/ubOde/ODEScene.cs`: ODE implementation.
- `OpenSim/Region/PhysicsModules/BasicPhysics/BasicPhysicsScene.cs`: explicitly incomplete; suitable for regression/testing only, not production gameplay.

These implementations are currently integrated as Region modules and receive a Region Scene during module lifecycle. Their existence does **not** prove they can safely create arbitrary independent Game Instance worlds.

Before choosing an integration, inspect engine construction, native resource ownership, module lifecycle, thread ownership, collision callbacks, terrain/meshing dependencies, and disposal semantics.

The first implementation task is a focused engine-isolation spike. Do not instantiate a Region-bound `PhysicsScene` directly as a game world and do not register a game instance as a fake live Region.

Preferred investigation order:
1. Determine whether BulletS can expose an independently owned world with safe lifecycle behind a narrow adapter.
2. If not, determine whether a dedicated Game Runtime worker can host one genuine isolated Region/Scene per game instance using supported module lifecycle and explicit quotas.
3. Evaluate ODE using the same criteria.
4. Keep the engine replaceable; project manifests must not contain Bullet/ODE-specific types.

No engine choice is final until the spike proves lifecycle isolation and CI/build compatibility.

## 4. Runtime architecture

Components:
- **Game Instance Host**: creates, starts, pauses, stops, and disposes instances; assigns immutable instance IDs and physics budgets.
- **Game Physics World**: isolated simulation world per running instance; owns bodies, shapes, constraints, broadphase/contact state, and simulation clock.
- **Physics Engine Adapter**: translates engine-neutral commands to the selected backend and normalizes contacts/raycast results.
- **Physics Command Queue**: bounded queue; commands apply at simulation boundaries, never directly from HTTP or Viewer threads.
- **Collision/Event Dispatcher**: normalizes and bounds contact/trigger events; never runs arbitrary user code in engine callbacks.
- **Game Rules Runtime**: consumes events and makes authoritative decisions (score, damage, pickup, checkpoint, round state).
- **Replication Layer**: publishes authoritative transforms and selected events to subscribed Viewers.
- **Creator Physics Configuration**: declarative, versioned manifest settings; no backend handles or executable code.
- **Budget Monitor**: tracks step duration, body/contact counts, queue depth, dropped/deferred work, and replication volume.

Data flow:
1. Creator saves a validated project revision with world geometry and physics profile.
2. Instance Host resolves assets and creates an isolated physics world.
3. Runtime builds static collision geometry, then dynamic/kinematic bodies.
4. Game logic enqueues validated commands (impulse, target velocity, enable/disable, teleport).
5. At each fixed step, the world applies commands, advances the engine, and collects contacts.
6. Dispatcher normalizes bounded contact/trigger events.
7. Game Rules Runtime validates consequences and updates authoritative state.
8. Replication sends snapshots/events; Viewer interpolates and renders effects.

## 5. Engine-neutral contracts

Introduce small contracts without exposing Bullet/ODE classes:
- `IGamePhysicsWorldFactory`: create a world from validated instance configuration.
- `IGamePhysicsWorld`: step, pause, resume, dispose; create/remove bodies; apply forces/impulses; raycast; create triggers/constraints; read snapshots.
- `IGamePhysicsBody`: stable instance-local body ID and bounded state access.
- `IGamePhysicsEngineAdapter`: backend identity, capabilities, and world factory.
- `GamePhysicsWorldConfig`: profile, gravity, fixed step, limits, collision matrix.
- `GamePhysicsBodyDefinition`: body mode, shape, mass, material, transform, collision group.
- `GamePhysicsContactEvent`: instance ID, body IDs, contact point/normal/impulse, phase, simulation tick.
- `GamePhysicsSnapshot`: tick, timestamp, transforms, velocities, sleep state.

Use repository-native UUID/vector/quaternion types where appropriate, but keep backend handles private. IDs are scoped to an instance. Never trust a client body ID without checking instance ownership.

Reject NaN/infinity, invalid quaternions, negative mass, unsupported shapes, oversized mesh data, and out-of-world coordinates.

## 6. Standard (Balanced) default profile

These are initial engineering targets, not measured performance claims. Tune them with CI and representative low-cost server benchmarks.

| Setting | Initial target | Notes |
|---|---:|---|
| Simulation | Fixed 30 Hz | Independent of Viewer render rate |
| Maximum catch-up | 3 steps/update | Avoid unbounded spiral of death |
| Gravity | 9.81 m/s² downward | Bounded per-world override |
| Dynamic bodies | 128 / instance | Initial quota; static geometry excluded |
| Active constraints | 32 / instance | Initial quota |
| Trigger overlap pairs | 256 / step | Bounded and observable |
| Collision events | 256 / step | Coalesce repeated stay events |
| Snapshot rate | Up to 15 Hz | Interest-managed |
| Shapes | Box, sphere, capsule, convex hull | Triangle mesh mainly static |
| Sleeping | Enabled | Avoid idle work |

On budget exhaustion, reject new bodies/constraints or reduce non-critical update frequency by explicit policy. Never silently discard authoritative collision outcomes.

Profiles:
- **Lightweight**: reduced body/constraint quotas and simpler shapes.
- **Balanced (default)**: standard rigid-body and trigger gameplay within initial budgets.
- **Advanced**: higher quotas and optional vehicle/constraint features only when host capacity allows.

Advanced is not a guarantee of capacity. Admission control may reject launch or require a higher-capacity host. Profiles are server-enforced ceilings, not client quality toggles.

## 7. Feature scope

### P0 — Core interaction
- Static, dynamic, and kinematic bodies.
- Box, sphere, capsule, convex hull collision shapes.
- Gravity, mass, linear/angular velocity, force, impulse, damping.
- Friction and restitution via engine-neutral material presets.
- Collision groups/masks.
- Contact begin/end and bounded contact data.
- Trigger overlap begin/end.
- Filtered raycast.
- Sleeping/wake-up.
- Server-authoritative transforms.

### P1 — Gameplay mechanics
- Hinge, slider, fixed, spring constraints where supported.
- Moving platforms and doors.
- Character controller integration with explicit avatar collision policy.
- Vehicle controller abstraction: acceleration, braking, steering, traction, suspension capability reporting.
- Surface response presets.
- Checkpoint/pickup triggers belong to game rules, not physics-engine responsibilities.

### P2 — Advanced
- More complex vehicle suspension and wheel contact.
- Ragdoll/physical animation experiments.
- Destructible-object approximation using pre-authored fragments.
- Soft-body research only if cost/backend support justify it.

P2 must not block P0 or force backend-specific public schema.

## 8. Creator-facing manifest

Add an optional versioned `physics` object to the project manifest. Illustrative only:

```json
{
  "physics": {
    "profile": "balanced",
    "gravity": { "x": 0, "y": 0, "z": -9.81 },
    "fixedStepHz": 30,
    "maxDynamicBodies": 128,
    "collisionLayers": [
      { "id": "world", "collidesWith": ["player", "vehicle", "projectile"] },
      { "id": "player", "collidesWith": ["world", "player", "vehicle"] }
    ]
  },
  "worlds": [{
    "worldId": "example-world-id",
    "physicsObjects": [{
      "objectId": "example-object-id",
      "bodyMode": "dynamic",
      "shape": { "type": "box" },
      "mass": 10,
      "material": "wood",
      "friction": 0.6,
      "restitution": 0.2
    }]
  }]
}
```

The final schema must follow repository UUID serialization and strict allow-lists. No arbitrary native engine parameters, code, URLs, or file paths. Validate asset permissions and collision mesh complexity server-side.

Physics settings are optional with safe defaults so old manifests remain readable. Schema migration must be explicit and tested.

## 9. Authority, networking, Viewer

- Server owns body creation, collision resolution, impulses, authoritative transforms, trigger state, and game outcomes.
- Viewer input is intent (move/steer/jump), never a claimed final transform or collision result.
- Server validates input rate, acceleration/velocity limits, ownership, and instance membership.
- Viewer interpolates snapshots and may predict local presentation for responsiveness; prediction is never authoritative.
- Reconciliation corrects prediction drift.
- Static geometry is not replicated as a per-tick transform stream.
- Use interest management; send reliable gameplay events separately from high-frequency snapshots.
- Reuse existing OpenSim update paths only when semantics fit. Do not overload legacy SL packet fields with incompatible game meanings.

## 10. Isolation and security

- Unique server-side instance ID and separate physics world per Game Instance.
- Physics callbacks never execute untrusted scripts or AI output.
- Commands enter a bounded queue and are validated before stepping.
- Enforce per-instance CPU, memory, body, constraint, shape complexity, contact, event, and network budgets.
- A slow/crashed physics world must not stall the main Region loop or another instance.
- Engine failure marks instance unhealthy; host may stop/restart from a known revision.
- Disposal releases native resources and joins owned threads before teardown completes.
- Prevent cross-instance body references/events.
- Creator meshes are bounded; prefer convex/simple collision proxies over unrestricted high-poly dynamic meshes.

## 11. Performance strategy

- Fixed-step simulation independent of Viewer frame rate.
- Static geometry batching and broadphase filtering.
- Sleep inactive bodies.
- Prefer primitive/convex shapes; visual meshes are not automatically collision meshes.
- Coalesce non-critical contact-stay events.
- Interest-managed snapshots and adaptive replication frequency.
- Cap catch-up steps and detect overload.
- Collect per-instance telemetry and creator-facing budget warnings.
- Benchmark single- and multi-instance contention before increasing quotas.

## 12. Implementation phases and acceptance gates

### Phase 0 — Backend capability/lifecycle audit
1. Trace PhysicsScene lifecycle and Region module registration.
2. Trace BulletS world construction, stepping, callbacks, native disposal, and thread ownership.
3. Trace ODE lifecycle and resource ownership.
4. Inventory existing raycast, constraints, collision callbacks, materials, and vehicle support.
5. Prototype isolated worlds without changing production Region behavior.

Gate: create/step/contact/dispose two independent worlds in one process; one world's fault/slowdown/disposal cannot corrupt the other; no fake Region registration; no legacy default changes. Document backend limitations and adapter strategy.

### Phase 1 — Contracts and validation
1. Add engine-neutral contracts in a dedicated Game Runtime namespace/project.
2. Add versioned physics profile/body definitions to the manifest.
3. Validate finite transforms, normalized rotations, bounded gravity/mass/dimensions, quotas, collision layer references, and asset permissions.
4. Test old manifests without physics settings.

Gate: invalid/oversized configuration is rejected before engine allocation.

### Phase 2 — Balanced backend adapter
1. Implement one adapter only after Phase 0 proves safe lifecycle.
2. Implement body modes, primitive/convex shapes, fixed stepping, force/impulse, materials, filters, raycasts, sleep.
3. Normalize collision/trigger events.
4. Add deterministic disposal and health monitoring.

Gate: tests for gravity/floor, friction, restitution, kinematic movement, raycast, trigger transitions, and isolation; no existing Region physics regressions.

### Phase 3 — Game Instance integration
1. Bind exactly one physics world to each Game Instance.
2. Load only validated immutable project revisions.
3. Add bounded command queue and server-side event dispatch.
4. Enforce per-instance quotas and admission control.
5. Add stop/restart and resource cleanup.

Gate: concurrent instances remain isolated; stopping one does not affect another; overloaded instance cannot stall main Region loop.

### Phase 4 — Replication and Viewer presentation
1. Define versioned snapshot/event protocol.
2. Implement interest management and bounded update rates.
3. Add Viewer interpolation and local presentation prediction.
4. Reconcile with server-authoritative snapshots.
5. Keep game state separate from legacy SL object update semantics.

Gate: latency/loss tests show bounded correction and no client authority over collisions; low-end Viewer displays motion without authoritative simulation.

### Phase 5 — Creator Studio
1. Add body mode, shape, mass, material, friction, restitution, damping, collision layer controls.
2. Add presets: movable object, bounce ball, moving platform, door, vehicle-ready.
3. Add preview/test mode using a disposable isolated instance.
4. Show budget warnings and backend capability limits.
5. Save physics settings through immutable project revisions.

Gate: non-programmer can create a physics puzzle with moving objects, triggers, and a moving platform.

### Phase 6 — Advanced gameplay
Add constraints, vehicle controller, character movement, and separately evaluated ragdoll/advanced effects. Increase budgets only with multi-instance benchmark evidence.

## 13. Required automated tests

- Two worlds create and step independently.
- Static floor stops falling dynamic body.
- Friction/restitution remain within tolerance.
- Kinematic platform moves without being displaced by dynamic bodies.
- Trigger begin/end emitted once per transition.
- Collision masks prevent configured pairs.
- Raycast filters return eligible hits only.
- NaN/infinity and out-of-range mass/shape rejected.
- Body/constraint/event quotas enforced.
- Client cannot submit authoritative transform or cross-instance body ID.
- One instance disposes while another continues.
- Engine fault is contained and reported.
- Existing Region physics and legacy script behavior unchanged.

Floating-point physics tests use tolerances/invariants, not bit-identical transforms across platforms.

## 14. Repository boundaries

- Keep M1 Game Project service limited to metadata and immutable declarative revisions.
- Put runtime physics contracts/implementation in a dedicated Game Runtime/Physics area.
- Never persist engine objects, native handles, or live simulation state in SQLite project revisions.
- Game Project HTTP endpoints do not create physics worlds.
- Game Instance Host alone allocates/disposes physics worlds.
- Avoid broad `Scene.cs` changes; prefer a separate runtime host and narrow integration interfaces.
- Existing Region physics remains separately configured and tested.

## 15. Immediate next tasks

1. Complete Phase 0 source audit and publish a backend capability matrix.
2. Prototype independent BulletS world lifecycle (or document why a dedicated worker/Region-host strategy is required).
3. Choose the first backend only after prototype and isolation tests.
4. Add contracts and manifest validation after lifecycle is proven.
5. Keep Creator UI and AI/NPC integration out of the critical path until Balanced core passes acceptance tests.
