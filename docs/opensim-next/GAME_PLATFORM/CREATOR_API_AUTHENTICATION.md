# Creator API Authentication and Robust Registration — W6-D

Status: architecture decision recorded; implementation not yet enabled  
Target branch: `opensim-next-dev`  
Date: 2026-11-26

## 1. Audit findings

The current OpenSim server has two distinct extension mechanisms that must not be conflated:

1. Robust loads HTTP service connectors from `Startup.ServiceConnectors` (and `[ServiceList]`) in `OpenSim/Server/ServerMain.cs`. A connector is loaded as an `IServiceConnector` and receives the merged config, selected `IHttpServer`, and optional config-section name.
2. A connector such as `UserAccountServiceConnector` loads its local service from that service section's `LocalServiceModule`, creates service-to-service authentication with `ServiceAuth.Create`, and registers a stream handler on the supplied HTTP server.

The generic `ServiceAuth` / `BasicHttpAuthentication` mechanism authenticates a configured server credential. It does not identify an individual Creator account and must not be treated as user authentication.

OpenSim already exposes `IAuthenticationService.Verify(UUID principalID, string token, int lifetime)`. Its contract describes tokens as principal-bound, expiring service-access tokens. The existing authentication HTTP handler accepts a principal and token pair for verification. This is the best existing identity primitive to evaluate for Creator requests; do not introduce a second password store.

The Game Runtime plugin currently composes `GameProjectService`, Worker factory, and `GameRuntimeService`, but is not yet registered through Robust and has no Creator HTTP handler. The plugin's `IAsyncDisposable` / `IDisposable` support is not by itself a host shutdown registration.

## 2. Identity decision

For the first Creator API iteration, use the existing OpenSim authentication token:

- Request header: `Authorization: Bearer <OpenSim-authentication-token>`.
- Request header: `X-OpenSim-Principal: <principal-uuid>`.
- The handler parses the UUID, rejects zero/invalid IDs, extracts the bearer token, and calls the configured `IAuthenticationService.Verify(principalID, token, 30)`.
- Only after successful verification may the handler treat that principal UUID as the authenticated Creator identity.
- Pass that server-derived identity to `IGameProjectService` and `IGameRuntimeService`.
- Never accept `OwnerID`, `authenticatedOwnerID`, or an equivalent identity field from JSON, query parameters, route parameters, or Creator-supplied metadata.
- A principal header is not independently trusted: changing it must make verification fail because the token is bound to its principal.
- Missing, malformed, expired, or invalid credentials return HTTP 401. Authorization failures for a resource owned by another principal should not reveal its existence; return the existing not-found behavior where applicable.

The existing token lifetime contract is 30 minutes. The Creator handler will use a fixed 30-minute verification window; clients must obtain/refresh credentials through the grid's existing authentication flow. Do not accept a client-selected lifetime.

### Why not Basic HTTP auth?

The existing Basic credential is a shared service credential configured in server INI. Embedding it in a browser, Viewer, or Creator client would disclose a grid-wide secret. Basic service auth remains suitable only for trusted server-to-server calls over protected transport, not Creator end-user identity.

### Browser security

- Require HTTPS for any remotely reachable Creator endpoint.
- Use bearer headers, not cookies, for the initial API to avoid ambient-cookie CSRF.
- Do not enable wildcard CORS with credentials. If browser access is required, configure an explicit Creator-origin allowlist and permit only required methods/headers.
- Never log Authorization values, tokens, or full request headers.
- Do not put tokens in URLs, query strings, redirects, or error messages.
- Apply request-size limits, bounded JSON parsing, per-principal rate limits, and bounded lifecycle concurrency.
- Do not expose Worker IPC paths, capability tokens, process IDs, stack traces, or backend implementation details.

This is a first-party grid API contract, not an OAuth/OIDC claim. If external identity providers or third-party Creator clients are later required, add a deliberate token-exchange/OIDC design rather than stretching the existing token format.

## 3. Robust connector composition

Implement the HTTP entry point as a dedicated Robust connector, not as a Region module:

