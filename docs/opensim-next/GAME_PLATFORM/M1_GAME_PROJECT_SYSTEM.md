# OpenSim-next Game Platform — M1 Game Project System

Status: Architecture specification (M1 scope)
Target repository: `danggiatri6-spec/opensim`
Target branch: `opensim-next-dev`

## 1. Purpose

M1 establishes the server-side project model and lifecycle for user-created games. It does not yet implement the visual editor, AI generation, game simulation, NPC behavior, matchmaking, or public publishing.

The project system must be additive: it must not replace or alter existing OpenSim Region, Scene, Avatar, Inventory, Asset, LSL, or OSSL behavior.

## 2. Architectural boundaries

- **OpenSim Region/Scene:** continues to own the live virtual-world scene and existing simulator behavior.
- **Game Project Service:** owns creator projects, metadata, world definitions, settings, revisions, and access checks.
- **Game Runtime (later milestone):** loads a validated project revision and runs server-authoritative game state.
- **Viewer Creator Mode (separate Viewer work):** edits projects through a versioned service contract.
- **AI Gateway (later milestone):** calls creator-configured model providers and proposes project changes. It must not write directly to the live Scene or execute model output as arbitrary server code.

The project service must be independent of the Firestorm Viewer. The Viewer is a client of the service, not the owner of project data.

## 3. M1 domain model

### GameProject

Required fields:

- `ProjectId`: UUID, immutable identifier.
- `OwnerId`: UUID of the creator account.
- `Name`: display name, 1–80 characters.
- `Description`: optional plain text, bounded length.
- `Status`: `Draft`, `Published`, or `Archived`.
- `SchemaVersion`: version of the serialized project format.
- `CurrentRevision`: monotonically increasing integer.
- `CreatedUtc`, `UpdatedUtc`: UTC timestamps.

A project belongs to one owner in M1. Collaboration and team ownership are explicitly deferred.

### WorldDefinition

A project may contain one or more world definitions. M1 stores definitions only; it does not allocate or start simulator Regions.

Required fields:

- `WorldId`: UUID unique within the project.
- `Name`: display name.
- `Description`: optional.
- `RegionTemplate`: bounded dimensions and environment metadata; not a live Region UUID.
- `SpawnPoints`: validated position and rotation records.
- `AssetReferences`: references to existing OpenSim assets by UUID and declared asset type.

### GameSettings

M1 settings are declarative and bounded:

- player capacity target (advisory in M1; enforced by Runtime later);
- default spawn world and spawn point;
- game mode identifier from a registered allow-list;
- visibility preference;
- optional tags.

No arbitrary executable expressions or script bodies are accepted in this object.

### ProjectRevision

Every successful save creates a revision:

- `ProjectId`
- `Revision`
- `SchemaVersion`
- `Manifest` (validated serialized project document)
- `CreatedUtc`
- `CreatedBy`

Published revisions must be immutable. Editing a published project creates a new draft revision; it must not mutate the revision currently used by running instances.

### AssetReference

Assets remain in the existing OpenSim asset system. A project stores references and expected types, not duplicate asset binaries. The service validates that the reference is well-formed and that the requesting creator is permitted to use the asset before publishing or runtime loading.

## 4. Persistence contract

Introduce an abstraction before selecting a concrete database implementation:

```csharp
public interface IGameProjectStore
{
    GameProjectRecord Create(GameProjectRecord project);
    GameProjectRecord Get(Guid projectId);
    IReadOnlyList<GameProjectRecord> ListOwned(Guid ownerId, int offset, int limit);
    GameProjectRecord UpdateMetadata(GameProjectRecord project, long expectedRevision);
    ProjectRevisionRecord SaveRevision(ProjectRevisionRecord revision, long expectedCurrentRevision);
    ProjectRevisionRecord GetRevision(Guid projectId, long revision);
    bool Archive(Guid projectId, Guid ownerId);
}
```

The signatures are a contract sketch and must be adapted to the repository's actual target framework, async conventions, error types, and coding style before implementation.

