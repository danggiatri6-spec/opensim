using System.Text.Json;
using OpenMetaverse;
using Nini.Config;
using OpenSim.GamePhysics.WorkerProtocol;
using OpenSim.GameRuntime;
using OpenSim.Services.Interfaces;

static void Assert(bool value, string message)
{
    if (!value) throw new Exception("ASSERT FAILED: " + message);
}

GameCreatorRequestAuthenticatorTests.Run();
GameCreatorApiHandlerTests.Run();
GameCreatorConnectorTests.Run();

UUID owner = UUID.Random();
UUID foreign = UUID.Random();
UUID projectId = UUID.Random();
UUID worldId = UUID.Random();
var projects = new FakeProjectService(owner, projectId, worldId);
var workers = new FakeWorkerFactory();
await using (var disabledPlugin = new GameRuntimeServicePlugin(new IniConfigSource()))
{
    GameInstanceResult pluginDisabledStart = await disabledPlugin.StartInstanceAsync(owner, projectId, 1);
    Assert(pluginDisabledStart.Error == GameInstanceError.WorkerUnavailable,
        "config-driven service plugin defaults to disabled without initializing project storage");
}

await using (var disabledRuntime = new GameRuntimeService(projects, new FakeWorkerFactory(), new GameRuntimeOptions()))
{
    GameInstanceResult disabledStart = await disabledRuntime.StartInstanceAsync(owner, projectId, 1);
    Assert(disabledStart.Error == GameInstanceError.WorkerUnavailable,
        "Game Runtime remains disabled by default without requiring a worker deployment path");
}

var options = new GameRuntimeOptions
{
    Enabled = true,
    WorkerAssemblyPath = "worker.dll",
    MaximumActiveInstances = 4
};
var v2Projects = new FakeProjectService(owner, projectId, worldId);
v2Projects.Revision.SchemaVersion = 2;
v2Projects.RuntimeExperienceID = UUID.Random();
v2Projects.SecondaryRuntimeExperienceID = UUID.Random();
v2Projects.SecondaryRuntimeWorld = new GameWorldDefinition
{
    WorldID = UUID.Random(),
    Name = "Secondary v2 World",
    SizeX = 256,
    SizeY = 256
};
v2Projects.RuntimeWorld = new GameWorldDefinition
{
    WorldID = worldId,
    Name = "Pinned v2 World",
    SizeX = 256,
    SizeY = 256
};
var v2Workers = new FakeWorkerFactory();
await using (var v2Runtime = new GameRuntimeService(v2Projects, v2Workers, options))
{
    GameInstanceResult v2Started = await v2Runtime.StartInstanceAsync(
        owner, projectId, 1, v2Projects.RuntimeExperienceID);
    Assert(v2Started.Success && v2Projects.LastResolvedRevision == 1
        && v2Projects.LastResolvedExperienceID == v2Projects.RuntimeExperienceID
        && v2Started.Instance?.ExperienceID == v2Projects.RuntimeExperienceID,
        "v2 Game Instance resolves the exact requested project and Experience revisions");
    Assert(v2Workers.Sessions.Count == 1
        && v2Workers.Sessions[0].LastConfiguration.GetProperty("worldName").GetString() == "Pinned v2 World",
        "v2 runtime resolver supplies the Place world definition to the Worker");
    GameInstanceResult v2SecondaryStarted = await v2Runtime.StartInstanceAsync(
        owner, projectId, 1, v2Projects.SecondaryRuntimeExperienceID);
    Assert(v2SecondaryStarted.Success
        && v2SecondaryStarted.Instance?.ExperienceID == v2Projects.SecondaryRuntimeExperienceID,
        "separate pinned Experiences can run as independent Game Instances for one project revision");
    Assert(v2Workers.Sessions[^1].LastConfiguration.GetProperty("worldName").GetString() == "Secondary v2 World",
        "selected secondary Experience resolves its own start Place");
    await v2Runtime.StopInstanceAsync(owner, v2Started.Instance!.InstanceID);
    await v2Runtime.StopInstanceAsync(owner, v2SecondaryStarted.Instance!.InstanceID);
}

await using var runtime = new GameRuntimeService(projects, workers, options);

