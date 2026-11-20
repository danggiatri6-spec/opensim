# LLUDP Packet Dispatch — Test Gap and Implementation Checklist

**Branch:** `opensim-next-dev`  
**Status:** Source inspection complete; tests not executed in this environment.

## Finding

The repository already has UDP tests under:

`OpenSim/Region/ClientStack/Linden/UDP/Tests/`

Existing files include:
- `BasicCircuitTests.cs`
- `LLImageManagerTests.cs`
- `PacketHandlerTests.cs`
- `ThrottleTests.cs`

However, `PacketHandlerTests.cs` currently contains only a `[TestFixture]` class. Its former `InPacketTest` and setup code are commented out. It does not currently provide active coverage for the `LLClientView` packet registration and dispatch methods.

This is a more precise finding than saying that no UDP tests exist: transport/circuit/image/throttle tests are present, but the packet-dispatch fixture is effectively a placeholder.

## Current dispatch behavior to preserve

Source: `OpenSim/Region/ClientStack/Linden/UDP/LLClientView.cs`

- `AddLocalPacketHandler(PacketType, PacketMethod)` defaults to asynchronous processing.
- `AddLocalPacketHandler(PacketType, PacketMethod, bool)` registers with `Dictionary.TryAdd` under a lock. A duplicate packet type is rejected (returns false); it does not replace the previous handler.
- `AddGenericPacketHandler` lowercases and trims the method name before registering it; duplicate names are rejected.
- `ProcessPacketMethod` checks the static handler table first, then the per-client handler table.
- Both static and per-client handlers preserve their configured synchronous/asynchronous mode.
- Asynchronous work is queued through the client's `m_asyncPacketProcess` queue.
- An unrecognized packet returns false from `ProcessPacketMethod`.

The static handler table is a frozen dictionary. It should remain separate from the per-client mutable handler registry.

## Required characterization tests

Before extracting a registry or moving packet handlers, add active tests for:

1. A first registration succeeds.
2. A second registration for the same packet type fails and does not overwrite the first handler.
3. Synchronous handlers execute inline.
4. Asynchronous handlers are queued and eventually execute.
5. An unregistered packet is reported as unhandled.
6. Static handlers retain precedence over per-client handlers for the same packet type.
7. Generic message method names are normalized consistently (trimmed and lowercased).
8. Duplicate generic message handlers are rejected.
9. Handler exceptions follow the existing async queue/error policy and do not corrupt subsequent dispatch.

## Safe implementation boundary

Do not change the public `IClientAPI` contract in this task.

Do not change:
- LLUDP packet IDs or packet layouts.
- Static handler registration.
- Packet delivery reliability or throttling.
- The synchronous/asynchronous defaults.
- The async packet queue implementation.

The first implementation should be a narrow internal registry extraction only if the tests can exercise it without constructing the full simulator. Keep the existing `LLClientView` methods as forwarding methods so packet registration call sites remain unchanged.

## Validation required before merge

- Run the focused UDP test fixture.
- Run the full OpenSim test suite.
- Build the generated solution using the repository's documented .NET 8 environment.
- Perform a two-viewer smoke test: login, region entry, movement, chat, object update, disconnect and reconnect.

No claim of passing tests or successful build should be made until those commands have actually run.
