using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenSim.GamePhysics.WorkerProtocol;

public static class WorkerProtocolConstants
{
    public const string ProtocolName = "opensim.game-physics";
    public const int CurrentVersion = 1;
    public const int DefaultMaxCommandFrameBytes = 1024 * 1024;
    public const int DefaultMaxEventFrameBytes = 1024 * 1024;
    public const int DefaultMaxSnapshotFrameBytes = 4 * 1024 * 1024;
    public const int DefaultMaxQueuedCommands = 4096;
    public const int DefaultMaxQueuedEvents = 4096;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkerMessageKind
{
    Request,
    Response,
    Event
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkerLifecycleState
{
    Starting,
    Handshaking,
    Ready,
    WorldCreating,
    Running,
    Stopping,
    Stopped,
    Faulted
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkerErrorCode
{
    ProtocolMismatch,
    AuthenticationFailed,
    InvalidEnvelope,
    InvalidSequence,
    InvalidWorldState,
    InvalidArgument,
    UnsupportedOperation,
    ObjectNotFound,
    ObjectAlreadyExists,
    CapacityExceeded,
    QueueFull,
    DeadlineExceeded,
    BackendUnavailable,
    BackendOperationFailed,
    EventOverflow,
    WorkerStopping,
    InternalError
}

public sealed class WorkerProtocolEnvelope
{
    public string Protocol { get; set; } = WorkerProtocolConstants.ProtocolName;
    public int Version { get; set; } = WorkerProtocolConstants.CurrentVersion;
    public WorkerMessageKind Kind { get; set; }
    public Guid WorldId { get; set; }
    public Guid? RequestId { get; set; }
    public long Sequence { get; set; }
    public long? EventSequence { get; set; }
    public string? Operation { get; set; }
    public bool? Ok { get; set; }
    public WorkerErrorCode? Error { get; set; }
    public JsonElement Payload { get; set; }
}

public static class WorkerProtocolValidator
{
    public static bool TryValidate(WorkerProtocolEnvelope? envelope, out string error)
    {
        if (envelope is null)
            return Fail("Envelope is null.", out error);

        if (!string.Equals(envelope.Protocol, WorkerProtocolConstants.ProtocolName, StringComparison.Ordinal))
            return Fail("Protocol name is invalid.", out error);

        if (envelope.Version != WorkerProtocolConstants.CurrentVersion)
            return Fail("Protocol version is not supported.", out error);

        if (envelope.WorldId == Guid.Empty)
            return Fail("World ID must be non-empty.", out error);

        if (envelope.Sequence <= 0)
            return Fail("Sequence must be positive.", out error);

        switch (envelope.Kind)
        {
            case WorkerMessageKind.Request:
                if (envelope.RequestId is null || envelope.RequestId == Guid.Empty)
                    return Fail("Requests require a non-empty request ID.", out error);
                if (string.IsNullOrWhiteSpace(envelope.Operation))
                    return Fail("Requests require an operation.", out error);
                if (envelope.Ok is not null || envelope.Error is not null || envelope.EventSequence is not null)
                    return Fail("Request contains response/event-only fields.", out error);
                break;

            case WorkerMessageKind.Response:
                if (envelope.RequestId is null || envelope.RequestId == Guid.Empty)
                    return Fail("Responses require a non-empty request ID.", out error);
                if (envelope.Ok is null)
                    return Fail("Responses require an outcome.", out error);
                if (envelope.Ok == true && envelope.Error is not null)
                    return Fail("Successful responses cannot contain an error code.", out error);
                if (envelope.Ok == false && envelope.Error is null)
                    return Fail("Failed responses require an error code.", out error);
                if (envelope.EventSequence is not null)
                    return Fail("Responses cannot contain an event sequence.", out error);
                break;

            case WorkerMessageKind.Event:
                if (envelope.EventSequence is null || envelope.EventSequence <= 0)
                    return Fail("Events require a positive event sequence.", out error);
                if (envelope.RequestId is not null || envelope.Operation is not null ||
                    envelope.Ok is not null || envelope.Error is not null)
                    return Fail("Event contains request/response-only fields.", out error);
                break;

            default:
                return Fail("Message kind is invalid.", out error);
        }

        error = string.Empty;
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
