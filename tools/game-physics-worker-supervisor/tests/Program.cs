using System.Text.Json;
using OpenSim.GamePhysics.Worker;
using OpenSim.GamePhysics.WorkerProtocol;
using OpenSim.GamePhysics.WorkerSupervisor;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL " + message);
    Console.WriteLine("PASS " + message);
}

string workerAssembly = typeof(WorkerMarker).Assembly.Location;
GamePhysicsWorkerLaunchOptions Options() => new()
{
    WorkerAssemblyPath = workerAssembly,
    WorldId = Guid.NewGuid(),
    StartupTimeout = TimeSpan.FromSeconds(15),
    RequestTimeout = TimeSpan.FromSeconds(5),
    ShutdownTimeout = TimeSpan.FromSeconds(10),
    HeartbeatInterval = TimeSpan.FromSeconds(1),
    HeartbeatTimeout = TimeSpan.FromSeconds(3)
};

await using (GamePhysicsWorkerSupervisor supervisor =
    await GamePhysicsWorkerSupervisor.StartAsync(Options()))
{
    Assert(supervisor.State == WorkerLifecycleState.Ready, "supervisor reaches Ready after authenticated startup");
    JsonElement echo = await supervisor.EchoAsync(JsonSerializer.SerializeToElement(new { message = "host-supervisor", value = 42 }));
    Assert(echo.GetProperty("message").GetString() == "host-supervisor", "supervisor correlates and returns Echo response");
    await supervisor.CheckHealthAsync();
    Assert(true, "supervisor completes worker health ping");
    await supervisor.StopAsync();
    Assert(supervisor.State == WorkerLifecycleState.Stopped, "supervisor performs graceful worker shutdown");
}

await using (GamePhysicsWorkerSupervisor supervisor =
    await GamePhysicsWorkerSupervisor.StartAsync(Options()))
{
    int pid = supervisor.ProcessId;
    using Process child = Process.GetProcessById(pid);
    child.Kill(entireProcessTree: true);
    await supervisor.WaitForFaultAsync(TimeSpan.FromSeconds(8));
    Assert(supervisor.State == WorkerLifecycleState.Faulted, "supervisor detects unexpected worker process exit");
}

Console.WriteLine("All Game Physics Worker Supervisor tests passed.");
