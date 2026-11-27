# Experience / Place Durable Persistence Design

Status: approved implementation direction for the additive C0 persistence slice.

## 1. Goals and compatibility rules

- Promote the in-memory Experience / Place contracts into durable, independently versioned domain records.
- Preserve every existing GameProjectRevisions.ManifestJson byte-for-byte. Historical revisions are immutable and remain readable through the v1 manifest adapter.
- Do not equate a Place with a Region. Region IDs, Region process state, physics handles, worker process IDs, and live simulation state are never stored as Place definition data.
- Make published project revisions reproducible: a project revision must identify exact Experience and Place revisions, not merely their mutable current pointers.
- Keep ownership checks, optimistic concurrency, and publication atomic at the database boundary.

## 2. Aggregate and revision model

```
GameProject
  └─ GameProjectRevision (immutable)
       └─ one or more pinned GameExperienceRevision references
            └─ immutable PlaceRevision references
```

The durable hierarchy separates identity rows from immutable revisions:

- GameExperiences: stable Experience identity, owning ProjectID, display metadata, lifecycle timestamps.
- GameExperienceRevisions: immutable Experience revision metadata, including StartPlaceID and schema version.
- GameExperienceRevisionPlaces: ordered membership rows mapping an Experience revision to exact Place revision numbers.
- GamePlaces: stable Place identity scoped to its owning Experience and Project.
- GamePlaceRevisions: immutable declarative Place definition JSON and schema version.
- GameProjectRevisionExperiences: maps an immutable project revision to exact Experience revision numbers.

The composite identity of a Place is scoped by ProjectID + ExperienceID + PlaceID. IDs remain globally unique UUIDs at the API boundary, but composite foreign keys prevent cross-project or cross-Experience references from being attached accidentally.

A project may eventually contain multiple Experiences. The initial Creator Studio workflow may create one default Experience, but the storage model must not hard-code that product limitation.

## 3. Revision pinning and publication

1. Saving a Place creates a new immutable PlaceRevision. It never updates an earlier revision.
2. Saving an Experience creates a new immutable ExperienceRevision whose membership rows pin exact PlaceRevision numbers and whose StartPlaceID must be a member.
3. Saving a GameProject revision pins exact ExperienceRevision numbers.
4. Publishing a GameProject revision validates that every referenced ExperienceRevision and PlaceRevision exists, belongs to the same project/Experience, and passes schema validation.
5. Publication and status transition occur in one SQLite transaction. No published project revision may resolve through a mutable CurrentRevision pointer.
6. A running Game Instance pins one project revision and resolves all downstream references from that immutable graph at startup.

This prevents later edits to a Place or Experience from changing an already published revision or a running instance.

## 4. Legacy v1 manifest adaptation

Existing v1 project revisions remain untouched.

- Read paths continue to deserialize the v1 GameProjectManifest and use GameProjectExperienceAdapter to project each legacy World to a Place.
- WorldID is preserved as PlaceID; DefaultWorldID is preserved as StartPlaceID.
- The adapter is a compatibility read model, not a write-through migration.
- A legacy project is not silently rewritten during startup or read.
- The first explicit v2 save creates new durable Experience / Place revision records and a new project revision that pins them. Previous v1 project revisions remain available unchanged.
- Any generated default Experience identity for a legacy project must be deterministic from ProjectID, so retries and concurrent requests cannot create duplicate default Experiences. Place IDs reuse existing WorldIDs.

The deterministic identity algorithm must be specified and tested before implementing legacy write promotion. Do not use random IDs during a retryable migration.

## 5. SQLite migration requirements

- Add a new numbered migration after schema v1; never edit the already-applied v1 migration.
- Use composite primary keys and foreign keys for project/Experience/Place revision relationships.
- Enable/verify foreign-key enforcement on every connection used for writes, not only during migration.
- Add indexes for owner/project lookup and revision traversal.
- Avoid cascade deletion of immutable revision history. Archiving is a status transition; physical deletion requires a separate retention policy.
- Keep all write paths parameterized and transactional.
- Store timestamps as UTC round-trip strings, matching the existing provider.
- Store declarative JSON only. Do not persist executable code, native engine objects, worker credentials, or runtime state.

