# W7 Creator HTTPS Integration Acceptance

Status: implementation added; CI result pending  
Branch: opensim-next-dev

## Purpose

Exercise the Creator connector through a real OpenSim `BaseHttpServer` HTTPS listener, rather than invoking handler methods directly or substituting fake authentication and project services.

The integration runner configures and loads:
- the production `WebkeyAuthenticationService` plugin;
- the production `SQLiteAuthenticationData` authentication provider;
- the production `GameRuntimeServicePlugin` and `GameProjectService`;
- the production `SQLiteGameProjectData` project provider;
- the production `GameCreatorServiceConnector` and its registered project handlers;
- an actual TLS listener and HTTP client.

The test creates a temporary self-signed localhost certificate and an isolated SQLite database. It obtains a real principal-bound bearer token through the OpenSim authentication service, then sends HTTPS requests to the registered Creator routes.

## Acceptance checks

1. Production authentication storage accepts a test principal.
2. Webkey authentication issues a token and the authentication service verifies it.
3. The Creator connector loads through its normal configuration path and registers the routes on a real `BaseHttpServer`.
4. HTTPS `POST /creator/projects` creates a Draft project and returns HTTP 201.
5. HTTPS `GET /creator/projects?offset=0&limit=50` reads the persisted project back from SQLite.
6. Reusing the same bearer token with a different principal header returns HTTP 401.
7. Connector disposal unregisters its routes and disposes the runtime.

## Deliberate scope boundary

This is a live OpenSim HTTP-server integration test using production connector, authentication, and SQLite implementations. It is not yet a subprocess test that boots the complete `Robust.exe` host with a production grid configuration. That final deployment acceptance remains necessary before enabling Creator API on a real grid.

The test does not start a physics worker or create a live Region/Scene. It verifies the Creator project API and the real authentication/storage boundary only.
