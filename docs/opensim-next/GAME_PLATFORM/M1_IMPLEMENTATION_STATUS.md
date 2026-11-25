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

The `OpenSim.Services.Interfaces` Prebuild project has no explicit `<Files>` list. Confirm generated project inclusion during the first local Prebuild run before treating the new contracts as build-verified. The `OpenSim.Data.SQLite` project recursively includes C# files and embeds `Resources/*.migrations`, so the new provider and migration follow its existing inclusion rules.

3. `OpenSim/Data/SQLite/SQLiteGameProjectData.cs` and `OpenSim/Data/SQLite/Resources/GameProjects.migrations`
   - SQLite schema for project metadata and immutable manifest revisions.
   - Parameterized CRUD, owner-scoped listing, archival and UTC timestamp persistence.
   - Revision writes use a serializable transaction, owner/status check and compare-and-advance guard.
   - Provider opens a private connection per operation; it does not reuse the legacy SQLite static shared connection pattern.

4. `docs/opensim-next/GAME_PLATFORM/CREATOR_ECONOMY.md`
   - Defines creator-controlled access pricing, offers, memberships and game-scoped virtual goods.
   - Defines creator-configurable quest, achievement, event and progression rewards.
   - Separates external payments, platform credit, game currency, entitlements and creator revenue.
   - Specifies server-authoritative transactions, idempotency, immutable ledger records, player disclosures and cross-game isolation.
   - This is a design specification only; no real-money payments, wallets, payouts or reward runtime have been implemented.

## Not yet implemented

- Concrete service implementation and provider wiring/configuration.
- Creator Economy runtime: offers, reward rules, game-scoped wallets, ledger, entitlements and reporting.
- A concrete `IGameProjectService` implementation.
- HTTP/Capability endpoints and authenticated Creator identity binding.
- Automated unit tests and a full Prebuild + .NET 8 build.
- Viewer integration.

## Required next implementation steps

1. Wire the SQLite provider into the Game Project service configuration/factory.
2. Implement the service layer with authenticated owner checks, pagination limits, manifest serialization/size enforcement, validation and typed errors.
3. Add tests for SQLite migration, ownership isolation, revision conflicts, archival, validation and serialization.
4. Add equivalent providers for other supported database backends where appropriate.
5. Run Prebuild and the complete .NET 8 build; record the actual result before proceeding.

## Safety boundary

No Game Project operation in M1 may create or mutate a live Region, Scene, object, script, NPC or running Game Instance. That integration belongs to later milestones and must use explicit permissions and server-side validation.
