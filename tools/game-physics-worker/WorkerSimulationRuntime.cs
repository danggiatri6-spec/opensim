using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using OpenSim.GamePhysics.WorkerProtocol;

namespace OpenSim.GamePhysics.Worker;

/// <summary>
/// Owns the adapter on one dedicated OS thread. All backend calls and fixed-step
/// advancement occur on that thread, preserving native backend thread affinity.
/// </summary>
public sealed class WorkerSimulationRuntime : IAsyncDisposable
{
    private const int QueueCapacity = WorkerProtocolConstants.DefaultMaxQueuedCommands;
    private const int MaxCommandsPerLoop = 128;
    private const int MaxCatchUpSteps = 3;
    private static readonly long StepIntervalTicks = Stopwatch.Frequency / 30;
    private readonly IPhysicsWorldAdapter _adapter;
    private readonly BlockingCollection<WorkItem> _commands = new(new ConcurrentQueue<WorkItem>(), QueueCapacity);
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _simulationThread;
    private volatile bool _running;
    private volatile bool _faulted;
    private volatile bool _stopRequested;
    private int _disposed;
    private long _tickCount;
    private long _lastSnapshotTick;
    private Guid _worldId;

    public WorkerSimulationRuntime(IPhysicsWorldAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _simulationThread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "OpenSim Game Physics"
        };
        _simulationThread.Start();
    }

    public bool IsRunning => _running;
    public bool IsFaulted => _faulted;
    public int QueueDepth => _commands.Count;
    public long TickCount => Interlocked.Read(ref _tickCount);

    public void SetWorldId(Guid worldId)
    {
        if (worldId == Guid.Empty)
            throw new ArgumentException("World ID must be non-empty.", nameof(worldId));
        _worldId = worldId;
    }

    public Task<JsonElement> ExecuteAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_faulted || _stopRequested)
            throw new WorkerCommandException(WorkerErrorCode.BackendUnavailable, "Simulation runtime is not available.");

        var item = new WorkItem(operation, payload.Clone());
        if (!_commands.TryAdd(item))
            throw new WorkerCommandException(WorkerErrorCode.QueueFull, "Physics command queue is full.");
        _wake.Set();

        if (cancellationToken.CanBeCanceled)
        {
            _ = cancellationToken.Register(static state =>
                ((WorkItem)state!).Completion.TrySetCanceled(), item);
        }
        return item.Completion.Task;
    }

    private void RunLoop()
    {
        long nextStep = Stopwatch.GetTimestamp() + StepIntervalTicks;
        try
        {
            while (!_stopRequested)
            {
                int processed = 0;
                while (processed < MaxCommandsPerLoop && _commands.TryTake(out WorkItem? item))
                {
                    Process(item);
                    processed++;
                }

                if (_stopRequested)
                    break;

                if (_running && !_faulted)
                {
                    long now = Stopwatch.GetTimestamp();
                    int catchUp = 0;
                    while (now >= nextStep && catchUp < MaxCatchUpSteps && _running && !_faulted)
                    {
                        AdvanceOneStep();
                        nextStep += StepIntervalTicks;
                        catchUp++;
                        now = Stopwatch.GetTimestamp();
                    }

                    // Never enter an unbounded catch-up loop after a long stall.
                    if (now >= nextStep)
                        nextStep = now + StepIntervalTicks;
                }

                if (_commands.Count > 0)
                    continue;

                int waitMilliseconds = 1000;
                if (_running && !_faulted)
                {
                    long remaining = Math.Max(0, nextStep - Stopwatch.GetTimestamp());
                    waitMilliseconds = Math.Clamp(
                        (int)Math.Ceiling(remaining * 1000d / Stopwatch.Frequency), 1, 33);
                }
                _wake.WaitOne(waitMilliseconds);
            }
        }
        catch
        {
            _faulted = true;
            _running = false;
        }
        finally
        {
            FailQueued(WorkerErrorCode.WorkerStopping, "Worker simulation is stopping.");
            // Dispose the backend on the same dedicated thread that owned it.
            try { _adapter.Dispose(); } catch { }
        }
    }

    private void AdvanceOneStep()
    {
        try
        {
            JsonElement snapshot = _adapter.Step(JsonSerializer.SerializeToElement(new { steps = 1 }));
            long currentTick = snapshot.TryGetProperty("tick", out JsonElement tickValue) && tickValue.TryGetInt64(out long reportedTick)
                ? reportedTick
                : TickCount + 1;
            Interlocked.Exchange(ref _tickCount, currentTick);
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

    private void Process(WorkItem item)
    {
        if (item.Completion.Task.IsCompleted)
            return;

        try
        {
            JsonElement result = item.Operation switch
            {
                "CreateWorld" => _adapter.CreateWorld(_worldId, item.Payload),
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

    private JsonElement ExecuteManualStep(JsonElement payload)
    {
        if (_running)
            throw new WorkerCommandException(WorkerErrorCode.InvalidWorldState, "Manual Step is available only while simulation is paused.");
        JsonElement snapshot = _adapter.Step(payload);
        if (snapshot.TryGetProperty("tick", out JsonElement tickValue) && tickValue.TryGetInt64(out long reportedTick))
            Interlocked.Exchange(ref _tickCount, reportedTick);
        return snapshot;
    }

    private JsonElement StartSimulation()
    {
        _ = _adapter.GetSnapshot();
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
        while (_commands.TryTake(out WorkItem? item))
            item.Completion.TrySetException(new WorkerCommandException(code, message));
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;
        _running = false;
        _stopRequested = true;
        try { _commands.CompleteAdding(); } catch { }
        _wake.Set();
        if (!_simulationThread.Join(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Simulation thread did not stop within the teardown deadline.");
        _wake.Dispose();
        _commands.Dispose();
        return ValueTask.CompletedTask;
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
