# Creator Project API — W6-E / W6-F / W7

Status: W6-E and W6-F implemented. W7 live HTTPS integration harness added; final CI verification is in progress.  
Branch: opensim-next-dev  
Date: 2026-11-27

## Scope

This API exposes the existing `IGameProjectService` through the opt-in Robust Creator connector. The connector and runtime share one `GameProjectService` instance and therefore one configured storage provider. Project ownership is always derived from the authenticated principal; request bodies cannot assign an owner.

The API persists declarative project metadata and immutable manifest revisions only. It does not create OpenSim Regions, Scenes, prims, scripts, NPCs, or physics worlds.

## Authentication and transport

Every route uses the same principal-bound bearer token verification as `/creator/instances`:

- `Authorization: Bearer <token>`
- `X-OpenSim-Principal: <uuid>`
- Existing `IAuthenticationService.Verify(principal, token, 30)`
- HTTPS required; 401 for invalid credentials, 503 if authentication is unavailable.
- Shared per-principal in-memory rate limiter with the Runtime API.
- Responses use `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`.
- Maximum request body is manifest maximum (2 Mi characters) plus 64 KiB envelope allowance.

The connector registers GET, POST, PUT and PATCH handlers under `/creator/projects` only when `[GameRuntimeService] Enabled=true`. The default remains disabled.

## Routes

| Method | Route | Body / behavior |
|---|---|---|
| POST | `/creator/projects` | `name`, optional `description`; creates a Draft project |
| GET | `/creator/projects?offset=0&limit=50` | Lists only the authenticated creator's projects; limit 1–100 |
| GET | `/creator/projects/{project_id}` | Gets an owned project |
| PUT | `/creator/projects/{project_id}/draft` | `expected_current_revision`, `manifest`; validates and saves a new immutable revision |
| GET | `/creator/projects/{project_id}/revisions/{revision}` | Reads an owned immutable revision |
| PATCH | `/creator/projects/{project_id}` | `expected_current_revision`, `name`, optional `description`; optimistic concurrency |
| POST | `/creator/projects/{project_id}/publish` | `expected_current_revision`; validates and atomically publishes the current immutable revision |
| POST | `/creator/projects/{project_id}/archive` | Archives the owned project |

A newly created project starts at revision 0. The first successful draft save must provide `expected_current_revision: 0`, producing immutable revision 1. Subsequent saves must send the latest current revision. Revision conflicts return HTTP 409. The project service rechecks owner, draft state, manifest validity, and revision under the storage boundary.

Owner identity fields (`owner`, `owner_id`, `ownerId`, `authenticatedOwnerId`, etc.) are rejected recursively in submitted JSON. The UUID JSON converter accepts only string UUIDs. Manifest schema and bounds are validated by the existing `GameProjectManifestValidator`.

## Publish lifecycle — W6-F

Publishing is an explicit authenticated transition:

`POST /creator/projects/{project_id}/publish`

Request body:

```json
{ "expected_current_revision": 3 }
```

The service requires:
- the authenticated principal owns the project;
- the project is still Draft;
- the supplied revision equals the project's current revision and is at least 1;
- the current immutable revision exists, was created by the owner, and uses the supported schema;
- the stored manifest is within the size limit, parses successfully, and passes the full manifest validator, including world IDs, dimensions, spawn points, asset references, settings and default-world membership.

The SQLite provider performs a single conditional status update requiring Draft status, matching owner, matching current revision, matching update timestamp, and existence of the current immutable revision. This makes the state transition atomic against concurrent draft saves, metadata updates, archive and publish attempts. Repeated publish and stale revision attempts are rejected; revision conflicts return HTTP 409. The immutable revision row is never modified.

After publishing, draft saves and metadata updates are rejected by the service. Game Runtime can then start only a published project revision. Archiving remains available for both Draft and Published projects; archived projects cannot be published or started. No direct database status edits are supported.

The project service validates the manifest before invoking the storage transition. The provider also independently enforces the atomic state/revision/ownership preconditions; it does not replace service-level semantic manifest validation.

## W7 live HTTPS integration

`tools/game-runtime/robust-e2e/` starts the production `BaseHttpServer` with a test-only localhost TLS certificate, loads the production `WebkeyAuthenticationService` and SQLite authentication provider, and uses the configured SQLite Game Project provider. It sends real HTTPS requests through the registered Creator connector and verifies project creation, list/read persistence, and principal-bound token isolation. The certificate is generated for the test and is not a deployment credential.

This is a live HTTP listener integration test, not yet a full `Robust.exe` process/grid startup test. Full Robust startup, configuration loading, service registration, and shutdown remain a separate deployment acceptance gate. The integration runner must pass cleanup as well as request assertions.

## Operational configuration

Keep the existing configuration opt-in:

```ini
[GameRuntimeService]
Enabled = false
```

When enabled, the existing `[GameProjectService]` provider and connection string are required. No new database is introduced by this API. The same service instance is injected into the runtime and project handlers.

## Tests

The Game Runtime test runner exercises:
- create/list/get project routes;
- authenticated owner identity propagation;
- rejection of client-supplied owner identity at top level and nested manifest level;
- draft save with expected revision;
- immutable revision retrieval;
- optimistic metadata update;
- publish route, owner-spoofing rejection, and HTTP 409 revision-conflict mapping.\n- archive route.\n- SQLite atomic publish, owner isolation, stale revision, repeated publish rejection, and immutable revision preservation.\n- service-level validation of project/revision schema, malformed manifest JSON, missing revisions, and manifest semantics before publication.

Handler-level tests use fake authentication and project services. Separate live HTTPS integration tests exercise the production authentication module, SQLite providers, actual `BaseHttpServer`, Creator connector registration, project create/list, and cross-principal token rejection.
