# OpenSim-next Architecture Roadmap

## Status

This document records the initial direction for evolving this repository into OpenSim-next. The current source tree is the OpenSimulator server codebase. No server behavior is changed by this document.

## Product direction

See the [OpenSim-next Product Charter](../product/open-sim-next-product-charter.md) for the long-term vision, six modernization pillars, three platform foundations, constraints, and success criteria.


OpenSim-next will begin as a centrally managed virtual-world grid:

- A central grid service manages accounts, avatar identity, region discovery, land ownership and permissions, inventory/asset references, friends, messaging, and other global services.
- Region simulators remain independently deployable so capacity can grow horizontally.
- Users connect through a dedicated PC viewer. The first milestone is desktop; mobile is not part of the initial delivery.
- The platform should remain usable on modest PCs. Graphics quality is a viewer-side setting and must not change authoritative world simulation rules.
- Distributed or independently hosted regions may be considered later, after the central-grid experience is stable.

## Viewer direction

The initial viewer strategy is a hybrid migration:

1. Use Firestorm as a protocol and feature reference to reach OpenSim compatibility quickly.
2. Keep protocol, world state, asset services, UI, and rendering behind clear module boundaries.
3. Define a renderer abstraction before replacing rendering internals.
4. Introduce modern rendering incrementally, validating each subsystem against the legacy path.
5. Prefer glTF 2.0 and PBR for new asset workflows while retaining compatibility with existing OpenSim/Second Life assets.

The project must not assume that replacing the renderer means replacing the server protocol or world model.

## Architectural boundaries

Target logical components:

- **Grid services:** identity, login, grid/region directory, permissions, land, inventory and social services.
- **Region runtime:** authoritative scene state, object lifecycle, physics, scripts, avatar presence, persistence.
- **Protocol adapters:** client login, UDP message handling, HTTP capabilities, asset transfer, and compatibility behavior.
- **Asset pipeline:** storage, validation, conversion, caching, texture/model processing, and future LOD generation.
- **Viewer:** desktop UI, input, avatar presentation, scene presentation, local cache, and renderer.
- **Operations:** configuration, deployment, observability, backups, and upgrade tooling.

These are target boundaries, not a claim that the current code already implements them as separate services.

## Migration principles

- Preserve upstream OpenSimulator behavior until a replacement has compatibility tests.
- Prefer adapters and new modules over invasive edits to stable core code.
- Keep protocol compatibility explicit and testable.
- Do not couple authoritative simulation behavior to graphics quality or a specific renderer.
- Add automated tests and a documented rollback path for each migration.
- Record upstream source, commit ancestry, and license notices when importing or adapting third-party code.

## Initial work sequence

### Phase 0 — Establish a reproducible baseline

- Confirm the exact upstream commit and branch ancestry.
- Record the current .NET/Mono target frameworks and supported build environments.
- Build the unmodified server and run the available test suite in a supported environment.
- Inventory configuration, database providers, region startup, login, asset, and protocol paths.
- Document known limitations before changing behavior.

### Phase 1 — Server architecture audit

Inspect and document:

- Login and grid service flow.
- Region startup and lifecycle.
- Scene, avatar presence, and object lifecycle.
- UDP and HTTP client protocol paths.
- Asset and inventory storage.
- Physics and scripting boundaries.
- Persistence and database abstraction.

For every subsystem, classify it as **keep**, **wrap**, **refactor**, or **replace**, with evidence and compatibility risks.

### Phase 2 — Compatibility and service boundaries

- Add integration tests around login, region entry, avatar movement, object updates, chat, inventory, and asset delivery.
- Introduce service interfaces only where they reduce coupling and can be tested.
- Keep the existing deployment model working while boundaries are introduced.

### Phase 3 — Dedicated viewer foundation

- Establish the viewer repository/build baseline.
- Implement login and region connection before visual modernization.
- Define renderer-facing scene, material, camera, lighting, animation, and GPU-resource interfaces.
- Compare candidate renderer technologies with a small measured proof of concept before committing.

### Phase 4 — Incremental modernization

- Modernize asset handling and rendering subsystem by subsystem.
- Add graphics presets and adaptive quality settings.
- Retain a compatibility path until the new implementation passes functional and performance tests.

## Immediate next deliverable

Produce a source-backed subsystem audit of the existing OpenSimulator code. Do not begin broad server rewrites or renderer replacement until that audit and a reproducible build baseline exist.