GameInstanceResult started = await runtime.StartInstanceAsync(owner, projectId, 1);
Assert(started.Success && started.Instance?.State == GameInstanceState.Running, "owner starts a valid revision");
Assert(workers.Sessions.Count == 1, "one worker created");
Assert(workers.Sessions[0].Started, "fixed-step simulation starts after world creation");
Assert(workers.Sessions[0].LastConfiguration.GetProperty("profile").GetString() == "Balanced", "Balanced profile enforced");
Assert(workers.Sessions[0].LastConfiguration.GetProperty("worldName").GetString() == "Default World", "default world resolved from immutable manifest");

GameInstanceResult duplicate = await runtime.StartInstanceAsync(owner, projectId, 1);
Assert(duplicate.Error == GameInstanceError.AlreadyRunning, "duplicate reservation rejected");
GameInstanceResult hidden = await runtime.GetInstanceAsync(foreign, started.Instance!.InstanceID);
Assert(hidden.Error == GameInstanceError.NotFound, "foreign owner cannot inspect instance");

GameInstanceResult stopped = await runtime.StopInstanceAsync(owner, started.Instance.InstanceID);
Assert(stopped.Success && stopped.Instance?.State == GameInstanceState.Stopped, "instance stops cleanly");
GameInstanceResult restarted = await runtime.StartInstanceAsync(owner, projectId, 1);
Assert(restarted.Success, "stopped project revision can be started again");

workers.Sessions[^1].SetFaulted();
await Task.Delay(250);
GameInstanceResult faulted = await runtime.GetInstanceAsync(owner, restarted.Instance!.InstanceID);
Assert(faulted.Success && faulted.Instance?.State == GameInstanceState.Faulted, "worker fault propagates to its instance");
Assert(workers.Sessions[^1].Disposed, "faulted worker session is disposed after fault propagation");

projects.Project.Status = GameProjectStatus.Archived;
GameInstanceResult archived = await runtime.StartInstanceAsync(owner, projectId, 1);
Assert(archived.Error == GameInstanceError.InvalidRevision, "archived project cannot start");

projects.Project.Status = GameProjectStatus.Published;
projects.Revision.ManifestJson = "{invalid";
GameInstanceResult malformed = await runtime.StartInstanceAsync(owner, projectId, 1);
Assert(malformed.Error == GameInstanceError.InvalidManifest, "stored manifest is reparsed and rejected when malformed");

var failingFactory = new FakeWorkerFactory { FailNextStart = true };
await using var retryRuntime = new GameRuntimeService(projects, failingFactory, options);
projects.Revision.ManifestJson = FakeProjectService.CreateManifest(worldId);
GameInstanceResult failed = await retryRuntime.StartInstanceAsync(owner, projectId, 1);
Assert(failed.Error == GameInstanceError.WorkerUnavailable, "worker startup failure is typed");
GameInstanceResult retry = await retryRuntime.StartInstanceAsync(owner, projectId, 1);
Assert(retry.Success, "startup failure releases its reservation");

var stopFailureFactory = new FakeWorkerFactory();
await using (var stopFailureRuntime = new GameRuntimeService(projects, stopFailureFactory, options))
{
    GameInstanceResult stopFailureStart = await stopFailureRuntime.StartInstanceAsync(owner, projectId, 1);
    stopFailureFactory.Sessions[0].FailStopWorld = true;
    GameInstanceResult stopFailure = await stopFailureRuntime.StopInstanceAsync(owner, stopFailureStart.Instance!.InstanceID);
    Assert(stopFailure.Error == GameInstanceError.WorkerUnavailable, "worker stop failure is returned as a typed service error");
    GameInstanceResult stopFailureState = await stopFailureRuntime.GetInstanceAsync(owner, stopFailureStart.Instance.InstanceID);
    Assert(stopFailureState.Instance?.State == GameInstanceState.Faulted && stopFailureFactory.Sessions[0].Disposed,
        "failed stop faults the instance and disposes the worker session");
}