- New connector type: `GameRuntimeServiceConnector : IServiceConnector`.
- Register it through the existing `Startup.ServiceConnectors` / `[ServiceList]` mechanism.
- Connector reads `[GameRuntimeService]`; `Enabled=false` remains the default and must register no Creator routes or start no Workers.
- When enabled, load the configured `IGameRuntimeService` implementation using the same plugin-loading convention as existing service connectors. The selected implementation is `GameRuntimeServicePlugin`.
- Load the configured `IAuthenticationService` from `[AuthenticationService].LocalServiceModule` using `ServerUtils.LoadPlugin`, following the existing Authentication/UserAccount connector pattern.
- Register a dedicated Creator stream handler on the chosen Robust `IHttpServer`.
- The handler performs user-token verification itself. Do not use `ServiceAuth.Create` as a substitute for the principal-bound token check.
- Connector and handler must own a clear disposal path. At Robust shutdown, dispose the Game Runtime and await all active instance cleanup before the host exits. If the current Robust connector lifecycle cannot guarantee disposal, add an explicit server shutdown hook rather than relying on garbage collection or process exit.

The connector must not register a synthetic Region, Scene, or Region module. Game Instances remain independent Worker processes.

## 4. Initial Creator Runtime routes

Keep the first API narrow and JSON-based:

| Method | Route | Input | Service operation |
|---|---|---|---|
| POST | `/creator/instances` | `project_id`, `revision` | `StartInstanceAsync(authenticatedPrincipal, projectID, revision)` |
| GET | `/creator/instances/{instance_id}` | none | `GetInstanceAsync(authenticatedPrincipal, instanceID)` |
| GET | `/creator/instances` | bounded `offset`, `limit` | `GetOwnedInstancesAsync(authenticatedPrincipal, offset, limit)` |
| POST | `/creator/instances/{instance_id}/stop` | none | `StopInstanceAsync(authenticatedPrincipal, instanceID)` |

The body schema must not contain an owner field. Unknown identity-like fields should be ignored or rejected; they must never override the authenticated principal. Limit JSON request bodies to a small fixed maximum (initial target 16 KiB) and return typed, non-sensitive errors.

Project creation/editing routes are a separate next slice. Do not combine them with runtime lifecycle until the authentication and ownership tests are in place.

## 5. Required tests before enabling

### Authentication tests
- No Authorization header -> 401; runtime service is not called.
- Non-Bearer scheme, empty token, malformed principal UUID, or zero UUID -> 401.
- Expired/invalid token -> 401; runtime service is not called.
- Valid token for principal A plus principal header A -> request proceeds as A.
- Valid token for principal A plus principal header B -> 401; no operation is called as B.
- Request JSON containing `OwnerID=B` while authenticated as A never invokes the service as B.
- Token and Authorization header are absent from logs and error payloads.

### Authorization and lifecycle tests
- A cannot inspect or stop B's instance.
- A cannot start B's project revision.
- A can start/inspect/stop only owned resources.
- Invalid JSON, oversized body, invalid UUID/revision, and unsupported HTTP method are rejected before worker creation.
- Disabled config registers no routes and launches no worker.
- Connector startup failure cleans up any already-created service objects.
- Robust shutdown disposes all active Game Instances and reaps all Worker processes.

### Deployment tests
- Linux and Windows Robust builds load the connector type.
- Connector can be selected from `Startup.ServiceConnectors`.
- HTTPS deployment and explicit CORS configuration are documented.
- Existing Second Life login and Region/Scene physics paths remain unchanged.

## 6. Implementation order

1. Add an isolated request-authentication helper with tests against a fake `IAuthenticationService`.
2. Add the Creator HTTP handler and route/body validation tests; handler must be unable to receive an arbitrary owner ID from the payload.
3. Add the Robust connector and verify plugin loading through the existing `ServerUtils.LoadPlugin` path.
4. Add an explicit connector shutdown/disposal integration path and a real Robust startup/shutdown acceptance test.
5. Keep `[GameRuntimeService].Enabled=false` until all preceding tests pass and an operator explicitly enables the feature.

## 7. Implemented API slice

The first implementation slice now exists in tools/game-runtime:

