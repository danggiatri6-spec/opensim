using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OpenSim.GamePhysics.WorkerProtocol;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException("FAIL " + message);
    Console.WriteLine("PASS " + message);
}

static byte[] Frame(byte[] payload)
{
    byte[] framed = new byte[sizeof(uint) + payload.Length];
    BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(0, sizeof(uint)), checked((uint)payload.Length));
    payload.CopyTo(framed, sizeof(uint));
    return framed;
}

var request = new WorkerProtocolEnvelope
{
    Kind = WorkerMessageKind.Request,
    WorldId = Guid.NewGuid(),
    RequestId = Guid.NewGuid(),
    Sequence = 1,
    Operation = "Hello"
};
Assert(WorkerProtocolValidator.TryValidate(request, out _), "valid request envelope is accepted");

var invalidRequest = new WorkerProtocolEnvelope { Kind = WorkerMessageKind.Request, WorldId = Guid.NewGuid(), Sequence = 1 };
Assert(!WorkerProtocolValidator.TryValidate(invalidRequest, out _), "request without request ID and operation is rejected");

var invalidVersion = new WorkerProtocolEnvelope
{
    Kind = WorkerMessageKind.Request,
    Version = WorkerProtocolConstants.CurrentVersion + 1,
    WorldId = Guid.NewGuid(),
    RequestId = Guid.NewGuid(),
    Sequence = 1,
    Operation = "Hello"
};
Assert(!WorkerProtocolValidator.TryValidate(invalidVersion, out _), "unknown protocol version is rejected");

var successResponse = new WorkerProtocolEnvelope
{
    Kind = WorkerMessageKind.Response,
    WorldId = request.WorldId,
    RequestId = request.RequestId,
    Sequence = 1,
    Ok = true
};
Assert(WorkerProtocolValidator.TryValidate(successResponse, out _), "successful response envelope is accepted");

var failedResponse = new WorkerProtocolEnvelope
{
    Kind = WorkerMessageKind.Response,
    WorldId = request.WorldId,
    RequestId = request.RequestId,
    Sequence = 2,
    Ok = false
};
Assert(!WorkerProtocolValidator.TryValidate(failedResponse, out _), "failed response without error code is rejected");

var eventEnvelope = new WorkerProtocolEnvelope
{
    Kind = WorkerMessageKind.Event,
    WorldId = request.WorldId,
    Sequence = 1,
    EventSequence = 1
};
Assert(WorkerProtocolValidator.TryValidate(eventEnvelope, out _), "event envelope with event sequence is accepted");

byte[] json = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
using (var stream = new MemoryStream())
{
    await WorkerFrameCodec.WriteAsync(stream, json, 1024);
    stream.Position = 0;
    using JsonDocument? parsed = await WorkerFrameCodec.ReadAsync(stream, 1024);
    Assert(parsed?.RootElement.GetProperty("hello").GetString() == "world", "frame round-trip succeeds");
}

using (var stream = new MemoryStream(Frame(json)))
using (JsonDocument? parsed = await WorkerFrameCodec.ReadAsync(stream, 1024))
    Assert(parsed is not null, "valid length-prefixed JSON frame is read");

using (var stream = new MemoryStream(new byte[] { 0, 0 }))
{
    bool rejected = false;
    try { using JsonDocument? _ = await WorkerFrameCodec.ReadAsync(stream, 1024); }
    catch (EndOfStreamException) { rejected = true; }
    Assert(rejected, "truncated frame header is rejected");
}

using (var stream = new MemoryStream(new byte[] { 0, 0, 8, 0 }))
{
    bool rejected = false;
    try { using JsonDocument? _ = await WorkerFrameCodec.ReadAsync(stream, 1024); }
    catch (InvalidDataException) { rejected = true; }
    Assert(rejected, "oversized frame is rejected before payload allocation");
}

using (var stream = new MemoryStream(Frame(Encoding.UTF8.GetBytes("{bad"))))
{
    bool rejected = false;
    try { using JsonDocument? _ = await WorkerFrameCodec.ReadAsync(stream, 1024); }
    catch (InvalidDataException) { rejected = true; }
    Assert(rejected, "invalid JSON payload is rejected");
}

using (var stream = new MemoryStream(Frame(json[..^2])))
{
    bool rejected = false;
    try { using JsonDocument? _ = await WorkerFrameCodec.ReadAsync(stream, 1024); }
    catch (InvalidDataException) { rejected = true; }
    Assert(rejected, "truncated JSON payload is rejected");
}

using (var stream = new MemoryStream())
{
    bool rejected = false;
    try { await WorkerFrameCodec.WriteAsync(stream, Encoding.UTF8.GetBytes("{bad"), 1024); }
    catch (InvalidDataException) { rejected = true; }
    Assert(rejected, "writer rejects invalid JSON");
}

Console.WriteLine("All Game Physics Worker protocol tests passed.");