var shutdownProjects = new FakeProjectService(owner, projectId, worldId);
UUID secondProjectId = UUID.Random();
UUID secondWorldId = UUID.Random();
shutdownProjects.AddProject(owner, secondProjectId, secondWorldId);
var shutdownWorkers = new FakeWorkerFactory();
var shutdownRuntime = new GameRuntimeService(shutdownProjects, shutdownWorkers, options);
GameInstanceResult shutdownFirst = await shutdownRuntime.StartInstanceAsync(owner, projectId, 1);
GameInstanceResult shutdownSecond = await shutdownRuntime.StartInstanceAsync(owner, secondProjectId, 1);
Assert(shutdownFirst.Success && shutdownSecond.Success, "host can own multiple independent Game Instances");
await shutdownRuntime.DisposeAsync();
GameInstanceResult shutdownFirstState = await shutdownRuntime.GetInstanceAsync(owner, shutdownFirst.Instance!.InstanceID);
GameInstanceResult shutdownSecondState = await shutdownRuntime.GetInstanceAsync(owner, shutdownSecond.Instance!.InstanceID);
Assert(shutdownFirstState.Instance?.State == GameInstanceState.Stopped &&
    shutdownSecondState.Instance?.State == GameInstanceState.Stopped &&
    shutdownWorkers.Sessions.Count == 2 && shutdownWorkers.Sessions.All(s => s.Disposed),
    "host shutdown gracefully stops and disposes every live instance independently");

var workerAssemblyPath = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory,
    "../../../../../game-physics-worker/bin/Release/net8.0/GamePhysicsWorker.dll"));
Assert(File.Exists(workerAssemblyPath), "real worker assembly is built for process-level acceptance");
var realOptions = new GameRuntimeOptions
{
    Enabled = true,
    WorkerAssemblyPath = workerAssemblyPath,
    StartupTimeout = TimeSpan.FromSeconds(15),
    RequestTimeout = TimeSpan.FromSeconds(5),
    ShutdownTimeout = TimeSpan.FromSeconds(10),
    HeartbeatInterval = TimeSpan.FromSeconds(1),
    HeartbeatTimeout = TimeSpan.FromSeconds(3)
};
await using (var realRuntime = new GameRuntimeService(projects, new GamePhysicsWorkerFactory(realOptions), realOptions))
{
    GameInstanceResult realStarted = await realRuntime.StartInstanceAsync(owner, projectId, 1);
    Assert(realStarted.Success && realStarted.Instance?.State == GameInstanceState.Running,
        "real child Worker authenticates, creates its world, and starts simulation");
    await Task.Delay(250);
    GameInstanceResult realStopped = await realRuntime.StopInstanceAsync(owner, realStarted.Instance!.InstanceID);
    Assert(realStopped.Success && realStopped.Instance?.State == GameInstanceState.Stopped,
        "real child Worker stops its world, shuts down, and is reaped");
}

Console.WriteLine("PASS: Game Runtime ownership, immutable revision validation, default world, duplicate reservation, stop/restart, fault propagation, and startup rollback.");