- GameCreatorRequestAuthenticator: validates the principal-bound bearer token through IAuthenticationService.Verify, fixed at 30 minutes.
- GameCreatorApiHandler: GET/POST JSON handler under /creator/instances; HTTPS required; 16 KiB request-body cap; bounded pagination; no-store responses; owner identity comes only from the authenticated principal.
- GameCreatorServiceConnector: opt-in Robust IServiceConnector; loads the configured AuthenticationService plugin and registers GET/POST handlers only when [GameRuntimeService] Enabled=true.
- Connector currently uses an AppDomain.ProcessExit disposal hook as a final host-process shutdown fallback. This is not yet equivalent to a tested, explicit Robust connector unload lifecycle; graceful host shutdown acceptance remains a release gate.
- The Game Runtime project now references the existing Robust HTTP server and handler contracts so the connector can be loaded as GameRuntime.dll:GameCreatorServiceConnector.

### Robust configuration (opt-in)

Add the connector to the existing [ServiceList] entries (preserve all current values):

```ini
[ServiceList]
GameRuntime = "GameRuntime.dll:GameCreatorServiceConnector"
```

Keep the existing authentication service configured:

```ini
[AuthenticationService]
; Keep the grid's existing LocalServiceModule value here.
```

The Game Runtime section remains disabled unless deliberately enabled:

```ini
[GameRuntimeService]
Enabled = false
WorkerAssemblyPath = "worker/GamePhysicsWorker.dll"
MaximumActiveInstances = 16
```

To expose the API, an operator must explicitly set Enabled = true, deploy the complete Game Runtime bundle, configure the existing AuthenticationService, and serve the Robust endpoint over HTTPS. The handler rejects requests when IOSHttpRequest.IsSecured is false. TLS termination at an external reverse proxy is not supported by this initial slice because trusted-proxy identity/transport headers have not been designed.

Routes implemented:

| Method | Route | Behavior |
|---|---|---|
| POST | /creator/instances | Start using project_id and revision |
| GET | /creator/instances | List authenticated creator's instances; offset/limit bounded |
| GET | /creator/instances/{instance_id} | Read only an owned instance |
| POST | /creator/instances/{instance_id}/stop | Stop only an owned instance |

The POST body rejects owner identity fields. The handler never accepts a client-supplied OwnerID as authority.

### Remaining acceptance gates

- Unit/integration tests for handler routing, HTTPS rejection, body limits, pagination, JSON type errors, and owner-field spoofing.
- Verify the actual Robust connector loader can load GameRuntime.dll:GameCreatorServiceConnector from the deployed bundle.
- Verify the deployment bundle includes all referenced OpenSim assemblies and the authentication plugin dependencies.
- Add explicit connector unload/host shutdown ownership instead of relying only on ProcessExit.
- Linux and Windows end-to-end Robust startup, authenticated API request, unauthorized cross-owner access, and shutdown tests.
- Only then mark Creator API registration complete and consider enabling it in production.

## 8. Current status

- [x] Audited Robust connector loading in `ServerMain`.
- [x] Audited existing service plugin loading convention.
- [x] Confirmed `ServiceAuth` Basic auth is a shared service credential, not Creator identity.
- [x] Confirmed principal-bound token verification exists in `IAuthenticationService`.
- [x] Recorded the authenticated-principal and API boundary decision.
- [x] Implement principal-bound request authenticator and positive/negative token tests (CI passed).
- [x] Implement Creator Runtime HTTP handler routes and HTTPS/body/pagination safeguards.\n- [ ] Add handler-level routing, body-limit, JSON-type, and owner-spoofing integration tests.
- [x] Implement opt-in Robust IServiceConnector and register GET/POST handlers.\n- [ ] Replace ProcessExit-only fallback with explicit Robust connector unload/host shutdown ownership and acceptance tests.
- [ ] Verify deployed-bundle connector loading, Linux/Windows Robust startup, authenticated API calls, cross-owner denial, and clean shutdown. The current CI proves compilation and packaging, not live Robust loading.
- [ ] Enable only after explicit operator configuration.
