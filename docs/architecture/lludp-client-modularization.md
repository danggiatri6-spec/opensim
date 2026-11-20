# LLUDP / LLClientView Modularization Design

**Branch:** `opensim-next-dev`  
**Status:** Design only — no protocol implementation has been changed.  
**Purpose:** Define a safe extraction path for the large Linden UDP client adapter without breaking existing viewers.

## 1. What the source currently does

The current network path is not one class:

1. `LLUDPServer` receives UDP buffers, validates and decodes packets, manages circuits/clients, queues traffic, handles acknowledgements/retransmits, throttling, and sends packets.
2. `LLUDPClient` holds per-client packet queues, throttle state, round-trip information, and queue/stat events.
3. `LLClientView` implements the public client-facing interfaces and translates protocol packets into simulator-facing events and simulator operations into outbound protocol packets.
4. `IClientAPI` is the simulator-facing contract used by scene and module code.

This is a valuable existing separation. OpenSim-next should strengthen it rather than replace LLUDP with a new transport immediately.

## 2. Observed responsibility concentration

### LLClientView

The class implements `IClientAPI`, `IClientCore`, `IClientIM`, `IClientChat`, `IClientInventory`, `IStatsCollector`, and `IClientIPEndpoint`. It also contains a large event surface and packet handler registration/dispatch.

Source inspection confirms:
- `AddLocalPacketHandler` registers packet handlers, including a per-handler async/sync choice.
- `ProcessPacketMethod` dispatches static and instance handlers and queues asynchronous work.
- `CloseWithoutChecks` coordinates connection-closed events, packet processing shutdown, UDP flush, scene removal, UDP client shutdown, image manager shutdown, and entity update cleanup.
- The class contains outbound methods for chat, instant messages, map, teleport, inventory, object and region updates.

### LLUDPServer and LLUDPClient

The transport responsibilities are already partly separated:
- `LLUDPServer.PacketReceived` validates the datagram, checks header lengths, zero-decodes when needed, builds a packet, and rejects malformed input.
- `LLUDPServer` also owns send queues, acknowledgements, retransmission and circuit/client management.
- `LLUDPClient` owns per-client queues and throttle accounting.

### IClientAPI

`IClientAPI` and its related interfaces/delegates form a broad compatibility contract consumed by simulator modules. Changing signatures here would have a wide impact.

## 3. Target responsibility map

| Component | Owns | Must not own |
|---|---|---|
| `LLUDPServer` | Socket-facing datagrams, packet framing, circuit lifecycle, reliable delivery, ACK/retry, throttling | Scene business rules, inventory policy, object permissions |
| `LLUDPClient` | Per-client queues, throttles, sequence/round-trip state | Avatar or object simulation |
| `LLClientView` facade | Stable client API, session identity, lifecycle coordination, compatibility surface | All packet-specific parsing and all message formatting forever |
| Packet handler groups | Decode a packet into validated request data and raise existing client events | Direct database or scene persistence access |
| Outbound message groups | Build and send protocol messages for a cohesive domain | Owning authoritative world state |
| `IClientAPI` | Stable simulator-facing contract | Concrete socket or renderer implementation |

## 4. Proposed internal extraction groups

These should initially be internal collaborators owned by `LLClientView`. Keep the public class and `IClientAPI` stable.

1. **ConnectionLifecycleCoordinator**
   - Start/close/disconnect sequence.
   - Ensure cleanup is idempotent and ordered.
   - Preserve the existing locking and force-close semantics.

2. **PacketDispatchRegistry**
   - Packet handler registration and lookup.
   - Preserve static-vs-instance dispatch and per-handler sync/async behavior.
   - Preserve packet ordering for handlers that require synchronous processing.

3. **ChatAndInstantMessageProtocol**
   - Incoming/outgoing chat and IM message mapping.
   - Preserve channel, source, sender, region and delivery semantics.