## 6. API rollout

The API rollout is additive:

1. Keep current /creator/projects v1 request/response behavior unchanged.
2. Add read-only Experience and Place endpoints backed by durable records after migration and provider tests pass.
3. Add create/update draft operations with explicit expected revision values.
4. Add v2 project revision publication only after the database can atomically validate and pin the complete revision graph.
5. Keep legacy project endpoints operational through the adapter until clients migrate.

No endpoint may accept a caller-supplied owner ID as an authorization source. The authenticated principal remains authoritative.

## 7. Required tests before enabling persistence

- Fresh database applies v1 then v2 successfully.
- Existing v1 database upgrades without changing any historical ManifestJson or revision number.
- Re-running migration initialization is idempotent.
- Experience and Place IDs are unique and cannot cross project boundaries.
- Duplicate or missing Place membership is rejected.
- StartPlaceID must reference a Place in the same Experience revision.
- Experience revisions pin exact Place revision numbers.
- Project revisions pin exact Experience revision numbers.
- Published revision graphs cannot be mutated or repointed.
- Stale compare-and-save requests fail without partial rows.
- Owner isolation applies to every new read and write path.
- Two concurrent saves cannot create the same next revision.
- Legacy v1 reads retain stable WorldID → PlaceID and DefaultWorldID → StartPlaceID mappings.

## 8. Implementation order

- C0-P1: schema v2 and provider DTO/interface contracts.
- C0-P2: transactional provider operations and SQLite migration tests.
- C0-P3: domain service validation and deterministic legacy promotion.
- C0-P4: additive Creator API read/write endpoints.
- C0-P5: immutable project → Experience → Place revision pinning in publish/runtime resolution.

Do not start API or runtime integration before provider-level revision graph tests pass.
## 9. Implementation status

The SQLite provider now has additive identity and revision records for Experiences and Places, compare-and-save operations, owner-scoped reads, and immutable revision triggers. Experience revision saves atomically pin exact Place revisions.

Project revision data now carries an ordered list of exact Experience revision references. For project manifest schema version 2 or later, provider saves require at least one Experience pin; the revision row and all pin rows are inserted in one transaction before the project's current revision pointer advances. Reads return the pinned references. Publishing a schema-v2 revision requires the pinned graph.

SQLite migration v4 adds database-level guards for valid Experience start-Place membership and required project revision pins. The existing schema-v1 project and manifest flow remains unchanged.

The owner-scoped Experience / Place service layer is implemented. It creates stable identities, saves immutable Place and Experience revisions with compare-and-save, validates StartPlace membership, and reads exact pinned revisions. The Creator HTTPS API exposes additive Experience, Place, and schema-v2 project revision routes through the existing authenticated project handler. Owner spoofing is rejected and nested routes reuse the parent authentication and rate-limit decision.

The Game Runtime now resolves schema-v2 projects through the exact immutable graph pinned by the requested project revision: project revision → Experience revision → StartPlace's Place revision. It never substitutes the latest Place revision. The resolved start Place is validated as a GameWorldDefinition and passed to the isolated Worker. The v1 manifest startup path remains unchanged. Until an explicit Experience selector is added, runtime startup intentionally accepts exactly one pinned Experience per v2 project revision; projects with multiple Experiences are rejected rather than choosing one implicitly.

The test runner now exercises the real SQLite-backed GameProjectService resolver: it resolves a project revision, saves a newer Place revision while the project remains a draft, and proves that resolving the original project revision still returns the original pinned world. Game Runtime tests also verify that the v2 resolver is invoked with the requested revision and its world definition reaches the Worker. CI run 46078010941 passed, including OpenSim build, Game Runtime lifecycle tests, SQLite integration runners, live Robust Creator HTTPS integration, Worker tests, and Windows Named Pipes tests.

Remaining work: add explicit Experience selection to the Game Instance start contract and API; extend tests to cover multiple Experiences, runtime revision pinning across long-lived instances, and failure/recovery paths. Deterministic legacy v1 → v2 promotion remains a separate migration feature and must be specified before implementation.