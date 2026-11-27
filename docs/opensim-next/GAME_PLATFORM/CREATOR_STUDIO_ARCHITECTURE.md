# OpenSim-next Creator Studio Architecture

Status: architecture proposal for incremental implementation.

## 1. Design objective

Creator Studio must let a creator build, test, publish, and operate a complete multiplayer game without requiring the creator to understand OpenSim Region deployment. It should borrow the approachable authoring workflow of Roblox Studio while preserving OpenSim's Avatar, asset, scripting, inventory, and Region compatibility.

The platform must not equate a Game Project with a Region.

## 2. Core domain model

| Concept | Responsibility | Runtime meaning |
|---|---|---|
| Game Project | Product identity, owner, permissions, monetization settings, lifecycle | Durable creator-owned aggregate |
| Experience | Logical player-facing game and its entry/flow rules | Groups Places into one game |
| Place | Authored scene/map, spawn points, scene assets, local rules | A versioned scene definition |
| Place Revision | Immutable, validated scene content | Input to publishing and runtime |
| Game Instance | One live multiplayer session for a selected Place revision | Isolated authoritative simulation |
| Region | Legacy OpenSim spatial/hosting unit | Compatibility and world-integration adapter |
| Physics Worker | Isolated physics process for one Game Instance | Owns exactly one physics world in v1 |

A Game Project may contain one or more Experiences; an Experience may contain one or more Places. A Place is a logical authored scene and is not required to be a Region. A Place may be backed by a single Region, a tiled group of Regions, or a standalone game scene. The MVP should support standalone scenes first and provide an explicit Region adapter for legacy integration.

A Game Instance runs a specific immutable Place revision. Multiple Game Instances may run the same revision concurrently. A Game Instance owns its own runtime state and one Worker process/world in the current physics isolation model.

## 3. Separation of authored content and runtime

Creator editing changes a Draft. Saving validates and stores an immutable revision; publishing atomically selects a revision as the published version. Runtime instances pin the revision they started with and must not observe edits made later to the Draft or newer published revisions.

The database stores project metadata, declarative manifests, revision metadata, and references to versioned assets. It must not store live engine objects, native physics handles, process identifiers, IPC capabilities, or mutable simulation state as project definitions.

Runtime state belongs to Game Runtime and the Worker. Player progression and durable game data belong to separately scoped persistence services, not to the immutable Place manifest.

## 4. Creator Studio workspaces

1. **Project Home** — create/open projects, collaborators, permissions, version history, publish status, player analytics.
2. **World Editor** — scene hierarchy, transform tools, terrain, lighting, environment, spawn points, and region/tile boundaries where applicable.
3. **Asset Browser** — owned assets, reusable library assets, import pipeline, dependency/version inspection, and licensing metadata.
4. **Object & Material Editor** — mesh/primitive properties, textures, PBR material channels, LOD, collision shape, and Avatar interaction anchors.
5. **Physics Editor** — static/dynamic/kinematic body, primitive/convex collider, mass, friction, restitution, damping, constraints, triggers, and physics profile.
6. **Logic Editor** — safe event/rule authoring, script editor, permission declarations, and test harness. Existing LSL compatibility is a separate compatibility surface; new game logic must not silently execute arbitrary native code inside the Worker.
7. **UI & Interaction Editor** — in-world prompts, HUD/UI panels, input mappings, dialogue, and accessibility settings.
8. **Test & Publish** — local preview, multiplayer test session, validation report, immutable revision, publish/rollback, and instance management.

The default workspace should prioritize the 3D scene. Hierarchy, properties, assets, and logic panels are contextual and dockable rather than permanently consuming the viewport.

## 5. Authoring workflow

1. Create a Game Project and its first Experience/Place from a template or blank scene.
2. Build the scene and add versioned assets.
3. Configure collision and gameplay physics using the Balanced profile by default.
4. Define game rules, permissions, player entry, spawn points, and persistence requirements.
5. Run validation: schema, asset references, size/bounds, unsupported features, permission declarations, physics budgets, and script safety.
6. Save a Draft revision with optimistic concurrency.
7. Publish atomically. Published revisions are immutable.
8. Start one or more Game Instances from the selected published revision.
9. Monitor health, capacity, errors, and player sessions; stop or roll back without mutating an active revision.

