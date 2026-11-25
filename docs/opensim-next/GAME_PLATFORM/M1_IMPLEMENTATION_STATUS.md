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

3. `OpenSim/Data/SQLite/SQLiteGameProjectData.cs` and `OpenSim/Data/SQLite/Resources/GameProjects.migrations`
   - SQLite schema for project metadata and immutable manifest revisions.
   - Parameterized CRUD, owner-scoped listing, archival and UTC timestamp persistence.
   - Revision writes use a serializable transaction, owner/status check and compare-and-advance guard.
   - Provider opens a private connection per operation; it does not reuse the legacy SQLite static shared connection pattern.

4. `OpenSim/Services/UserAccountService/GameProjectService.cs`
   - Implements `IGameProjectService` with creator-owner checks, draft-only mutation, bounded pagination, metadata validation and typed result errors.
   - Serializes manifests with a 2 MiB character limit and a UUID-as-string JSON converter.
   - Saves immutable revisions through the provider's atomic compare-and-advance operation.
   - Loads `IGameProjectData` through the existing `ServiceBase.LoadPlugin` pattern. The `GameProjectService` config section must provide `StorageProvider` and `ConnectionString` (or inherit them from `DatabaseService`).
   - Provider failures are mapped to safe service results; no live Region or Scene is touched.

5. `docs/opensim-next/GAME_PLATFORM/CREATOR_ECONOMY.md`
   - Defines creator-controlled access pricing, offers, memberships and game-scoped virtual goods.
   - Defines creator-configurable quest, achievement, event and progression rewards.
   - Separates external payments, platform credit, game currency, entitlements and creator revenue.
   - This remains a design specification; no real-money payments, wallets, payouts or reward runtime have been implemented.

## Build-system audit

The active branch uses Prebuild to generate `OpenSim.sln`; the solution file is generated and is not tracked at repository root.

The `OpenSim.Data` project explicitly includes root-level `*.cs` files, so `IGameProjectData.cs` is covered by its existing source inclusion rule.

The `OpenSim.Services.Interfaces` Prebuild project has no explicit `<Files>` list. Confirm generated project inclusion during the first local Prebuild run before treating the new contracts as build-verified. The `OpenSim.Data.SQLite` project recursively includes C# files and embeds `Resources/*.migrations`, so the new provider and migration follow its existing inclusion rules. Confirm that the UserAccountService project includes the new service source during Prebuild.

## Not yet implemented

- A deployment configuration/example and runtime verification that the SQLite provider is loadable through the plugin loader.
- Creator Economy runtime: offers, reward rules, game-scoped wallets, ledger, entitlements and reporting.
- HTTP/Capability endpoints and authenticated Creator identity binding.
- Automated unit tests and a full Prebuild + .NET 8 build.
- Viewer integration.

## Required next implementation steps

1. Add a sample `GameProjectService` configuration and verify the SQLite provider is loadable through the existing plugin loader.
2. Add tests for SQLite migration, ownership isolation, revision conflicts, archival, validation and UUID/manifest serialization.
3. Confirm Prebuild includes all new source files and run the complete .NET 8 build; record the actual result before proceeding.
4. Add equivalent providers for other supported database backends where appropriate.

## Safety boundary

No Game Project operation in M1 may create or mutate a live Region, Scene, object, script, NPC or running Game Instance. That integration belongs to later milestones and must use explicit permissions and server-side validation.
