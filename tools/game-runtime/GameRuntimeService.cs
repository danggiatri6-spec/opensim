using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMetaverse;
using OpenSim.GamePhysics.WorkerSupervisor;
using OpenSim.Services.Interfaces;
using WorkerState = OpenSim.GamePhysics.WorkerProtocol.WorkerLifecycleState;

namespace OpenSim.GameRuntime;

public sealed class GameRuntimeOptions
{
    public bool Enabled { get; init; }
    public required string WorkerAssemblyPath { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public int MaximumActiveInstances { get; init; } = 16;
}

public interface IGamePhysicsWorkerSession : IAsyncDisposable
{
    WorkerState State { get; }
    Task<JsonElement> CreateWorldAsync(JsonElement configuration, CancellationToken cancellationToken);
    Task<JsonElement> StartSimulationAsync(CancellationToken cancellationToken);
    Task<JsonElement> StopWorldAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IGamePhysicsWorkerFactory
{
    Task<IGamePhysicsWorkerSession> StartAsync(Guid worldId, CancellationToken cancellationToken);
}

public sealed class GamePhysicsWorkerFactory : IGamePhysicsWorkerFactory
{
    private readonly GameRuntimeOptions _options;

    public GamePhysicsWorkerFactory(GameRuntimeOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<IGamePhysicsWorkerSession> StartAsync(Guid worldId, CancellationToken cancellationToken)
    {
        var supervisor = await GamePhysicsWorkerSupervisor.StartAsync(new GamePhysicsWorkerLaunchOptions
        {
            WorkerAssemblyPath = Path.GetFullPath(_options.WorkerAssemblyPath),
            WorldId = worldId,
            StartupTimeout = _options.StartupTimeout,
            RequestTimeout = _options.RequestTimeout,
            ShutdownTimeout = _options.ShutdownTimeout,
            HeartbeatInterval = _options.HeartbeatInterval,
            HeartbeatTimeout = _options.HeartbeatTimeout
        }, cancellationToken).ConfigureAwait(false);
        return new SupervisorSession(supervisor);
    }

    private sealed class SupervisorSession : IGamePhysicsWorkerSession
    {
        private readonly GamePhysicsWorkerSupervisor _supervisor;
        public SupervisorSession(GamePhysicsWorkerSupervisor supervisor) => _supervisor = supervisor;
        public WorkerState State => _supervisor.State;
        public Task<JsonElement> CreateWorldAsync(JsonElement configuration, CancellationToken token) =>
            _supervisor.RequestAsync("CreateWorld", configuration, token);
        public Task<JsonElement> StartSimulationAsync(CancellationToken token) =>
            _supervisor.RequestAsync("StartSimulation", JsonSerializer.SerializeToElement(new { }), token);
        public Task<JsonElement> StopWorldAsync(CancellationToken token) =>
            _supervisor.RequestAsync("StopWorld", JsonSerializer.SerializeToElement(new { }), token);
        public Task StopAsync(CancellationToken token) => _supervisor.StopAsync(token);
        public ValueTask DisposeAsync() => _supervisor.DisposeAsync();
    }
}

/// <summary>
/// In-memory Game Instance lifecycle. Each reservation owns one isolated Worker process.
/// No Region, Scene, or native physics object is created in the OpenSim host.
/// </summary>
public sealed class GameRuntimeService : IGameRuntimeService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly IGameProjectService _projects;
    private readonly IGamePhysicsWorkerFactory _workers;
    private readonly GameRuntimeOptions _options;
    private readonly ConcurrentDictionary<UUID, InstanceRecord> _instances = new();
    private readonly Dictionary<string, UUID> _reservations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _registryLock = new(1, 1);
    private int _disposed;

    public GameRuntimeService(IGameProjectService projects, IGamePhysicsWorkerFactory workers, GameRuntimeOptions options)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _workers = workers ?? throw new ArgumentNullException(nameof(workers));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
    }