## 6. Region interoperability

Region integration is an adapter, not a mandatory dependency of the Creator domain model.

- **Standalone Place:** runs as a Game Runtime scene and does not create/register a legacy Region.
- **Region-backed Place:** maps a Place to an explicitly configured Region or region group through an adapter.
- **Legacy Region as Place:** allows a creator to package an existing Region-based environment as a Place, subject to compatibility and asset validation.
- **Cross-Place travel:** uses Experience-level routing and explicit transfer contracts; it must not assume that two Places share a process or physics world.

The first implementation should not attempt to make arbitrary multi-Region physics one simulation. A Place that spans tiles needs a later spatial-partitioning design. Until then, the MVP should use one bounded physics world per Game Instance.

## 7. Runtime and security boundaries

- Creator API authenticates the creator and enforces ownership/collaboration permissions.
- Creator Studio never receives Worker capability tokens or host secrets.
- Game Runtime validates the published revision and provisions the Worker.
- Worker owns physics only; it does not own billing, player identity, rewards, game rules, arbitrary network access, or database credentials.
- Game logic executes in a separately sandboxed runtime with explicit capabilities and resource limits.
- Viewer prediction is cosmetic; server-side Game Runtime/Worker results are authoritative for collision, damage, scoring, and rewards.
- Published content and assets are version-pinned for reproducible instance startup.

## 8. Suggested implementation sequence

### C0 — Data model and API contracts
Add Experience, Place, PlaceRevision, and explicit Project-to-Experience-to-Place relationships. Keep existing Game Project APIs backward compatible by automatically creating a default Experience and Place for existing single-world manifests during migration or read adaptation.

### C1 — Manifest and validation
Define versioned Place manifest schema, asset dependency references, world bounds, spawn points, physics settings, and compatibility flags. Validate before saving/publishing.

### C2 — Asset pipeline
Add asset upload/import, content hashing, immutable versions, ownership/licensing metadata, and dependency resolution. Do not embed large binary assets in the project manifest.

### C3 — Creator Studio MVP
Build a scene hierarchy, viewport editing, transform gizmos, property inspector, asset browser, and save/test/publish flow. Keep panels contextual so the world viewport remains primary.

### C4 — Runtime provisioning
Start Game Instances from a published Place revision, isolate each instance, and expose lifecycle/health APIs. Keep Region creation optional.

### C5 — Game logic and UI
Add safe event-driven game logic and UI authoring with explicit permission/capability declarations. Integrate LSL compatibility through a separate adapter rather than coupling it to physics.

### C6 — Region bridge and multi-Place travel
Implement Region-backed Places and Experience-level travel after standalone Place runtime is stable.

## 9. Current implementation status

The first additive C0 contract slice is present in `OpenSim/Services/Interfaces/GameExperienceContracts.cs`:

- `GameExperienceDefinition` and `GamePlaceDefinition` are explicit domain contracts.
- `GameProjectExperienceAdapter` maps a legacy v1 manifest into one default Experience.
- Each legacy World maps to one Place while preserving its WorldID as PlaceID; the existing default world maps to StartPlaceID.
- This is an in-memory compatibility projection only. It does not change persisted v1 JSON, SQLite tables, revision numbering, or runtime provisioning. Durable Experience/Place persistence requires a separately versioned schema migration and API contract. The durable revision graph, SQLite migration constraints, legacy promotion rules, and staged API rollout are specified in [EXPERIENCE_PLACE_PERSISTENCE.md](EXPERIENCE_PLACE_PERSISTENCE.md).

The Game Project test runner verifies the stable-ID mapping and start Place behavior.

## 10. MVP acceptance criteria

- A creator can create a project and a default Place without manually creating a Region.
- A Place can be edited, validated, saved as an immutable revision, and published.
- Two Game Instances can run the same Place revision without sharing simulation state.
- Editing a Draft cannot mutate an already running instance.
- A Place can run without registering a legacy Region or constructing a fake Scene.
- A Region-backed Place can be introduced through an adapter without changing the core Game Project data model.
- The Creator UI can author a basic scene without requiring script knowledge; advanced scripting remains optional.
