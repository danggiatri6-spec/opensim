# OpenSim-next PC Viewer Architecture

## 1. Purpose

The PC Viewer is the primary user-facing client for OpenSim-next. Its first milestone is protocol and world compatibility, not a wholesale renderer rewrite. The implementation follows the agreed hybrid route:

- Reuse and study Firestorm's mature Second Life/OpenSim protocol and interaction behavior.
- Keep simulator-facing protocol code isolated from rendering and UI code.
- Introduce a renderer abstraction before replacing the legacy rendering backend.
- Preserve existing OpenSim assets and workflows while adding modern formats incrementally.
- Provide quality tiers so low-spec computers can enter, socialize, and build.

This document is a design contract, not a claim that a new Viewer has already been implemented.

## 2. Repository boundary

Target standalone repository: `danggiatri6-spec/opensim-next-viewer`.

The server repository remains `danggiatri6-spec/opensim`. The Viewer must not be placed inside the OpenSim server's runtime projects or become a server build dependency.

Until the standalone repository is provisioned, this document is maintained in the server repository as the source-of-truth architecture specification. No Firestorm source is copied by this document.

## 3. High-level architecture

```text
OpenSim-next PC Viewer
├── app-shell/              # startup, settings, windowing, update lifecycle
├── protocol/
│   ├── login/               # grid selection, authentication, login response
│   ├── udp/                 # LLUDP transport, packet codecs, reliability
│   ├── caps/                # capability discovery and HTTP operations
│   ├── assets/              # asset requests, transfer, cache contracts
│   └── simulator/           # region connection and simulator state
├── world/
│   ├── scene/               # entities, transforms, region and terrain state
│   ├── avatar/              # appearance, skeleton, animation, attachments
│   ├── objects/             # primitives, mesh, materials, selection
│   └── spatial/             # visibility, bounds, LOD and streaming
├── renderer-api/             # backend-independent rendering contracts
├── renderer-legacy/          # compatibility backend
├── renderer-modern/          # future Vulkan or other modern backend
├── asset-pipeline/
│   ├── legacy/               # legacy texture, mesh and inventory assets
│   ├── gltf/                 # glTF 2.0 / GLB import path
│   ├── materials/            # PBR material conversion and fallback
│   └── cache/                # disk cache, metadata and versioning
├── social/
│   ├── chat/                 # local, region, group and instant messages
│   ├── friends/              # friends, presence and permissions
│   ├── voice/                # voice integration boundary
│   ├── transcription/        # speech-to-text, user-controlled
│   └── translation/          # optional message translation
├── build-tools/
└── tests/
```

The diagram is a logical module map. It does not prescribe that every folder becomes a separate binary or process.

## 4. Non-negotiable boundaries

### 4.1 Protocol must not depend on the renderer

Protocol modules produce simulator/world state and consume user actions. They must not call Vulkan, OpenGL, a scene-graph implementation, or UI widgets directly.

### 4.2 Renderer is replaceable

World state is represented in backend-independent types. A renderer consumes snapshots or explicit scene updates. The Viewer must be able to run a compatibility renderer while a modern backend is developed in parallel.

### 4.3 Preserve the existing ecosystem

The first compatible release must not require users to replace their existing inventory, avatar clothing, attachments, scripts, or land content. Unsupported modern material features need graceful fallbacks.

### 4.4 AI is an assistive tool, not a substitute resident

AI features may assist with transcription, translation, building, scripting, discovery and moderation. They must not impersonate a human resident, fabricate a human presence, or take ownership-changing actions without explicit user confirmation.

### 4.5 Low-spec mode is a first-class target

Quality settings may reduce draw distance, texture resolution, shadow quality, reflections, avatar complexity, vegetation and post-processing. They must not change authoritative simulator behavior or social permissions.

## 5. Renderer API responsibilities

The renderer API should define stable contracts for:

