# OpenSim-next Source Audit — Initial Pass

**Branch reviewed:** `opensim-next-dev`  
**Audit type:** Static source inspection; no build or runtime tests have been executed.  
**Scope:** Login, Grid lookup/registration, Region scene lifecycle, scene graph, Avatar presence, object groups, client protocol adapter, asset service, and build instructions.

## Executive assessment

The current codebase already contains useful service boundaries (for example `ILoginService`, `IGridService`, `IAssetService`, and `IClientAPI`) and should not be treated as a monolith that needs a wholesale rewrite. At the same time, several central implementation classes are very large and combine multiple responsibilities. The safest modernization path is to preserve existing interfaces and behavior, add characterization/integration tests, and extract responsibilities behind adapters incrementally.

No production source file was changed as part of this audit.

## Source evidence

| Area | Source file | Approx. size | Evidence observed | Initial decision |
|---|---|---:|---|---|
| Login | `OpenSim/Services/LLLoginService/LLLoginService.cs` | 55 KB | Implements `ILoginService`; includes `Login`, destination lookup, and agent launch flow | **Keep + wrap**; characterize login flow before changing it |
| Grid directory | `OpenSim/Services/GridService/GridService.cs` | 48 KB | Implements `IGridService`; region registration/deregistration, lookup by UUID/handle/name/URI, neighbours and region ranges | **Keep** service contract; later separate persistence, registration policy and query concerns if tests justify it |
| Asset service | `OpenSim/Services/AssetService/AssetService.cs` | 7 KB | Implements `IAssetService`; get, metadata, data, cache, store, update and delete operations | **Keep**; likely a useful boundary for future storage/cache adapters |
| Region scene | `OpenSim/Region/Framework/Scenes/Scene.cs` | 258 KB | Partial class; lifecycle, module registration, update loop, backup, terrain, storage loading, object creation and ray-cast related methods are present | **Refactor gradually**; do not replace the Scene API wholesale |
| Scene manager | `OpenSim/Region/Framework/Scenes/SceneManager.cs` | 18 KB | Manages multiple scenes, start/close/restart, lookup by region/name/endpoint, and current-scene operations | **Keep** as the region orchestration boundary; test lifecycle and lookup |
| Scene graph | `OpenSim/Region/Framework/Scenes/SceneGraph.cs` | 85 KB | Owns object add/delete operations, presence iteration, object lookup, statistics and object movement updates | **Refactor selectively**; clarify object registry, presence registry and update responsibilities |
| Avatar presence | `OpenSim/Region/Framework/Scenes/ScenePresence.cs` | 269 KB | Implements `IScenePresence`; client event registration, movement, teleport, neighbour-region state, sit/stand and agent updates | **Refactor gradually**; separate movement/state transitions from client-event plumbing only after tests exist |
| Object group | `OpenSim/Region/Framework/Scenes/SceneObjectGroup.cs` | 203 KB | Implements `ISceneObject`; owns parts, attachments, backup hooks, bounds/cost calculations, script state, selection and object operations | **Refactor gradually**; preserve object serialization and identity semantics |
| Client protocol | `OpenSim/Region/ClientStack/Linden/UDP/LLClientView.cs` | 580 KB | Implements several client-facing interfaces; packet handlers plus chat, IM, region crossing/teleport, map, inventory and many outbound message methods | **Highest-priority boundary candidate**; split protocol concerns behind internal collaborators without changing packet semantics |
| Build | `BUILDING.md` | 2.5 KB | Documents .NET 8 SDK; Windows uses `runprebuild.bat` then `compile.bat`; Linux/macOS uses `runprebuild.sh` then `dotnet build` | **Establish reproducible CI build** before source refactoring |

## Key findings

### 1. Preserve the existing service interfaces

The login, grid, and asset implementations already implement explicit interfaces. This is valuable: OpenSim-next should extend these boundaries where needed rather than inventing a parallel service architecture immediately.

### 2. The largest risk is responsibility concentration in core runtime classes

`LLClientView`, `Scene`, `ScenePresence`, and `SceneObjectGroup` are large. File size alone does not prove a defect, but the method surface shows that protocol messaging, scene lifecycle, avatar movement, and object lifecycle have many concerns concentrated in a few classes.