4. **MovementAndTeleportProtocol**
   - Agent updates, region crossing, local/region teleport, neighbour information.
   - Preserve sequencing and circuit handoff semantics.

5. **ObjectAndBuildProtocol**
   - Rez/derez, object selection, transforms, link/delink, texture and shape updates.
   - Preserve permission checks and object identity.

6. **InventoryAndAssetTransferProtocol**
   - Inventory requests, uploads/downloads, xfer and task inventory messages.
   - Preserve asset IDs, transfer IDs, callbacks and failure responses.

7. **MapAndRegionProtocol**
   - Region handshake, map blocks/items, terrain and region information.

These are candidate boundaries, not a requirement to create seven new classes in one commit. Extract one cohesive group at a time after tests cover its behavior.

## 5. Non-negotiable compatibility invariants

- Do not change LLUDP packet IDs, field layouts, flags, sequence numbers or zero-code behavior.
- Do not change reliable/unreliable delivery classification, ACK/retry rules, throttle categories or packet priority.
- Do not silently convert synchronous handlers to asynchronous handlers.
- Preserve ordering where multiple packets update the same avatar/object state.
- Preserve circuit authentication and session/agent identity checks.
- Preserve permission checks in the simulator/domain layer; a protocol handler must not bypass them.
- Preserve disconnect cleanup ordering and make repeated close calls safe.
- Do not put renderer, GPU, PBR, or Viewer-only concerns in server protocol code.
- Keep existing OpenSim/Second Life compatible packet behavior as the baseline.

## 6. Recommended extraction sequence

### Step A — Characterization tests (first code task)

Before moving methods, add tests or a controllable test harness for:
- Packet handler registration and duplicate registration behavior.
- Synchronous versus asynchronous handler dispatch.
- Unknown packet handling.
- Malformed/undersized datagram rejection.
- Zero-coded packet decoding.
- Reliable packet acknowledgement and retransmission behavior.
- Client close, forced close, and repeated close.
- Chat/IM event mapping.
- Agent update and teleport/crossing event ordering.

If some behavior cannot be covered by unit tests, document a manual two-viewer integration test.

### Step B — Extract PacketDispatchRegistry

This is the lowest-risk first extraction candidate because the existing registration and dispatch code is relatively localized. Keep method signatures on `LLClientView` as forwarding methods so callers do not change.

### Step C — Extract one outbound message family

Choose a cohesive family such as map/region messages or chat/IM. The extracted collaborator should receive a narrow send delegate or packet transport interface, not direct ownership of the UDP socket.

### Step D — Extract connection lifecycle

Only after lifecycle tests exist. This code touches scene removal, packet workers, UDP queues, image manager and entity updates; it has ordering and concurrency risks.

### Step E — Continue by protocol family

Move one family at a time, run compatibility tests, and keep the facade forwarding to the extracted implementation.

## 7. Future transport evolution

A future modern viewer may use additional transport mechanisms, but the first milestone should retain LLUDP and the existing HTTP capabilities path. If a new transport is later introduced, define a transport-neutral simulator message contract and implement LLUDP as one adapter. Do not make the current server depend on a new viewer-only protocol before there is a working compatibility client.

## 8. Immediate implementation decision

**Next code change should be tests and a small internal packet-dispatch extraction, not a wholesale rewrite of LLClientView.**

Before implementation, inspect all current packet registration call sites and confirm whether the dispatch dictionaries are accessed outside `LLClientView`. If they are private and local, extract the registry behind the existing forwarding methods. Do not modify `IClientAPI` in this first step.

## Source files inspected

- [LLClientView.cs](../../OpenSim/Region/ClientStack/Linden/UDP/LLClientView.cs)
- [LLUDPServer.cs](../../OpenSim/Region/ClientStack/Linden/UDP/LLUDPServer.cs)
- [LLUDPClient.cs](../../OpenSim/Region/ClientStack/Linden/UDP/LLUDPClient.cs)
- [IClientAPI.cs](../../OpenSim/Framework/IClientAPI.cs)
