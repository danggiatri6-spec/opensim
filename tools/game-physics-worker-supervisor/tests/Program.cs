using System.Diagnostics;
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
    Task<JsonElement>[] concurrent = Enumerable.Range(0, 12)
        .Select(i => supervisor.EchoAsync(JsonSerializer.SerializeToElement(new { index = i })))
        .ToArray();
    JsonElement[] concurrentResults = await Task.WhenAll(concurrent);
    Assert(concurrentResults.Select(x => x.GetProperty("index").GetInt32()).OrderBy(x => x).SequenceEqual(Enumerable.Range(0, 12)),
        "concurrent host requests preserve strict wire sequence ordering");
    JsonElement world = await supervisor.RequestAsync("CreateWorld", JsonSerializer.SerializeToElement(new
    {
        profile = "Balanced",
        gravity = new { x = 0f, y = -9.81f, z = 0f },
        maxDynamicBodies = 1
    }));
    Assert(world.GetProperty("state").GetString() == "Created", "fake adapter creates one bounded physics world");

    Guid invalidShapeId = Guid.NewGuid();
    bool invalidShapeRejected = false;
    try
    {
        await supervisor.RequestAsync("CreateBody", JsonSerializer.SerializeToElement(new
        {
            bodyId = invalidShapeId, mass = 1f, shape = "UnknownShape"
        }));
    }
    catch (InvalidOperationException) { invalidShapeRejected = true; }
    Assert(invalidShapeRejected, "fake adapter rejects unsupported collision shapes");

    Guid invalidMassId = Guid.NewGuid();
    bool invalidMassRejected = false;
    try
    {
        await supervisor.RequestAsync("CreateBody", JsonSerializer.SerializeToElement(new
        {
            bodyId = invalidMassId, mass = 0f, shape = "Box"
        }));
    }
    catch (InvalidOperationException) { invalidMassRejected = true; }
    Assert(invalidMassRejected, "fake adapter rejects zero-mass dynamic bodies");

    Guid bodyId = Guid.NewGuid();
    await supervisor.RequestAsync("CreateBody", JsonSerializer.SerializeToElement(new
    {
        bodyId, mass = 2f, shape = "Box", position = new { x = 0f, y = 10f, z = 0f }
    }));

    bool capacityRejected = false;
    try
    {
        await supervisor.RequestAsync("CreateBody", JsonSerializer.SerializeToElement(new
        {
            bodyId = Guid.NewGuid(), mass = 1f, shape = "Sphere"
        }));
    }
    catch (InvalidOperationException) { capacityRejected = true; }
    Assert(capacityRejected, "fake adapter enforces the configured dynamic-body capacity");
    await supervisor.RequestAsync("SetVelocity", JsonSerializer.SerializeToElement(new
    {
        bodyId, velocity = new { x = 1f, y = 0f, z = 0f }
    }));
    bool catchUpRejected = false;
    try
    {
        await supervisor.RequestAsync("Step", JsonSerializer.SerializeToElement(new { steps = 4 }));
    }
    catch (InvalidOperationException) { catchUpRejected = true; }
    Assert(catchUpRejected, "fake adapter rejects a step request beyond the bounded catch-up limit");

    JsonElement snapshot = default;
    for (int i = 0; i < 10; i++)
        snapshot = await supervisor.RequestAsync("Step", JsonSerializer.SerializeToElement(new { steps = 3 }));
    JsonElement bodyState = snapshot.GetProperty("bodies")[0];
    Assert(snapshot.GetProperty("tick").GetInt64() == 30 &&
        bodyState.GetProperty("position").GetProperty("y").GetSingle() < 10f,
        "fake adapter advances deterministic fixed 30 Hz gravity steps");

    await supervisor.RequestAsync("ApplyImpulse", JsonSerializer.SerializeToElement(new
    {
        bodyId, impulse = new { x = 2f, y = 0f, z = 0f }
    }));
    snapshot = await supervisor.RequestAsync("GetSnapshot", JsonSerializer.SerializeToElement(new { }));
    bodyState = snapshot.GetProperty("bodies")[0];
    Assert(bodyState.GetProperty("velocity").GetProperty("x").GetSingle() > 1.9f,
        "fake adapter applies mass-scaled impulses");

    await supervisor.RequestAsync("DestroyBody", JsonSerializer.SerializeToElement(new { bodyId }));
    snapshot = await supervisor.RequestAsync("GetSnapshot", JsonSerializer.SerializeToElement(new { }));
    Assert(snapshot.GetProperty("bodies").GetArrayLength() == 0, "fake adapter destroys bodies by opaque ID");

    Guid realtimeBodyId = Guid.NewGuid();
    await supervisor.RequestAsync("CreateBody", JsonSerializer.SerializeToElement(new
    {
        bodyId = realtimeBodyId, mass = 1f, shape = "Box",
        position = new { x = 0f, y = 20f, z = 0f }
    }));
    JsonElement started = await supervisor.RequestAsync("StartSimulation", JsonSerializer.SerializeToElement(new { }));
    Assert(started.GetProperty("state").GetString() == "Running", "worker starts the background fixed-step simulation");
    await Task.Delay(TimeSpan.FromMilliseconds(400));
    JsonElement paused = await supervisor.RequestAsync("PauseSimulation", JsonSerializer.SerializeToElement(new { }));
    snapshot = await supervisor.RequestAsync("GetSnapshot", JsonSerializer.SerializeToElement(new { }));
    long realtimeTicks = snapshot.GetProperty("tick").GetInt64();
    Assert(paused.GetProperty("state").GetString() == "Paused" && realtimeTicks >= 8 &&
        snapshot.GetProperty("bodies")[0].GetProperty("position").GetProperty("y").GetSingle() < 20f,
        "background simulation advances at approximately 30 Hz and pauses cleanly");
    await supervisor.RequestAsync("DestroyBody", JsonSerializer.SerializeToElement(new { bodyId = realtimeBodyId }));

    Guid boundaryBodyId = Guid.NewGuid();
    await supervisor.RequestAsync("CreateBody", JsonSerializer.SerializeToElement(new
    {
        bodyId = boundaryBodyId, mass = 1f, shape = "Sphere",
        position = new { x = 0f, y = 1_000_000f, z = 0f }
    }));
    await supervisor.RequestAsync("SetVelocity", JsonSerializer.SerializeToElement(new
    {
        bodyId = boundaryBodyId, velocity = new { x = 0f, y = 100f, z = 0f }
    }));
    bool worldBoundRejected = false;
    try
    {
        await supervisor.RequestAsync("Step", JsonSerializer.SerializeToElement(new { steps = 1 }));
    }
    catch (InvalidOperationException) { worldBoundRejected = true; }
    snapshot = await supervisor.RequestAsync("GetSnapshot", JsonSerializer.SerializeToElement(new { }));
    Assert(worldBoundRejected && snapshot.GetProperty("tick").GetInt64() == 30 &&
        snapshot.GetProperty("bodies")[0].GetProperty("position").GetProperty("y").GetSingle() == 1_000_000f,
        "fake adapter rejects out-of-bounds steps without partially advancing the world");
    await supervisor.RequestAsync("DestroyBody", JsonSerializer.SerializeToElement(new { bodyId = boundaryBodyId }));

    bool secondWorldRejected = false;
    try
    {
        await supervisor.RequestAsync("CreateWorld", JsonSerializer.SerializeToElement(new { profile = "Balanced" }));
    }
    catch (InvalidOperationException) { secondWorldRejected = true; }
    Assert(secondWorldRejected, "worker rejects a second world in the same process");

    await supervisor.RequestAsync("StopWorld", JsonSerializer.SerializeToElement(new { }));
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