sealed class FakeProjectService : IGameProjectService, IGameProjectRuntimeDefinitionService
{
    public GameProject Project { get; }
    public GameProjectRevision Revision { get; }
    public GameProject? SecondProject { get; private set; }
    public GameProjectRevision? SecondRevision { get; private set; }
    public GameWorldDefinition? RuntimeWorld { get; set; }
    public UUID RuntimeExperienceID { get; set; }
    public UUID SecondaryRuntimeExperienceID { get; set; }
    public GameWorldDefinition? SecondaryRuntimeWorld { get; set; }
    public long LastResolvedRevision { get; private set; }
    public UUID LastResolvedExperienceID { get; private set; }
    public GameWorldDefinition? ResolveRuntimeWorld(UUID owner, UUID project, long revision)
    {
        LastResolvedRevision = revision;
        return owner == Project.OwnerID && project == Project.ProjectID
            && revision == Revision.Revision && Revision.SchemaVersion == 2 ? RuntimeWorld : null;
    }
    public GameRuntimeWorldDefinition? ResolveRuntimeDefinition(
        UUID owner, UUID project, long revision, UUID experienceID)
    {
        LastResolvedRevision = revision;
        LastResolvedExperienceID = experienceID;
        if (owner != Project.OwnerID || project != Project.ProjectID
            || revision != Revision.Revision || Revision.SchemaVersion != 2)
            return null;
        if (experienceID == UUID.Zero)
            return SecondaryRuntimeExperienceID == UUID.Zero
                ? new GameRuntimeWorldDefinition { ExperienceID = RuntimeExperienceID, World = RuntimeWorld! }
                : null;
        if (experienceID == RuntimeExperienceID)
            return new GameRuntimeWorldDefinition { ExperienceID = RuntimeExperienceID, World = RuntimeWorld! };
        if (experienceID == SecondaryRuntimeExperienceID)
            return new GameRuntimeWorldDefinition { ExperienceID = SecondaryRuntimeExperienceID, World = SecondaryRuntimeWorld! };
        return null;
    }
    public FakeProjectService(UUID owner, UUID project, UUID world)
    {
        Project = new GameProject { ProjectID = project, OwnerID = owner, Name = "Test Game", Status = GameProjectStatus.Published, CurrentRevision = 1 };
        Revision = new GameProjectRevision { ProjectID = project, Revision = 1, CreatedBy = owner, SchemaVersion = 1, ManifestJson = CreateManifest(world) };
    }
    public void AddProject(UUID owner, UUID project, UUID world)
    {
        SecondProject = new GameProject { ProjectID = project, OwnerID = owner, Name = "Second Game", Status = GameProjectStatus.Published, CurrentRevision = 1 };
        SecondRevision = new GameProjectRevision { ProjectID = project, Revision = 1, CreatedBy = owner, SchemaVersion = 1, ManifestJson = CreateManifest(world) };
    }
    public static string CreateManifest(UUID world) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        name = "Test Game",
        worlds = new[] { new { worldID = world.ToString(), name = "Default World", description = "", sizeX = 256, sizeY = 256, spawnPoints = Array.Empty<object>(), assetReferences = Array.Empty<object>() } },
        settings = new { gameMode = "sandbox", defaultWorldID = world.ToString(), visibility = "private", maxPlayers = 16, tags = Array.Empty<string>() }
    });
    public GameProjectResult CreateProject(UUID owner, string name, string description) => throw new NotImplementedException();
    public GameProjectResult GetProject(UUID owner, UUID id)
    {
        if (owner == Project.OwnerID && id == Project.ProjectID)
            return new GameProjectResult { Error = GameProjectError.None, Project = Project };
        if (SecondProject is not null && owner == SecondProject.OwnerID && id == SecondProject.ProjectID)
            return new GameProjectResult { Error = GameProjectError.None, Project = SecondProject };
        return new GameProjectResult { Error = GameProjectError.NotFound };
    }
    public List<GameProject> GetOwnedProjects(UUID owner, int offset, int limit) => new();
    public GameProjectResult SaveDraft(UUID owner, UUID id, long expected, GameProjectManifest manifest) => throw new NotImplementedException();
    public GameProjectRevision? GetRevision(UUID owner, UUID id, long revision)
    {
        if (owner == Project.OwnerID && id == Project.ProjectID && revision == Revision.Revision)
            return Revision;
        if (SecondProject is not null && SecondRevision is not null &&
            owner == SecondProject.OwnerID && id == SecondProject.ProjectID && revision == SecondRevision.Revision)
            return SecondRevision;
        return null;
    }
    public GameProjectResult UpdateMetadata(UUID owner, UUID id, long expected, string name, string description) => throw new NotImplementedException();
    public GameProjectResult PublishProject(UUID owner, UUID id, long expected) => throw new NotImplementedException();
    public bool ArchiveProject(UUID owner, UUID id) => false;
}

sealed class FakeWorkerFactory : IGamePhysicsWorkerFactory
{
    public List<FakeSession> Sessions { get; } = new();
    public bool FailNextStart { get; set; }
    public Task<IGamePhysicsWorkerSession> StartAsync(Guid worldId, CancellationToken cancellationToken)
    {
        if (FailNextStart) { FailNextStart = false; throw new IOException("synthetic startup failure"); }
        var session = new FakeSession();
        Sessions.Add(session);
        return Task.FromResult<IGamePhysicsWorkerSession>(session);
    }
}

sealed class FakeSession : IGamePhysicsWorkerSession
{
    public WorkerLifecycleState State { get; private set; } = WorkerLifecycleState.Ready;
    public JsonElement LastConfiguration { get; private set; }
    public Task<JsonElement> CreateWorldAsync(JsonElement configuration, CancellationToken cancellationToken)
    {
        LastConfiguration = configuration.Clone();
        return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "Created" }));
    }
    public Task<JsonElement> StartSimulationAsync(CancellationToken cancellationToken)
    {
        Started = true;
        return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "Running" }));
    }
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    public bool FailStopWorld { get; set; }
    public Task<JsonElement> StopWorldAsync(CancellationToken cancellationToken)
    {
        if (FailStopWorld) throw new IOException("synthetic stop failure");
        return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "Stopped" }));
    }
    public Task StopAsync(CancellationToken cancellationToken) { State = WorkerLifecycleState.Stopped; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Disposed = true; State = WorkerLifecycleState.Stopped; return ValueTask.CompletedTask; }
    public void SetFaulted() => State = WorkerLifecycleState.Faulted;
}