Storage requirements:

- enforce unique project IDs and revision numbers;
- enforce owner checks in the service layer, not only in the Viewer;
- use optimistic concurrency via `expectedCurrentRevision`;
- bound page size and serialized document size;
- use transactions for metadata/revision updates;
- never silently overwrite a newer revision;
- keep storage implementation replaceable.

Before choosing SQLite, MySQL, or an existing OpenSim connector, audit the active branch's supported database providers and migration conventions. M1 must not introduce an incompatible database dependency.

## 5. Service contract

The service layer should expose these operations to future Viewer/API adapters:

- Create project.
- Get project metadata.
- List projects owned by the authenticated creator.
- Save a draft revision.
- Read a specific revision.
- Rename/update description and settings.
- Archive a project.

All operations derive the creator identity from the authenticated server session. A client-supplied `OwnerId` is never trusted as authorization evidence.

Errors should be typed and stable: not found, forbidden, validation failure, revision conflict, quota exceeded, and storage unavailable.

## 6. Project document format

Use JSON as the interchange format, with a required top-level schema version. The persisted document is a manifest, not executable code.

Illustrative shape:

```json
{
  "schemaVersion": 1,
  "projectId": "00000000-0000-0000-0000-000000000001",
  "name": "Zombie Survival",
  "worlds": [
    {
      "worldId": "00000000-0000-0000-0000-000000000002",
      "name": "Abandoned City",
      "assetReferences": []
    }
  ],
  "settings": {
    "gameMode": "survival",
    "defaultWorldId": "00000000-0000-0000-0000-000000000002"
  }
}
```

The example IDs are placeholders only. Production validation must check UUIDs, required fields, enum allow-lists, maximum collection sizes, coordinate bounds, asset types, and schema version.

## 7. Security and resource limits

M1 must include:

- authenticated creator identity;
- owner-scoped authorization for every read/write;
- strict input validation and maximum document size;
- bounded project count and revision count per creator (configurable);
- no arbitrary server file paths;
- no arbitrary network URLs in project manifests;
- no execution of scripts or AI-generated code;
- audit logging for create, update, archive, and publish-state changes.

Creator AI API keys are not part of GameProject or ProjectRevision. A later credential service must store them separately, encrypted at rest, with secrets never returned to the Viewer after initial entry.

## 8. Lifecycle

M1 lifecycle:

1. Create project in Draft.
2. Save validated draft revisions.
3. Read/list/rename/archive owned projects.
4. Mark a validated revision as Published only when publishing policy is implemented.

Actual game launch and Instance allocation are deferred to M2/M3. A project record must not pretend that a live Region or GameInstance exists.

## 9. M1 acceptance criteria

- A creator can create a project and receive a stable ProjectId.
- A creator can list and reopen their own projects.
- A creator cannot read, modify, or archive another creator's project.
- Saving creates a new revision and detects stale revision conflicts.
- Invalid or oversized manifests are rejected.
- A project can reference existing OpenSim assets without copying their binaries.
- Existing Region/Scene, login, inventory, LSL, and OSSL behavior remains unchanged.
- Unit tests cover ownership, validation, revision concurrency, pagination, archive behavior, and serialization round trips.

## 10. Explicitly out of scope

- Game Runtime and live GameInstance allocation.
- NPC behavior and AI dialogue.
- Visual Creator Studio UI.
- AI Provider configuration and API-key storage.
- Marketplace, discovery, payments, and creator revenue.
- Cross-Region instance orchestration.
- Arbitrary script execution.

## 11. Implementation sequence after this specification

1. Inspect the active branch's project layout, target framework, dependency injection/module registration, test conventions, and database connector/migration patterns.
2. Add domain records and manifest validation without touching Scene.
3. Add the store interface and a repository-compatible persistence implementation.
4. Add the project service with authenticated owner checks and optimistic concurrency.
5. Add unit tests and build the server solution.
6. Only after the server contract stabilizes, implement the Viewer Creator Mode client adapter.
