using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenSim.GamePhysics.Worker;
using OpenSim.GamePhysics.WorkerProtocol;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL " + message);
    Console.WriteLine("PASS " + message);
}

static byte[] JsonBytes(WorkerProtocolEnvelope envelope) => JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));

static async Task<JsonDocument?> SendAsync(Stream stream, WorkerProtocolEnvelope request)
{
    await WorkerFrameCodec.WriteAsync(stream, JsonBytes(request), WorkerProtocolConstants.DefaultMaxCommandFrameBytes);
    return await WorkerFrameCodec.ReadAsync(stream, WorkerProtocolConstants.DefaultMaxCommandFrameBytes);
}

static async Task<Stream> AcceptAsync(bool windows, string endpoint, Socket? listener, NamedPipeServerStream? pipe)
{
    if (windows)
    {
        await pipe!.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return pipe;
    }
    Socket socket = await listener!.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(15));
    return new NetworkStream(socket, ownsSocket: true);
}

static async Task RunRoundTripAsync()
{
    bool windows = OperatingSystem.IsWindows();
    string root = Path.Combine(Path.GetTempPath(), "opensim-worker-" + Guid.NewGuid().ToString("N"));
    string endpoint;
    Socket? listener = null;
    NamedPipeServerStream? pipe = null;
    if (windows)
    {
        endpoint = "opensim-game-physics-" + Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
    else
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        endpoint = Path.Combine(root, "worker.sock");
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(endpoint));
        listener.Listen(1);
    }

    string workerDll = typeof(WorkerMarker).Assembly.Location;
    var start = new System.Diagnostics.ProcessStartInfo("dotnet")
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    start.ArgumentList.Add(workerDll);
    start.ArgumentList.Add(windows ? "--pipe" : "--socket");
    start.ArgumentList.Add(endpoint);
    using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Worker process did not start.");
    byte[] token = RandomNumberGenerator.GetBytes(32);
    await process.StandardInput.WriteLineAsync(Convert.ToBase64String(token));
    process.StandardInput.Close();

    await using Stream stream = await AcceptAsync(windows, endpoint, listener, pipe);
    Guid worldId = Guid.NewGuid();
    var hello = new WorkerProtocolEnvelope
    {
        Kind = WorkerMessageKind.Request, WorldId = worldId, RequestId = Guid.NewGuid(),
        Sequence = 1, Operation = "Hello",
        Payload = JsonSerializer.SerializeToElement(new { token = Convert.ToBase64String(token) })
    };
    using (JsonDocument? ready = await SendAsync(stream, hello))
    {
        if (ready is not null) Console.WriteLine("Handshake response: " + ready.RootElement.GetRawText());
        else
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine("Worker exit code: " + process.ExitCode);
            Console.WriteLine("Worker diagnostic: " + await process.StandardError.ReadToEndAsync());
        }
        Assert(ready is not null && ready.RootElement.GetProperty("ok").GetBoolean(), "worker authenticates host and completes handshake");
    }

    var echo = new WorkerProtocolEnvelope
    {
        Kind = WorkerMessageKind.Request, WorldId = worldId, RequestId = Guid.NewGuid(),
        Sequence = 2, Operation = "Echo",
        Payload = JsonSerializer.SerializeToElement(new { message = "opensim-next", count = 7 })
    };
    using (JsonDocument? echoed = await SendAsync(stream, echo))
    {
        Assert(echoed is not null &&
            echoed.RootElement.GetProperty("payload").GetProperty("message").GetString() == "opensim-next",
            "worker echoes a framed request over local IPC");
    }

    var shutdown = new WorkerProtocolEnvelope
    {
        Kind = WorkerMessageKind.Request, WorldId = worldId, RequestId = Guid.NewGuid(),
        Sequence = 3, Operation = "Shutdown",
        Payload = JsonSerializer.SerializeToElement(new { })
    };
    using (JsonDocument? stopped = await SendAsync(stream, shutdown))
        Assert(stopped is not null && stopped.RootElement.GetProperty("ok").GetBoolean(), "worker acknowledges graceful shutdown");

    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Assert(process.ExitCode == 0, "worker process exits cleanly");
    listener?.Dispose();
    pipe?.Dispose();
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static async Task RunForcedTerminationAsync()
{
    string workerDll = typeof(WorkerMarker).Assembly.Location;
    var start = new System.Diagnostics.ProcessStartInfo("dotnet")
    {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true
    };
    start.ArgumentList.Add(workerDll);
    start.ArgumentList.Add("--socket");
    start.ArgumentList.Add(Path.Combine(Path.GetTempPath(), "missing-worker-" + Guid.NewGuid().ToString("N") + ".sock"));
    using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Worker process did not start.");
    process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Assert(process.HasExited, "host can forcibly terminate a worker without affecting the test host");
}

static async Task RunAuthenticationRejectionAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "opensim-worker-auth-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    string endpoint = Path.Combine(root, "worker.sock");
    using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    listener.Bind(new UnixDomainSocketEndPoint(endpoint));
    listener.Listen(1);

    string workerDll = typeof(WorkerMarker).Assembly.Location;
    var start = new System.Diagnostics.ProcessStartInfo("dotnet")
    {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true
    };
    start.ArgumentList.Add(workerDll);
    start.ArgumentList.Add("--socket");
    start.ArgumentList.Add(endpoint);
    using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Worker process did not start.");
    byte[] expectedToken = RandomNumberGenerator.GetBytes(32);
    byte[] wrongToken = RandomNumberGenerator.GetBytes(32);
    await process.StandardInput.WriteLineAsync(Convert.ToBase64String(expectedToken));
    process.StandardInput.Close();

    using Socket client = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(15));
    await using var stream = new NetworkStream(client, ownsSocket: false);
    var hello = new WorkerProtocolEnvelope
    {
        Kind = WorkerMessageKind.Request, WorldId = Guid.NewGuid(), RequestId = Guid.NewGuid(),
        Sequence = 1, Operation = "Hello",
        Payload = JsonSerializer.SerializeToElement(new { token = Convert.ToBase64String(wrongToken) })
    };
    using (JsonDocument? rejected = await SendAsync(stream, hello))
    {
        Assert(rejected is not null &&
            rejected.RootElement.GetProperty("ok").GetBoolean() == false &&
            rejected.RootElement.GetProperty("error").GetString() == "AuthenticationFailed",
            "worker rejects a mismatched capability token");
    }
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Assert(process.ExitCode == 5, "worker exits after failed authentication");
    CryptographicOperations.ZeroMemory(expectedToken);
    CryptographicOperations.ZeroMemory(wrongToken);
    Directory.Delete(root, recursive: true);
}

static async Task RunSequenceReplayRejectionAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "opensim-worker-seq-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    string endpoint = Path.Combine(root, "worker.sock");
    using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    listener.Bind(new UnixDomainSocketEndPoint(endpoint));
    listener.Listen(1);

    string workerDll = typeof(WorkerMarker).Assembly.Location;
    var start = new System.Diagnostics.ProcessStartInfo("dotnet")
    {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true
    };
    start.ArgumentList.Add(workerDll);
    start.ArgumentList.Add("--socket");
    start.ArgumentList.Add(endpoint);
    using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Worker process did not start.");
    byte[] token = RandomNumberGenerator.GetBytes(32);
    await process.StandardInput.WriteLineAsync(Convert.ToBase64String(token));
    process.StandardInput.Close();

    using Socket client = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(15));
    await using var stream = new NetworkStream(client, ownsSocket: false);
    Guid worldId = Guid.NewGuid();
    var hello = new WorkerProtocolEnvelope
    {
        Kind = WorkerMessageKind.Request, WorldId = worldId, RequestId = Guid.NewGuid(),
        Sequence = 1, Operation = "Hello",
        Payload = JsonSerializer.SerializeToElement(new { token = Convert.ToBase64String(token) })
    };
    using (JsonDocument? ready = await SendAsync(stream, hello))
        Assert(ready is not null && ready.RootElement.GetProperty("ok").GetBoolean(), "sequence replay test authenticates worker");

    var replay = new WorkerProtocolEnvelope
    {
        Kind = WorkerMessageKind.Request, WorldId = worldId, RequestId = Guid.NewGuid(),
        Sequence = 3, Operation = "Echo",
        Payload = JsonSerializer.SerializeToElement(new { shouldReject = true })
    };
    using (JsonDocument? response = await SendAsync(stream, replay))
        Assert(response is null, "worker closes the session on an out-of-order command sequence");

    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Assert(process.ExitCode == 6, "worker reports invalid command sequence through process exit status");
    CryptographicOperations.ZeroMemory(token);
    Directory.Delete(root, recursive: true);
}

await RunRoundTripAsync();
await RunAuthenticationRejectionAsync();
await RunSequenceReplayRejectionAsync();
await RunForcedTerminationAsync();
Console.WriteLine("All Game Physics Worker integration tests passed.");
