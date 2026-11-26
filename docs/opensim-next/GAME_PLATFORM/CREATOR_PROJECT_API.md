# Creator Project API — W6-E

Status: implemented; CI validation pending for this commit series  
Branch: opensim-next-dev  
Date: 2026-11-26

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
| POST | `/creator/projects/{project_id}/archive` | Archives the owned project |

A newly created project starts at revision 0. The first successful draft save must provide `expected_current_revision: 0`, producing immutable revision 1. Subsequent saves must send the latest current revision. Revision conflicts return HTTP 409. The project service rechecks owner, draft state, manifest validity, and revision under the storage boundary.

Owner identity fields (`owner`, `owner_id`, `ownerId`, `authenticatedOwnerId`, etc.) are rejected recursively in submitted JSON. The UUID JSON converter accepts only string UUIDs. Manifest schema and bounds are validated by the existing `GameProjectManifestValidator`.

## Important lifecycle limitation

The current `IGameProjectService` and SQLite provider expose Draft, Published, and Archived status values, but do not yet provide a publish transition. This API therefore does not expose a publish route. A project can be created and revised, but cannot be started by Game Runtime until a separately tested atomic publish operation is added. Do not work around this by editing database status directly.

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
- archive route.

These handler tests use fake authentication and project services. They do not replace SQLite provider integration tests or live Robust end-to-end acceptance.
