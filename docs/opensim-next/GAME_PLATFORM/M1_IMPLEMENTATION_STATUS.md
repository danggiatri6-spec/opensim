# OpenSim-next Game Platform — M1 implementation status

Branch: `opensim-next-dev`
Target repository: `danggiatri6-spec/opensim`

## Completed in source

1. `OpenSim/Services/Interfaces/GameProjectContracts.cs`
   - Project, world, spawn point, asset reference, settings, manifest and revision contracts.
   - Project status and service error enums.
   - Manifest validator with schema version and collection limits; bounds world descriptions, spawn names, tags and asset usage; rejects non-finite transforms and non-normalized rotations; requires a valid default-world reference.
   - `IGameProjectService` service boundary.

2. `OpenSim/Data/IGameProjectData.cs`
   - Persistent storage records for project metadata and immutable revisions.
   - Provider contract requires atomic revision compare-and-save.
   - Metadata update contract includes both expected manifest revision and expected update timestamp for compare-and-swap.

3. `OpenSim/Data/SQLite/SQLiteGameProjectData.cs` and `OpenSim/Data/SQLite/Resources/GameProjects.migrations`
   - SQLite schema for project metadata and immutable manifest revisions.
   - Parameterized CRUD, owner-scoped listing, archival and UTC timestamp persistence.
   - Revision writes use a serializable transaction, owner/status check and compare-and-advance guard.
   - Metadata updates compare owner, revision and prior `UpdatedUTC`, preventing same-revision lost updates.
   - Provider opens a private connection per operation.

4. `OpenSim/Services/UserAccountService/GameProjectService.cs`
   - Implements `IGameProjectService` with creator-owner checks, draft-only mutation, bounded pagination, metadata validation and typed result errors.
   - Serializes manifests with a 2 MiB character limit and a UUID-as-string JSON converter.
   - Saves immutable revisions through the provider's atomic compare-and-advance operation.
   - Loads `IGameProjectData` through the existing `ServiceBase.LoadPlugin` pattern.
   - No live Region or Scene is touched.

5. `prebuild.xml`
   - Explicitly includes root C# source files for `OpenSim.Services.Interfaces`.
   - Explicitly includes root C# files and `Properties/*.cs` for `OpenSim.Services.UserAccountService`.

6. `bin/config-include/storage/SQLiteStandalone.ini`
   - Adds a `[GameProjectService]` configuration using `OpenSim.Data.SQLite.dll:SQLiteGameProjectData`.
   - Reuses the standalone `OpenSim.db` connection, so the migration creates additive tables in the existing SQLite database.

7. `docs/opensim-next/GAME_PLATFORM/CREATOR_ECONOMY.md`
   - Defines creator-controlled access pricing, offers, memberships and game-scoped virtual goods.
   - Defines creator-configurable quest, achievement, event and progression rewards.
   - Separates external payments, platform credit, game currency, entitlements and creator revenue.
   - This remains a design specification; no real-money payments, wallets, payouts or reward runtime have been implemented.

## Important integration boundary

The configuration section and service class do not by themselves expose a public API. A trusted host module must instantiate `GameProjectService` and pass an owner UUID obtained from authenticated server-side identity. Do not accept `authenticatedOwnerID` from an untrusted Viewer request. HTTP/Capability endpoints and Creator UI are not yet implemented.

## Not yet implemented

- Trusted host/module wiring and authenticated Creator identity binding.
- HTTP/Capability endpoints and Viewer Creator Mode.
- Automated unit tests and a full Prebuild + .NET 8 build.
- Creator Economy runtime: offers, reward rules, game-scoped wallets, ledger, entitlements and reporting.
- Equivalent providers for other supported database backends.

## Required next implementation steps

1. Run Prebuild and verify the generated project files include the new interfaces and service.
2. Run the complete .NET 8 build and fix any compile/API mismatches.
3. Add tests for SQLite migration, ownership isolation, revision conflicts, metadata compare-and-swap, archival, validation and serialization.
4. Add a trusted service host and authenticated endpoint layer; never trust a creator UUID supplied by the Viewer.
5. Add other database providers where appropriate, then begin Viewer Creator Mode.

## Safety boundary

No Game Project operation in M1 may create or mutate a live Region, Scene, object, script, NPC or running Game Instance. That integration belongs to later milestones and must use explicit permissions and server-side validation.
