using System.Text.Json;
using OpenSim.GamePhysics.WorkerProtocol;

namespace OpenSim.GamePhysics.Worker;

public interface IPhysicsWorldAdapter : IDisposable
{
    JsonElement CreateWorld(Guid worldId, JsonElement configuration);
    JsonElement CreateBody(JsonElement body);
    JsonElement SetVelocity(JsonElement command);
    JsonElement ApplyImpulse(JsonElement command);
    JsonElement Step(JsonElement command);
    JsonElement GetSnapshot();
    JsonElement DestroyBody(JsonElement command);
    JsonElement StopWorld();
}

public sealed class WorkerCommandException : Exception
{
    public WorkerCommandException(WorkerErrorCode code, string message) : base(message) => Code = code;
    public WorkerErrorCode Code { get; }
}
