using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using OpenSim.GamePhysics.WorkerProtocol;

namespace OpenSim.GamePhysics.WorkerSupervisor;

public sealed class GamePhysicsWorkerLaunchOptions
{
    public required string WorkerAssemblyPath { get; init; }
    public required Guid WorldId { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

public sealed class GamePhysicsWorkerSupervisor : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GamePhysicsWorkerLaunchOptions _options;
    private readonly Process _process;
    private readonly Stream _stream;
    private readonly string? _privateDirectory;
    private readonly Socket? _socketListener;
    private readonly NamedPipeServerStream? _pipeServer;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _heartbeatStop = new();
    private readonly Task _exitMonitor;
    private Task? _heartbeatTask;
    private long _sequence = 1;
    private int _state = (int)WorkerLifecycleState.Handshaking;
    private int _disposed;

    private GamePhysicsWorkerSupervisor(
        GamePhysicsWorkerLaunchOptions options,
        Process process,
        Stream stream,
        string? privateDirectory,
        Socket? socketListener,
        NamedPipeServerStream? pipeServer)
    {
        _options = options;
        _process = process;
        _stream = stream;
        _privateDirectory = privateDirectory;
        _socketListener = socketListener;
        _pipeServer = pipeServer;
        _exitMonitor = MonitorProcessExitAsync();
    }

    public WorkerLifecycleState State => (WorkerLifecycleState)Volatile.Read(ref _state);
    public int ProcessId => _process.Id;
    public Guid WorldId => _options.WorldId;

    public static async Task<GamePhysicsWorkerSupervisor> StartAsync(
        GamePhysicsWorkerLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.WorldId == Guid.Empty)
            throw new ArgumentException("World ID must be non-empty.", nameof(options));
        if (!File.Exists(options.WorkerAssemblyPath))
            throw new FileNotFoundException("Worker assembly was not found.", options.WorkerAssemblyPath);

        string? privateDirectory = null;
        Socket? listener = null;
        NamedPipeServerStream? pipe = null;
        string endpoint;
        bool windows = OperatingSystem.IsWindows();

        if (windows)
        {
            endpoint = "opensim-game-physics-" + Guid.NewGuid().ToString("N");
            pipe = new NamedPipeServerStream(
                endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        else
        {
            privateDirectory = Path.Combine(Path.GetTempPath(), "opensim-worker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(privateDirectory);
            File.SetUnixFileMode(privateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            endpoint = Path.Combine(privateDirectory, "worker.sock");
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(endpoint));
            listener.Listen(1);
        }

        byte[] token = RandomNumberGenerator.GetBytes(32);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(options.WorkerAssemblyPath);
        start.ArgumentList.Add(windows ? "--pipe" : "--socket");
        start.ArgumentList.Add(endpoint);

        Process? process = null;
        Stream? connectedStream = null;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Worker process did not start.");
            await process.StandardInput.WriteLineAsync(Convert.ToBase64String(token)).WaitAsync(options.StartupTimeout, cancellationToken);
            process.StandardInput.Close();

            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(options.StartupTimeout);
            Stream stream;
            if (windows)
            {
                await pipe!.WaitForConnectionAsync(startupCts.Token).ConfigureAwait(false);
                stream = pipe;
            }
            else
            {
                Socket socket = await listener!.AcceptAsync(startupCts.Token).ConfigureAwait(false);
                listener.Dispose();
                stream = new NetworkStream(socket, ownsSocket: true);
            }
            connectedStream = stream;

            var supervisor = new GamePhysicsWorkerSupervisor(options, process, stream, privateDirectory, listener, pipe);
            var hello = new WorkerProtocolEnvelope
            {
                Kind = WorkerMessageKind.Request,
                WorldId = options.WorldId,
                RequestId = Guid.NewGuid(),
                Sequence = 1,
                Operation = "Hello",
                Payload = JsonSerializer.SerializeToElement(new { token = Convert.ToBase64String(token) })
            };

            using JsonDocument response = await supervisor.ExchangeAsync(hello, options.StartupTimeout, cancellationToken).ConfigureAwait(false);
            if (!response.RootElement.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException("Worker rejected the authenticated handshake.");

            supervisor.SetState(WorkerLifecycleState.Ready);
            supervisor._heartbeatTask = supervisor.RunHeartbeatAsync();
            CryptographicOperations.ZeroMemory(token);
            return supervisor;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(token);
            try { if (process is not null && !process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            if (connectedStream is not null)
            {
                try { await connectedStream.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            if (process is not null)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
                process.Dispose();
            }
            pipe?.Dispose();
            listener?.Dispose();
            if (privateDirectory is not null && Directory.Exists(privateDirectory))
                Directory.Delete(privateDirectory, recursive: true);
            throw;
        }

    }

    public async Task<JsonElement> EchoAsync(JsonElement payload, CancellationToken cancellationToken = default) =>
        await RequestAsync("Echo", payload, cancellationToken).ConfigureAwait(false);

    public async Task<JsonElement> RequestAsync(
        string operation,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("Operation is required.", nameof(operation));
        using JsonDocument response = await SendRequestAsync(operation, payload, cancellationToken).ConfigureAwait(false);
        return response.RootElement.GetProperty("payload").Clone();
    }

    public async Task CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await SendRequestAsync("Ping", JsonSerializer.SerializeToElement(new { }), cancellationToken).ConfigureAwait(false);
        if (!response.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException("Worker health request failed.");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (State == WorkerLifecycleState.Stopped)
            return;
        if (State == WorkerLifecycleState.Faulted)
        {
            _heartbeatStop.Cancel();
            _lifetime.Cancel();
            if (_heartbeatTask is not null)
            {
                try { await _heartbeatTask.ConfigureAwait(false); } catch { }
            }
            TryKill();
            await CleanupTransportAsync().ConfigureAwait(false);
            return;
        }

        SetState(WorkerLifecycleState.Stopping);
        _heartbeatStop.Cancel();
        if (_heartbeatTask is not null)
        {
            try { await _heartbeatTask.ConfigureAwait(false); } catch { }
        }

        try
        {
            using JsonDocument response = await SendRequestAsync(
                "Shutdown", JsonSerializer.SerializeToElement(new { }), cancellationToken, allowStopping: true).ConfigureAwait(false);
            await _process.WaitForExitAsync(cancellationToken).WaitAsync(_options.ShutdownTimeout, cancellationToken).ConfigureAwait(false);
            _lifetime.Cancel();
            SetState(WorkerLifecycleState.Stopped);
        }
        catch
        {
            TryKill();
            _lifetime.Cancel();
            SetState(WorkerLifecycleState.Faulted);
            throw;
        }
        finally
        {
            await CleanupTransportAsync().ConfigureAwait(false);
        }
    }

    public async Task WaitForFaultAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        while (State != WorkerLifecycleState.Faulted)
            await Task.Delay(50, linked.Token).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendRequestAsync(
        string operation,
        JsonElement payload,
        CancellationToken cancellationToken,
        bool allowStopping = false)
    {
        if (!allowStopping && State != WorkerLifecycleState.Ready)
            throw new InvalidOperationException($"Worker is not ready (state: {State}).");

        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!allowStopping && State != WorkerLifecycleState.Ready)
                throw new InvalidOperationException($"Worker is not ready (state: {State}).");

            long sequence = ++_sequence;
            var request = new WorkerProtocolEnvelope
            {
                Kind = WorkerMessageKind.Request,
                WorldId = _options.WorldId,
                RequestId = Guid.NewGuid(),
                Sequence = sequence,
                Operation = operation,
                Payload = payload
            };
            return await ExchangeLockedAsync(request, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<JsonDocument> ExchangeAsync(
        WorkerProtocolEnvelope request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExchangeLockedAsync(request, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<JsonDocument> ExchangeLockedAsync(
        WorkerProtocolEnvelope request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            linked.CancelAfter(timeout);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            await WorkerFrameCodec.WriteAsync(_stream, bytes, WorkerProtocolConstants.DefaultMaxCommandFrameBytes, linked.Token).ConfigureAwait(false);
            JsonDocument response = await WorkerFrameCodec.ReadAsync(_stream, WorkerProtocolConstants.DefaultMaxCommandFrameBytes, linked.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("Worker closed the IPC stream.");
            WorkerProtocolEnvelope? envelope = response.RootElement.Deserialize<WorkerProtocolEnvelope>(JsonOptions);
            if (!WorkerProtocolValidator.TryValidate(envelope, out _) ||
                envelope!.Kind != WorkerMessageKind.Response ||
                envelope.WorldId != request.WorldId ||
                envelope.RequestId != request.RequestId ||
                envelope.Sequence != request.Sequence)
            {
                response.Dispose();
                throw new InvalidDataException("Worker response did not match its request.");
            }
            if (envelope.Ok != true)
            {
                string error = envelope.Error?.ToString() ?? "UnknownWorkerError";
                response.Dispose();
                throw new InvalidOperationException($"Worker request '{request.Operation}' failed: {error}.");
            }
            return response;
    }

    private async Task RunHeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_options.HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(_heartbeatStop.Token).ConfigureAwait(false))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_heartbeatStop.Token);
                timeout.CancelAfter(_options.HeartbeatTimeout);
                await CheckHealthAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_heartbeatStop.IsCancellationRequested) { }
        catch
        {
            SetState(WorkerLifecycleState.Faulted);
            TryKill();
        }
    }

    private async Task MonitorProcessExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            if (State is not (WorkerLifecycleState.Stopping or WorkerLifecycleState.Stopped))
                SetState(WorkerLifecycleState.Faulted);
        }
        catch { SetState(WorkerLifecycleState.Faulted); }
    }

    private void SetState(WorkerLifecycleState state) => Volatile.Write(ref _state, (int)state);

    private void TryKill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
    }

    private async Task CleanupTransportAsync()
    {
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
        _pipeServer?.Dispose();
        _socketListener?.Dispose();
        if (_privateDirectory is not null && Directory.Exists(_privateDirectory))
        {
            try { Directory.Delete(_privateDirectory, recursive: true); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (State is not (WorkerLifecycleState.Stopped or WorkerLifecycleState.Faulted))
        {
            try { await StopAsync().ConfigureAwait(false); }
            catch { TryKill(); }
        }
        _heartbeatStop.Cancel();
        _lifetime.Cancel();
        await CleanupTransportAsync().ConfigureAwait(false);
        try { await _exitMonitor.ConfigureAwait(false); } catch { }
        _requestLock.Dispose();
        _heartbeatStop.Dispose();
        _lifetime.Dispose();
        _process.Dispose();
    }
}
