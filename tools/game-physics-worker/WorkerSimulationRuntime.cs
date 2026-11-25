using System.Text.Json;
using System.Threading.Channels;
using OpenSim.GamePhysics.WorkerProtocol;

namespace OpenSim.GamePhysics.Worker;

/// <summary>
/// Owns the adapter on one background simulation loop. All backend calls, including
/// commands and fixed-step advancement, are serialized on that loop.
/// </summary>
public sealed class WorkerSimulationRuntime : IAsyncDisposable
{
    private const int QueueCapacity = WorkerProtocolConstants.DefaultMaxQueuedCommands;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1d / 30d);
    private readonly IPhysicsWorldAdapter _adapter;
    private readonly Channel<WorkItem> _commands;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private volatile bool _running;
    private volatile bool _faulted;
    private int _disposed;
    private long _tickCount;
    private long _lastSnapshotTick;

    public WorkerSimulationRuntime(IPhysicsWorldAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _commands = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _loop = RunLoopAsync();
    }

    public bool IsRunning => _running;
    public bool IsFaulted => _faulted;
    public int QueueDepth => _commands.Reader.Count;
    public long TickCount => Interlocked.Read(ref _tickCount);

    public async Task<JsonElement> ExecuteAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_faulted)
            throw new WorkerCommandException(WorkerErrorCode.BackendUnavailable, "Simulation runtime is faulted.");

        var item = new WorkItem(operation, payload.Clone());
        if (!_commands.Writer.TryWrite(item))
            throw new WorkerCommandException(WorkerErrorCode.QueueFull, "Physics command queue is full.");

        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((WorkItem)state!).Completion.TrySetCanceled(), item);
        return await item.Completion.Task.ConfigureAwait(false);
    }

    private async Task RunLoopAsync()
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                while (_commands.Reader.TryRead(out WorkItem? item))
                    Process(item);

                if (_running && !_faulted)
                {
                    try
                    {
                        JsonElement snapshot = _adapter.Step(JsonSerializer.SerializeToElement(new { steps = 1 }));
                        Interlocked.Increment(ref _tickCount);
                        if (TickCount - _lastSnapshotTick >= 2)
                        {
                            _lastSnapshotTick = TickCount;
                            // Snapshot publication is added with the duplex event channel in W3-B.
                            _ = snapshot;
                        }
                    }
                    catch
                    {
                        _faulted = true;
                        _running = false;
                        FailQueued(WorkerErrorCode.BackendOperationFailed, "Physics simulation step failed.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            FailQueued(WorkerErrorCode.WorkerStopping, "Worker simulation is stopping.");
        }
    }

    private void Process(WorkItem item)
    {
        if (item.Completion.Task.IsCompleted)
            return;

        try
        {
            JsonElement result = item.Operation switch
            {
                "CreateWorld" => _adapter.CreateWorld(item.Payload.TryGetProperty("worldId", out _) ? ReadWorldId(item.Payload) : _worldId, item.Payload),
                "CreateBody" => _adapter.CreateBody(item.Payload),
                "SetVelocity" => _adapter.SetVelocity(item.Payload),
                "ApplyImpulse" => _adapter.ApplyImpulse(item.Payload),
                "Step" => ExecuteManualStep(item.Payload),
                "GetSnapshot" => _adapter.GetSnapshot(),
                "DestroyBody" => _adapter.DestroyBody(item.Payload),
                "StopWorld" => StopWorld(),
                "StartSimulation" => StartSimulation(),
                "PauseSimulation" => PauseSimulation(),
                "GetHealth" => GetHealth(),
                _ => throw new WorkerCommandException(WorkerErrorCode.UnsupportedOperation, "Operation is not supported by this worker.")
            };
            item.Completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            item.Completion.TrySetException(ex);
        }
    }

    private Guid _worldId;

    private static Guid ReadWorldId(JsonElement payload)
    {
        if (!payload.TryGetProperty("worldId", out JsonElement value) || !value.TryGetGuid(out Guid id) || id == Guid.Empty)
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, "worldId is required in CreateWorld.");
        return id;
    }

    public void SetWorldId(Guid worldId)
    {
        if (worldId == Guid.Empty)
            throw new ArgumentException("World ID must be non-empty.", nameof(worldId));
        _worldId = worldId;
    }

    private JsonElement ExecuteManualStep(JsonElement payload)
    {
        if (_running)
            throw new WorkerCommandException(WorkerErrorCode.InvalidWorldState, "Manual Step is available only while simulation is paused.");
        return _adapter.Step(payload);
    }

    private JsonElement StartSimulation()
    {
        _ = _adapter.GetSnapshot(); // Validate that the world exists before starting.
        _running = true;
        return JsonSerializer.SerializeToElement(new { state = "Running", tick = TickCount });
    }

    private JsonElement PauseSimulation()
    {
        _running = false;
        return JsonSerializer.SerializeToElement(new { state = "Paused", tick = TickCount });
    }

    private JsonElement StopWorld()
    {
        _running = false;
        return _adapter.StopWorld();
    }

    private JsonElement GetHealth() => JsonSerializer.SerializeToElement(new
    {
        state = _faulted ? "Faulted" : (_running ? "Running" : "Ready"),
        tick = TickCount,
        commandQueueDepth = QueueDepth,
        snapshotRateHz = 15
    });

    private void FailQueued(WorkerErrorCode code, string message)
    {
        while (_commands.Reader.TryRead(out WorkItem? item))
            item.Completion.TrySetException(new WorkerCommandException(code, message));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _running = false;
        _commands.Writer.TryComplete();
        _stop.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { }
        _adapter.Dispose();
        _stop.Dispose();
    }

    private sealed class WorkItem
    {
        public WorkItem(string operation, JsonElement payload)
        {
            Operation = operation;
            Payload = payload;
        }
        public string Operation { get; }
        public JsonElement Payload { get; }
        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