- Device and backend lifecycle.
- Render-frame lifecycle.
- Camera and viewport.
- Scene object creation, update and removal.
- Mesh, texture and material resource handles.
- Lighting, shadows and reflection capabilities.
- Skeletal pose and animation input.
- Visibility, LOD and resource-budget hints.
- Device-loss and resource-recreation handling.
- Capability discovery, so UI settings can expose only supported features.

Do not design the API around Vulkan-specific objects. Vulkan is a candidate backend, not the public architecture. Compare Vulkan and wgpu with a small proof of concept after the boundary is defined.

## 6. First vertical slice

The first Viewer milestone should be deliberately narrow:

1. Launch the client and select an OpenSim grid.
2. Authenticate using the existing login flow.
3. Connect to one region and receive initial simulator state.
4. Render terrain and a small set of legacy primitives.
5. Spawn one avatar, display its appearance and support basic movement.
6. Receive and send local chat.
7. Disconnect cleanly and preserve logs for diagnosis.

Acceptance requires a real OpenSim test region and two Viewer sessions for basic avatar visibility and chat. A mock scene alone is not a compatibility milestone.

## 7. Development phases

### Phase A — Compatibility research and build baseline

- Select a supported Firestorm release branch as a reference point.
- Record upstream commit, license notices and third-party dependency licenses.
- Document login, LLUDP, CAPS, asset transfer and simulator state flows.
- Establish reproducible Windows and Linux build instructions.
- Create protocol characterization tests before behavior changes.

### Phase B — Modular client shell

- Separate protocol, world state, renderer API and UI boundaries.
- Implement the first vertical slice using the compatibility renderer.
- Keep existing protocol semantics and asset fallbacks.
- Add structured logs and diagnostics for login, region connection and asset loading.

### Phase C — Modern renderer proof of concept

- Implement a minimal renderer API test scene.
- Compare Vulkan and wgpu on supported target platforms.
- Measure frame time, memory use, startup time and driver compatibility.
- Integrate only after the proof of concept meets the low-spec and maintainability goals.

### Phase D — Modern content and social features

- Add glTF/GLB import and PBR materials with legacy fallbacks.
- Improve avatar skeleton, animation and facial expression support.
- Add user-controlled speech-to-text and optional translation.
- Add creator tools and AI assistance behind explicit user actions.

## 8. Quality and compatibility tiers

| Tier | Intended hardware | Experience |
|---|---|---|
| Low | Older integrated graphics | Basic world, avatars, chat and building; limited draw distance and effects |
| Balanced | Mainstream integrated/discrete GPU | Better textures, moderate shadows and avatar detail |
| High | Modern discrete GPU | Advanced materials, shadows, reflections and higher scene complexity |

Exact hardware thresholds must be measured rather than guessed. The Viewer should expose manual overrides and avoid blocking login because a GPU lacks an optional feature.

## 9. Testing strategy

- Unit tests for protocol codecs, state transitions and renderer-independent world logic.
- Characterization tests for legacy packet and login behavior.
- Asset compatibility tests using representative legacy textures, mesh, clothing and attachments.
- Renderer conformance tests shared by all backends.
- Integration tests against a real OpenSim region.
- Two-client smoke tests for avatar presence, movement and chat.
- Performance tests on both low-spec and modern hardware.

## 10. Immediate next actions

1. Provision the standalone `opensim-next-viewer` repository.
2. Pin the chosen Firestorm reference release and record its license/dependency inventory.
3. Create the minimal repository skeleton and CI build checks.
4. Implement a protocol-independent world-state model before renderer-specific features.
5. Build the login-to-terrain-to-avatar-to-chat vertical slice.

## 11. Explicit non-goals for the first milestone

- Rewriting all Firestorm code at once.
- Requiring Vulkan on every machine.
- Replacing the entire asset ecosystem with glTF.
- Building the mobile 3D Viewer before the PC client works.
- Introducing autonomous AI residents or AI-generated social identities.
- Changing server packet formats to simplify the Viewer.
