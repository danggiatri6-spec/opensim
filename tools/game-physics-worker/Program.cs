using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using OpenSim.GamePhysics.WorkerProtocol;

namespace OpenSim.GamePhysics.Worker;

public static class WorkerMarker { }

internal static class Program
{
    private const int MaxFrameBytes = WorkerProtocolConstants.DefaultMaxCommandFrameBytes;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 2)
                throw new ArgumentException("Expected exactly one transport option and endpoint.");

            Stream stream = args[0] switch
            {
                "--pipe" => await ConnectPipeAsync(args[1]),
                "--socket" => await ConnectSocketAsync(args[1]),
                _ => throw new ArgumentException("Unsupported IPC transport.")
            };

            await using (stream.ConfigureAwait(false))
            {
                return await RunSessionAsync(stream);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Worker terminated: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static async Task<Stream> ConnectPipeAsync(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(15000);
        return pipe;
    }

    private static async Task<Stream> ConnectSocketAsync(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        return new NetworkStream(socket, ownsSocket: true);
    }

    private static async Task<int> RunSessionAsync(Stream stream)
    {
        using JsonDocument? first = await WorkerFrameCodec.ReadAsync(stream, MaxFrameBytes);
        if (first is null)
            return 3;

        WorkerProtocolEnvelope? hello = first.RootElement.Deserialize<WorkerProtocolEnvelope>(JsonOptions);
        if (!WorkerProtocolValidator.TryValidate(hello, out _) ||
            hello!.Kind != WorkerMessageKind.Request ||
            hello.Operation != "Hello" ||
            hello.Sequence != 1 ||
            hello.Payload.ValueKind != JsonValueKind.Object ||
            !hello.Payload.TryGetProperty("token", out JsonElement tokenElement) ||
            tokenElement.ValueKind != JsonValueKind.String)
        {
            Console.Error.WriteLine("Handshake rejected: invalid envelope or missing token.");
            return 4;
        }

        string? launchToken = await Console.In.ReadLineAsync();
        byte[] supplied;
        byte[] expected;
        try
        {
            supplied = Convert.FromBase64String(tokenElement.GetString() ?? string.Empty);
            expected = Convert.FromBase64String(launchToken ?? string.Empty);
        }
        catch (FormatException)
        {
            Console.Error.WriteLine("Handshake rejected: invalid capability token encoding.");
            return 5;
        }
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            Console.Error.WriteLine("Handshake rejected: capability token mismatch.");
            await WriteResponseAsync(stream, hello, false, WorkerErrorCode.AuthenticationFailed, default);
            return 5;
        }

        await using var runtime = new WorkerSimulationRuntime(new FakePhysicsWorldAdapter());
        runtime.SetWorldId(hello.WorldId);
        using var writerLock = new SemaphoreSlim(1, 1);
        using var eventStop = new CancellationTokenSource();
        Task eventPump = PublishSnapshotsAsync(runtime, stream, hello.WorldId, writerLock, eventStop.Token);
        await WriteResponseAsync(stream, hello, true, null, JsonSerializer.SerializeToElement(new { state = "Ready" }), writerLock);

        long lastSequence = 1;
        while (true)
        {
            using JsonDocument? document = await WorkerFrameCodec.ReadAsync(stream, MaxFrameBytes);
            if (document is null)
            {
                eventStop.Cancel();
                try { await eventPump.ConfigureAwait(false); } catch (OperationCanceledException) { }
                return 0;
            }

            WorkerProtocolEnvelope? request = document.RootElement.Deserialize<WorkerProtocolEnvelope>(JsonOptions);
            if (!WorkerProtocolValidator.TryValidate(request, out _) ||
                request!.Kind != WorkerMessageKind.Request ||
                request.WorldId != hello.WorldId ||
                request.Sequence != lastSequence + 1)
            {
                return 6;
            }

            lastSequence = request.Sequence;
            if (request.Operation == "Shutdown")
            {
                await WriteResponseAsync(stream, request, true, null, JsonSerializer.SerializeToElement(new { state = "Stopped" }), writerLock);
                eventStop.Cancel();
                try { await eventPump.ConfigureAwait(false); } catch (OperationCanceledException) { }
                return 0;
            }

            if (request.Operation == "Ping")
            {
                JsonElement health = await runtime.ExecuteAsync("GetHealth", JsonSerializer.SerializeToElement(new { }));
                await WriteResponseAsync(stream, request, true, null, health, writerLock);
                continue;
            }

            if (request.Operation == "Echo")
            {
                await WriteResponseAsync(stream, request, true, null, request.Payload, writerLock);
                continue;
            }

            try
            {
                JsonElement result = await runtime.ExecuteAsync(request.Operation!, request.Payload);
                await WriteResponseAsync(stream, request, true, null, result, writerLock);
            }
            catch (WorkerCommandException ex)
            {
                await WriteResponseAsync(stream, request, false, ex.Code,
                    JsonSerializer.SerializeToElement(new { message = ex.Message }), writerLock);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Worker command failed: {ex.GetType().Name}");
                await WriteResponseAsync(stream, request, false, WorkerErrorCode.InternalError,
                    JsonSerializer.SerializeToElement(new { message = "Worker command failed." }), writerLock);
            }
        }
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        WorkerProtocolEnvelope request,
        bool ok,
        WorkerErrorCode? error,
        JsonElement payload,
        SemaphoreSlim writerLock)
    {
        if (payload.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            payload = JsonSerializer.SerializeToElement(new { });

        var response = new WorkerProtocolEnvelope
        {
            Kind = WorkerMessageKind.Response,
            WorldId = request.WorldId,
            RequestId = request.RequestId,
            Sequence = request.Sequence,
            Ok = ok,
            Error = error,
            Payload = payload
        };

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        await writerLock.WaitAsync().ConfigureAwait(false);
        try { await WorkerFrameCodec.WriteAsync(stream, bytes, MaxFrameBytes).ConfigureAwait(false); }
        finally { writerLock.Release(); }
    }

    private static async Task PublishSnapshotsAsync(
        WorkerSimulationRuntime runtime, Stream stream, Guid worldId,
        SemaphoreSlim writerLock, CancellationToken cancellationToken)
    {
        long eventSequence = 0;
        await foreach (JsonElement snapshot in runtime.Snapshots.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var message = new WorkerProtocolEnvelope
            {
                Kind = WorkerMessageKind.Event,
                WorldId = worldId,
                Sequence = eventSequence + 1,
                EventSequence = ++eventSequence,
                Payload = JsonSerializer.SerializeToElement(new { type = "Snapshot", snapshot })
            };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            await writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WorkerFrameCodec.WriteAsync(stream, bytes, WorkerProtocolConstants.DefaultMaxSnapshotFrameBytes, cancellationToken).ConfigureAwait(false);
            }
            finally { writerLock.Release(); }
        }
    }
}
