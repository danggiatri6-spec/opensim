# OpenSim-next Avatar Interaction and Dynamics Server Contract

## Scope

The server-side work supports modern desktop avatar interaction without turning the simulator into a real-time character renderer. The first clients are ordinary PC Viewers. VR and webcam input are not required.

## Authority split

The Viewer owns presentation:
- face blend shapes, blinking, gaze, voice-energy lip sync;
- hair/cloth secondary motion;
- local animation blending and immediate visual prediction.

The simulator owns shared interaction authority:
- authenticated agent identity and root-presence status;
- region membership and distance validation;
- interaction preference/consent;
- request throttling and paired event relay;
- cancellation when an agent leaves, teleports, becomes unavailable, or revokes consent.

The simulator must not receive raw microphone audio, webcam frames, per-frame bone transforms, or per-strand hair/cloth state.

## Compatibility-first transport

OpenSim already relays legacy avatar animation state through its existing animation pack path. Facial expressions and gestures that map to existing animation UUIDs should continue using that path; no duplicate expression protocol should be introduced.

New paired interactions require an optional, negotiated capability. A client that does not advertise or understand the capability must continue to receive ordinary legacy behavior. Do not add unknown UDP message IDs or overload legacy packets.

## Interaction policy

The intended user policy values are:
- `Off`: reject unsolicited avatar-to-avatar interactions.
- `Friends`: accept only users who satisfy the Grid's supported friendship relationship.
- `Ask`: require explicit local approval before starting a paired interaction.
- `Everyone`: allow requests subject to distance and rate limits.

Safe initial default: `Off`.

The simulator must validate every request:
1. Requester and target are distinct, authenticated root presences in the same scene.
2. Both are not in transit, deleted, or otherwise unavailable.
3. The target's policy permits the requester; `Ask` is not silently treated as `Everyone`.
4. The request is within a conservative configured distance.
5. Per-agent and per-pair rate limits pass.
6. The requested interaction is from a server-approved bounded identifier list, not arbitrary client-supplied animation assets.
7. Both sides receive a paired start/cancel event; state is cleared on disconnect, teleport, region crossing, or timeout.

Client-side collision/contact reports are hints only and never sufficient proof for a server-authorized interaction.

## Phased server implementation

1. Add a region-scoped optional capability and a small LLSD request/response contract.
2. Add per-presence interaction policy with a safe default and cleanup on lifecycle transitions.
3. Validate requests and relay paired events; include request IDs and expiry to prevent replay.
4. Add simulator tests for same-scene checks, consent, distance, rate limits and lifecycle cleanup.
5. Keep all features disabled unless both client and region support the capability.

## Non-goals

- No server-side cloth, hair, face, eye, or mouth simulation.
- No forced avatar movement or physical pushing in the first version.
- No transmission of camera or microphone media.
- No changes to legacy LSL animation APIs or existing animation pack semantics.

## Status

This document defines the implementation contract only. It does not claim that the capability or runtime interaction system has been implemented.