    public async Task<GameInstanceResult> StartInstanceAsync(
        UUID authenticatedOwnerID, UUID projectID, long revision, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (authenticatedOwnerID == UUID.Zero)
            return Failure(GameInstanceError.Forbidden, "An authenticated creator is required.");
        if (!_options.Enabled)
            return Failure(GameInstanceError.WorkerUnavailable, "Game Runtime is disabled.");
        if (projectID == UUID.Zero || revision < 1)
            return Failure(GameInstanceError.InvalidRevision, "Project ID and revision must be valid.");

        GameProjectResult projectResult = _projects.GetProject(authenticatedOwnerID, projectID);
        if (!projectResult.Success || projectResult.Project is null)
            return Failure(projectResult.Error == GameProjectError.Forbidden
                ? GameInstanceError.Forbidden : GameInstanceError.NotFound, "Project was not found.");
        GameProject project = projectResult.Project;
        if (project.OwnerID != authenticatedOwnerID)
            return Failure(GameInstanceError.Forbidden, "Project ownership validation failed.");
        if (project.Status == GameProjectStatus.Archived)
            return Failure(GameInstanceError.InvalidRevision, "Archived projects cannot start new instances.");

        GameProjectRevision? storedRevision = _projects.GetRevision(authenticatedOwnerID, projectID, revision);
        if (storedRevision is null || storedRevision.ProjectID != projectID
            || storedRevision.Revision != revision || storedRevision.CreatedBy != authenticatedOwnerID)
            return Failure(GameInstanceError.InvalidRevision, "The requested immutable project revision was not found.");

        GameProjectManifest manifest;
        try
        {
            if (string.IsNullOrWhiteSpace(storedRevision.ManifestJson)
                || storedRevision.ManifestJson.Length > GameProjectManifestValidator.MaximumManifestCharacters)
                return Failure(GameInstanceError.InvalidManifest, "Stored project manifest is empty or exceeds its size limit.");
            manifest = JsonSerializer.Deserialize<GameProjectManifest>(storedRevision.ManifestJson, JsonOptions)
                ?? throw new JsonException("Manifest was null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Failure(GameInstanceError.InvalidManifest, "Stored project manifest could not be parsed.");
        }

        GameProjectValidationResult validation = GameProjectManifestValidator.Validate(manifest);
        if (!validation.IsValid)
            return Failure(GameInstanceError.InvalidManifest, "Stored project manifest failed runtime validation.");

        GameWorldDefinition? world = manifest.Worlds.SingleOrDefault(x => x.WorldID == manifest.Settings.DefaultWorldID);
        if (world is null)
            return Failure(GameInstanceError.InvalidManifest, "Default world did not resolve uniquely.");

        string reservationKey = authenticatedOwnerID + ":" + projectID + ":" + revision;
        var record = new InstanceRecord(new GameInstance
        {
            InstanceID = UUID.Random(),
            OwnerID = authenticatedOwnerID,
            ProjectID = projectID,
            ProjectRevision = revision,
            WorldID = UUID.Random(),
            State = GameInstanceState.Starting,
            CreatedUTC = DateTime.UtcNow
        });

        await _registryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_reservations.ContainsKey(reservationKey))
                return Failure(GameInstanceError.AlreadyRunning, "An instance for this project revision is already starting or running.");
            if (_instances.Values.Count(x => x.Snapshot.State is GameInstanceState.Starting or GameInstanceState.Running or GameInstanceState.Stopping)
                >= _options.MaximumActiveInstances)
                return Failure(GameInstanceError.CapacityExceeded, "The active Game Instance limit has been reached.");
            _reservations.Add(reservationKey, record.Snapshot.InstanceID);
            _instances.TryAdd(record.Snapshot.InstanceID, record);
        }
        finally { _registryLock.Release(); }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            record.Worker = await _workers.StartAsync(record.Snapshot.WorldID.Guid, cancellationToken).ConfigureAwait(false);
            JsonElement config = JsonSerializer.SerializeToElement(new
            {
                profile = "Balanced",
                gravity = new { x = 0.0, y = -9.81, z = 0.0 },
                maxDynamicBodies = 128,
                gameInstanceId = record.Snapshot.InstanceID.ToString(),
                projectId = projectID.ToString(),
                projectRevision = revision,
                worldDefinitionId = world.WorldID.ToString(),
                worldName = world.Name,
                sizeX = world.SizeX,
                sizeY = world.SizeY
            });
            await record.Worker.CreateWorldAsync(config, cancellationToken).ConfigureAwait(false);
            await record.Worker.StartSimulationAsync(cancellationToken).ConfigureAwait(false);
            await record.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (record.Worker.State != WorkerState.Ready)
                    throw new InvalidOperationException("Worker did not remain ready after world creation.");
                record.Snapshot.State = GameInstanceState.Running;
                record.Snapshot.StartedUTC = DateTime.UtcNow;
            }
            finally { record.Gate.Release(); }
            record.Monitor = MonitorWorkerAsync(record);
            return Success(record.Snapshot);
        }
        catch (OperationCanceledException)
        {
            await RollbackStartAsync(record, reservationKey).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await RollbackStartAsync(record, reservationKey).ConfigureAwait(false);
            return Failure(GameInstanceError.WorkerUnavailable, "Game physics worker could not start the requested world.");
        }
    }

    public async Task<GameInstanceResult> StopInstanceAsync(
        UUID authenticatedOwnerID, UUID instanceID, CancellationToken cancellationToken = default)
    {
        InstanceRecord? record = FindOwned(authenticatedOwnerID, instanceID);
        if (record is null) return Failure(GameInstanceError.NotFound, "Game Instance was not found.");
        await record.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (record.Snapshot.State == GameInstanceState.Stopped)
                return Success(record.Snapshot);
            if (record.Snapshot.State == GameInstanceState.Faulted)
                return Failure(GameInstanceError.InvalidState, "Faulted Game Instances cannot be gracefully stopped.");
            if (record.Snapshot.State != GameInstanceState.Running)
                return Failure(GameInstanceError.InvalidState, "Game Instance is not in a stoppable state.");
            record.Snapshot.State = GameInstanceState.Stopping;
            try
            {
                if (record.Worker is null) throw new InvalidOperationException("Worker session is missing.");
                await record.Worker.StopWorldAsync(cancellationToken).ConfigureAwait(false);
                await record.Worker.StopAsync(cancellationToken).ConfigureAwait(false);
                record.Snapshot.State = GameInstanceState.Stopped;
                record.Snapshot.StoppedUTC = DateTime.UtcNow;
                await ReleaseReservationAsync(record).ConfigureAwait(false);
                return Success(Clone(record.Snapshot));
            }
            catch
            {
                record.Snapshot.State = GameInstanceState.Faulted;
                record.Snapshot.LastErrorCode = "WorkerStopFailed";
                record.Snapshot.LastErrorMessage = "Worker did not complete a graceful stop.";
                throw;
            }
        }
        finally { record.Gate.Release(); }
    }

    public Task<GameInstanceResult> GetInstanceAsync(
        UUID authenticatedOwnerID, UUID instanceID, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceRecord? record = FindOwned(authenticatedOwnerID, instanceID);
        return Task.FromResult(record is null
            ? Failure(GameInstanceError.NotFound, "Game Instance was not found.")
            : Success(Clone(record.Snapshot)));
    }

    public Task<List<GameInstance>> GetOwnedInstancesAsync(
        UUID authenticatedOwnerID, int offset, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (authenticatedOwnerID == UUID.Zero) return Task.FromResult(new List<GameInstance>());
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);
        List<GameInstance> result = _instances.Values
            .Select(x => Clone(x.Snapshot))
            .Where(x => x.OwnerID == authenticatedOwnerID)
            .OrderByDescending(x => x.CreatedUTC)
            .Skip(offset).Take(limit).ToList();
        return Task.FromResult(result);
    }

    private async Task MonitorWorkerAsync(InstanceRecord record)
    {
        try
        {
            while (record.Worker is not null
                && record.Snapshot.State is GameInstanceState.Running or GameInstanceState.Stopping)
            {
                await Task.Delay(100).ConfigureAwait(false);
                if (record.Worker.State == WorkerState.Faulted)
                {
                    await record.Gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (record.Snapshot.State is GameInstanceState.Running or GameInstanceState.Stopping)
                        {
                            record.Snapshot.State = GameInstanceState.Faulted;
                            record.Snapshot.LastErrorCode = "WorkerFaulted";
                            record.Snapshot.LastErrorMessage = "Game physics worker exited or failed its health check.";
                        }
                    }
                    finally { record.Gate.Release(); }
                    await ReleaseReservationAsync(record).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch { /* Monitoring must not take down the host or another instance. */ }
    }

    private async Task ReleaseReservationAsync(InstanceRecord record)
    {
        string key = record.Snapshot.OwnerID + ":" + record.Snapshot.ProjectID + ":" + record.Snapshot.ProjectRevision;
        await _registryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_reservations.TryGetValue(key, out UUID id) && id == record.Snapshot.InstanceID)
                _reservations.Remove(key);
        }
        finally { _registryLock.Release(); }
    }

    private async Task RollbackStartAsync(InstanceRecord record, string reservationKey)
    {
        if (record.Worker is not null)
        {
            try { await record.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await record.Worker.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        await _registryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _instances.TryRemove(record.Snapshot.InstanceID, out _);
            if (_reservations.TryGetValue(reservationKey, out UUID id) && id == record.Snapshot.InstanceID)
                _reservations.Remove(reservationKey);
        }
        finally { _registryLock.Release(); }
    }

    private InstanceRecord? FindOwned(UUID owner, UUID id)
    {
        if (owner == UUID.Zero || id == UUID.Zero || !_instances.TryGetValue(id, out InstanceRecord? record))
            return null;
        return record.Snapshot.OwnerID == owner ? record : null;
    }

    private static GameInstance Clone(GameInstance source) => new()
    {
        InstanceID = source.InstanceID,
        OwnerID = source.OwnerID,
        ProjectID = source.ProjectID,
        ProjectRevision = source.ProjectRevision,
        WorldID = source.WorldID,
        State = source.State,
        CreatedUTC = source.CreatedUTC,
        StartedUTC = source.StartedUTC,
        StoppedUTC = source.StoppedUTC,
        LastErrorCode = source.LastErrorCode,
        LastErrorMessage = source.LastErrorMessage
    };

    private static GameInstanceResult Success(GameInstance instance) =>
        new() { Error = GameInstanceError.None, Instance = Clone(instance) };
    private static GameInstanceResult Failure(GameInstanceError error, string message) =>
        new() { Error = error, Message = message };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new UuidConverter());
        return options;
    }

    private static void ValidateOptions(GameRuntimeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.WorkerAssemblyPath))
            throw new ArgumentException("Worker assembly path is required.", nameof(options));
        if (options.MaximumActiveInstances < 1 || options.MaximumActiveInstances > 256)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumActiveInstances));
        foreach (TimeSpan timeout in new[] { options.StartupTimeout, options.RequestTimeout, options.ShutdownTimeout,
                     options.HeartbeatInterval, options.HeartbeatTimeout })
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
                throw new ArgumentOutOfRangeException(nameof(options), "Worker deadlines must be positive and bounded.");
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(GameRuntimeService));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (InstanceRecord record in _instances.Values)
        {
            await record.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (record.Worker is not null && record.Snapshot.State is GameInstanceState.Running or GameInstanceState.Stopping)
                {
                    try
                    {
                        await record.Worker.StopWorldAsync(CancellationToken.None).ConfigureAwait(false);
                        await record.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
                        record.Snapshot.State = GameInstanceState.Stopped;
                        record.Snapshot.StoppedUTC = DateTime.UtcNow;
                    }
                    catch
                    {
                        record.Snapshot.State = GameInstanceState.Faulted;
                        record.Snapshot.LastErrorCode = "HostShutdown";
                        record.Snapshot.LastErrorMessage = "Worker was terminated during host shutdown.";
                    }
                }
                if (record.Worker is not null)
                    await record.Worker.DisposeAsync().ConfigureAwait(false);
            }
            finally { record.Gate.Release(); }
        }
        _registryLock.Dispose();
    }

    private sealed class InstanceRecord
    {
        public InstanceRecord(GameInstance snapshot) => Snapshot = snapshot;
        public GameInstance Snapshot { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IGamePhysicsWorkerSession? Worker { get; set; }
        public Task? Monitor { get; set; }
    }

    private sealed class UuidConverter : JsonConverter<UUID>
    {
        public override UUID Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return UUID.TryParse(value, out UUID parsed) ? parsed : throw new JsonException("Invalid UUID.");
        }
        public override void Write(Utf8JsonWriter writer, UUID value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
