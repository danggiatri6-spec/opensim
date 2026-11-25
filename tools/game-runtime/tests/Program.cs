using System.Text.Json;
using OpenMetaverse;
using OpenSim.GamePhysics.WorkerProtocol;
using OpenSim.GameRuntime;
using OpenSim.Services.Interfaces;

static void Assert(bool value, string message)
{
    if (!value) throw new Exception("ASSERT FAILED: " + message);
}

UUID owner = UUID.Random();
UUID foreign = UUID.Random();
UUID projectId = UUID.Random();
UUID worldId = UUID.Random();
var projects = new FakeProjectService(owner, projectId, worldId);
var workers = new FakeWorkerFactory();
var options = new GameRuntimeOptions
{
    Enabled = true,
    WorkerAssemblyPath = "worker.dll",
    MaximumActiveInstances = 4
};
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

Console.WriteLine("PASS: Game Runtime ownership, immutable revision validation, default world, duplicate reservation, stop/restart, fault propagation, and startup rollback.");

sealed class FakeProjectService : IGameProjectService
{
    public GameProject Project { get; }
    public GameProjectRevision Revision { get; }
    public FakeProjectService(UUID owner, UUID project, UUID world)
    {
        Project = new GameProject { ProjectID = project, OwnerID = owner, Name = "Test Game", Status = GameProjectStatus.Published, CurrentRevision = 1 };
        Revision = new GameProjectRevision { ProjectID = project, Revision = 1, CreatedBy = owner, SchemaVersion = 1, ManifestJson = CreateManifest(world) };
    }
    public static string CreateManifest(UUID world) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        name = "Test Game",
        worlds = new[] { new { worldID = world.ToString(), name = "Default World", description = "", sizeX = 256, sizeY = 256, spawnPoints = Array.Empty<object>(), assetReferences = Array.Empty<object>() } },
        settings = new { gameMode = "sandbox", defaultWorldID = world.ToString(), visibility = "private", maxPlayers = 16, tags = Array.Empty<string>() }
    });
    public GameProjectResult CreateProject(UUID owner, string name, string description) => throw new NotImplementedException();
    public GameProjectResult GetProject(UUID owner, UUID id) => owner == Project.OwnerID && id == Project.ProjectID
        ? new GameProjectResult { Error = GameProjectError.None, Project = Project }
        : new GameProjectResult { Error = GameProjectError.NotFound };
    public List<GameProject> GetOwnedProjects(UUID owner, int offset, int limit) => new();
    public GameProjectResult SaveDraft(UUID owner, UUID id, long expected, GameProjectManifest manifest) => throw new NotImplementedException();
    public GameProjectRevision? GetRevision(UUID owner, UUID id, long revision) =>
        owner == Project.OwnerID && id == Project.ProjectID && revision == Revision.Revision ? Revision : null;
    public GameProjectResult UpdateMetadata(UUID owner, UUID id, long expected, string name, string description) => throw new NotImplementedException();
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
    public Task<JsonElement> StopWorldAsync(CancellationToken cancellationToken) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new { state = "Stopped" }));
    public Task StopAsync(CancellationToken cancellationToken) { State = WorkerLifecycleState.Stopped; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { State = WorkerLifecycleState.Stopped; return ValueTask.CompletedTask; }
    public void SetFaulted() => State = WorkerLifecycleState.Faulted;
}
