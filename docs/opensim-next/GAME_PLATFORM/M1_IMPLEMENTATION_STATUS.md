# OpenSim-next Game Platform — M1 implementation status

Branch: `opensim-next-dev`
Target repository: `danggiatri6-spec/opensim`

## Completed in source

1. `OpenSim/Services/Interfaces/GameProjectContracts.cs`
   - Project, world, spawn point, asset reference, settings, manifest and revision contracts.
   - Project status and service error enums.
   - Manifest validator with schema version, collection limits, coordinate checks, asset-reference checks and default-world validation.
   - `IGameProjectService` service boundary.

2. `OpenSim/Data/IGameProjectData.cs`
   - Persistent storage records for project metadata and immutable revisions.
   - `IGameProjectData` provider contract.
   - Revision save contract explicitly requires an atomic compare-and-advance operation using the expected current revision.

## Build-system audit

The active branch uses Prebuild to generate `OpenSim.sln`; the solution file is generated and is not tracked at repository root.

The `OpenSim.Data` project explicitly includes root-level `*.cs` files, so `IGameProjectData.cs` is covered by its existing source inclusion rule.

The `OpenSim.Services.Interfaces` Prebuild project has no explicit `<Files>` list. Confirm generated project inclusion during the first local Prebuild run before treating the new contracts as build-verified.

## Not yet implemented

- A concrete `IGameProjectData` provider (SQLite first, then other supported providers).
- A concrete `IGameProjectService` implementation.
- HTTP/Capability endpoints and authenticated Creator identity binding.
- Automated unit tests and a full Prebuild + .NET 8 build.
- Viewer integration.

## Required next implementation steps

1. Inspect SQLite provider registration, migration conventions and transaction helpers.
2. Add the Game Platform tables through the repository's supported migration mechanism.
3. Implement atomic project revision persistence in SQLite.
4. Implement the service layer with owner checks, pagination limits, validation and typed errors.
5. Add tests for ownership isolation, revision conflicts, validation and serialization.
6. Run Prebuild and the complete .NET 8 build; record the actual result before proceeding.

## Safety boundary

No Game Project operation in M1 may create or mutate a live Region, Scene, object, script, NPC or running Game Instance. That integration belongs to later milestones and must use explicit permissions and server-side validation.