The first extraction target should be the client protocol adapter because it is the boundary between network packets and simulator behavior. Any extraction must preserve packet ordering, reliability, permissions checks, and client-visible behavior.

### 3. Keep rendering out of the authoritative server

The inspected server-side scene and presence classes model simulation state and client communication. The modernization plan should keep GPU rendering, materials, camera effects, and graphics quality in the Viewer. Server-side simulation and permissions must not depend on the Viewer renderer or graphics preset.

### 4. Centralized Grid is compatible with the existing shape

The current Grid service exposes region registration, deregistration, region lookup, neighbour queries, and region-range queries. This gives us a concrete foundation for a centrally managed Grid while retaining separately running Region simulators. Account, land, inventory and social service boundaries still need a broader audit before proposing a final service split.

### 5. Build baseline is a prerequisite

The repository's build guide currently documents .NET 8 SDK and the Prebuild scripts. Before changing core code, add a CI workflow that runs the documented generation/build steps on a supported environment, and record the exact source revision and build result. The user's legacy Windows 7 PC should not be treated as the required build machine for this modern .NET toolchain.

## Recommended order of work

1. **Build baseline:** add CI for the documented .NET 8 build; capture warnings and failures without changing source behavior.
2. **Characterization tests:** cover login success/failure, region registration and lookup, region start/stop, avatar entry/movement/teleport, object create/update/delete, and asset fetch/store.
3. **Protocol boundary:** map `LLClientView` packet-handler registration and outbound message groups; propose internal collaborators, but do not split the file until tests can detect behavior changes.
4. **Scene responsibilities:** map `Scene` partial files and module interfaces; identify which methods are lifecycle, persistence, terrain, object operations, and simulation update.
5. **Service audit completion:** inspect user/account, inventory, land, messaging, friends/groups, database and Hypergrid code before finalizing the Grid service map.

## Explicit non-goals for this pass

- No change to network packet formats or LLUDP behavior.
- No change to login protocol or authentication semantics.
- No change to scene persistence or asset identifiers.
- No renderer integration in the server.
- No removal of legacy/Hypergrid behavior.
- No mass rename or rewrite of upstream classes.

## Source revision fingerprints

The following blob SHAs identify the exact source files inspected in this pass:

- `LLLoginService.cs`: `3fece4fab703095e626c1182afd50ee3bbbaae9e`
- `GridService.cs`: `580b4e7da9af691cda90d64c16db64ce57891a95`
- `AssetService.cs`: `8fdd111febe94e1b2ae3fc69f2a1c0c3534d2ea0`
- `Scene.cs`: `3ddc6c129aa539f46bb3e6a9bc9f812ed79ed2e9`
- `SceneManager.cs`: `38068b8bddd05052799b23770b697d6559810339`
- `SceneGraph.cs`: `05a2e1cc36e2acd7f09ed4826189735f00780d42`
- `ScenePresence.cs`: `7e75a409648b594a4a58db625101702ff322b8a2`
- `SceneObjectGroup.cs`: `c1d50de31e5126d4e8030dca7dfa75bf3e8b9230`
- `LLClientView.cs`: `114c088253d4d891de2db963c90b9f781897b5cc`
- `BUILDING.md`: `a8b47e478be8079eccbe3cb9d253fb6c25978e2b`

## Review links

- [LLLoginService](../../OpenSim/Services/LLLoginService/LLLoginService.cs)
- [GridService](../../OpenSim/Services/GridService/GridService.cs)
- [AssetService](../../OpenSim/Services/AssetService/AssetService.cs)
- [Scene](../../OpenSim/Region/Framework/Scenes/Scene.cs)
- [SceneManager](../../OpenSim/Region/Framework/Scenes/SceneManager.cs)
- [SceneGraph](../../OpenSim/Region/Framework/Scenes/SceneGraph.cs)
- [ScenePresence](../../OpenSim/Region/Framework/Scenes/ScenePresence.cs)
- [SceneObjectGroup](../../OpenSim/Region/Framework/Scenes/SceneObjectGroup.cs)
- [LLClientView](../../OpenSim/Region/ClientStack/Linden/UDP/LLClientView.cs)
- [Build instructions](../../BUILDING.md)
