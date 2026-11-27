# Creator Experience / Place API v2

Status: implementation contract. The authenticated Experience/Place and v2 project-revision routes are now wired through the existing Creator project handlers; continue treating each route as subject to CI and integration verification.

## Compatibility boundary

- Existing `/creator/projects` v1 routes and v1 manifest JSON remain unchanged.
- The SQLite migration version (currently v4) is independent from the public manifest/API schema version.
- A project becomes v2 only through an explicit v2 save/promotion operation. Reads of historical v1 revisions remain byte-for-byte compatible.
- All routes require the existing HTTPS Creator authentication pipeline. The authenticated principal is the sole source of owner identity; request bodies must not contain an authoritative `OwnerID`.

## Resource model

```
Project
  └─ ProjectRevision (immutable)
       └─ ExperienceID + exact ExperienceRevision
            └─ ExperienceRevision (immutable)
                 ├─ StartPlaceID
                 └─ PlaceID + exact PlaceRevision
                      └─ PlaceRevision (immutable definition)
```

A Place is authored content, not a Region, Scene, Game Instance, Worker, or physics world. No live runtime state is accepted by these endpoints.

## Proposed routes

All IDs are UUID strings. All revision values are positive integers except an expected current revision, which may be zero for first save.

| Method | Route | Purpose |
|---|---|---|
| GET | `/creator/projects/{projectId}/experiences` | List Experiences owned through this project |
| POST | `/creator/projects/{projectId}/experiences` | Create an Experience draft identity |
| GET | `/creator/projects/{projectId}/experiences/{experienceId}` | Read Experience metadata/current revision |
| PUT | `/creator/projects/{projectId}/experiences/{experienceId}/revisions` | Save a new immutable Experience revision |
| GET | `/creator/projects/{projectId}/experiences/{experienceId}/revisions/{revision}` | Read an exact Experience revision |
| GET | `/creator/projects/{projectId}/experiences/{experienceId}/places` | List Places in an Experience |
| POST | `/creator/projects/{projectId}/experiences/{experienceId}/places` | Create a Place draft identity |
| GET | `/creator/projects/{projectId}/experiences/{experienceId}/places/{placeId}` | Read Place metadata/current revision |
| PUT | `/creator/projects/{projectId}/experiences/{experienceId}/places/{placeId}/revisions` | Save a new immutable Place revision |
| GET | `/creator/projects/{projectId}/experiences/{experienceId}/places/{placeId}/revisions/{revision}` | Read an exact Place revision |
| PUT | `/creator/projects/{projectId}/revisions/v2` | Save a project revision pinning exact Experience revisions |

## Request contracts

### Create Experience

```json
{}
```

The service generates the ExperienceID. Identity creation is intentionally metadata-free; the Experience name and StartPlaceID are supplied in its first immutable revision. The project must exist, belong to the authenticated principal, and remain Draft.

### Create Place

```json
{}
```

The service generates the PlaceID. Place name and description are supplied with the first immutable Place revision. The Experience must belong to the same project and owner. Creating an identity does not publish content and does not advance an Experience revision.

### Save Place revision

```json
{
  "expectedCurrentRevision": 0,
  "schemaVersion": 1,
  "name": "Lobby",
  "description": "Starting area",
  "definition": {
    "world": {
      "worldID": "00000000-0000-0000-0000-000000000000",
      "sizeX": 256,
      "sizeY": 256
    }
  }
}
```

The actual definition schema must be validated and versioned independently. The PlaceID and ownership scope come from the route and database, not from the body. A save creates revision `expectedCurrentRevision + 1`; it never edits an existing revision.

### Save Experience revision

```json
{
  "expectedCurrentRevision": 0,
  "schemaVersion": 1,
  "name": "My Experience",
  "startPlaceID": "00000000-0000-0000-0000-000000000000",
  "places": [
    { "placeID": "00000000-0000-0000-0000-000000000000", "placeRevision": 1, "sortOrder": 0 }
  ]
}
```

Every Place pin must resolve to a revision belonging to the same Experience, project, and authenticated owner. Place IDs and sort orders must be unique; sort orders must be non-negative; StartPlaceID must be among the pinned Places. The service must reject missing, foreign, unpublished, or otherwise invalid references before saving.

### Save project revision v2

```json
{
  "expectedCurrentRevision": 1,
  "schemaVersion": 2,
  "experiences": [
    { "experienceID": "00000000-0000-0000-0000-000000000000", "experienceRevision": 1, "sortOrder": 0 }
  ]
}
```

The server resolves each exact Experience revision and verifies its full Place revision graph. The immutable project revision stores those exact pins. Publication remains a separate operation and is permitted only when the current v2 project revision contains at least one valid Experience pin.

## Concurrency and atomicity

- Every revision write uses optimistic concurrency against the stored current revision.
- A failed compare-and-swap must not insert a revision or advance a current-revision pointer.
- Project revision insertion, Experience pin insertion, and project current-revision advancement are one SQLite transaction (already enforced by the provider).
- Experience revision writes must atomically insert the revision, its exact Place pins, and advance the Experience current revision.
- Place revision writes must atomically insert the immutable definition and advance the Place current revision.
- Multi-resource creation is intentionally not represented as a single implicit transaction. If a multi-step Creator workflow partially creates draft identities, those empty identities remain private and can be retried or cleaned up; no project revision may pin them until valid immutable revisions exist.
- Never delete historical revisions to recover from a failed save.

## HTTP behavior

- `200`: successful reads and updates that return a representation.
- `201`: successful identity creation; include the generated ID and `Location`.
- `400`: malformed JSON, invalid schema, invalid UUID, or invalid bounds.
- `401/403`: missing/invalid authentication or authenticated principal not permitted to mutate the resource.
- `404`: resource absent or outside the authenticated owner's project scope (avoid leaking foreign IDs).
- `409`: expected revision is stale or immutable-state transition conflicts.
- `413`: request body exceeds the route limit.
- `500`: unexpected server failure; do not return SQL details, tokens, filesystem paths, or stack traces.

All responses remain `Cache-Control: no-store`. Apply the existing request-size cap and per-principal rate limiter to the new routes; do not create a less restrictive parallel path.

## v1 promotion

A v1 project is not silently rewritten on read. Promotion is an explicit operation that:
1. Reads a specific immutable v1 project revision.
2. Converts each legacy World into one Place, preserving WorldID as PlaceID.
3. Creates a default Experience whose StartPlaceID is the legacy DefaultWorldID.
4. Saves immutable Place and Experience revisions.
5. Saves a new v2 project revision pinning that exact Experience revision.
6. Uses compare-and-swap at each aggregate boundary and reports partial draft identity creation without altering the historical v1 revision.

Before promotion ships, define and test a deterministic ExperienceID derivation from ProjectID (including UUID byte order and namespace), so retries cannot create duplicate default Experiences. Do not infer this identity from a random UUID generated per request.

## Required tests before enabling routes

- authenticated owner can create/read Experience and Place identities;
- another principal receives indistinguishable not-found behavior for foreign resources;
- owner ID in a request body cannot override the authenticated principal;
- invalid StartPlaceID, missing Place revision, cross-project Place pin, duplicate pin, and stale CAS are rejected;
- failed revision writes leave no partial revision/pin rows and do not advance current revision;
- project v2 revision pins exact Experience revision, and Experience revision pins exact Place revisions;
- historical v1 manifest JSON and existing v1 endpoints remain unchanged;
- v2 publication requires a complete pinned graph;
- request size, rate limits, HTTPS-only behavior, and no-store headers match existing Creator routes.
