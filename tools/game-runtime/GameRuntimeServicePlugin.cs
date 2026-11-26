using Nini.Config;
using OpenMetaverse;
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;

namespace OpenSim.GameRuntime;

/// <summary>
/// Config-driven service composition entry point. Hosts may load this type as a
/// service plugin. Disabled is the default and does not initialize project storage
/// or launch any Worker processes.
/// </summary>
public sealed class GameRuntimeServicePlugin : IGameRuntimeService, IAsyncDisposable, IDisposable
{
    private readonly GameRuntimeService? _runtime;
    public IGameProjectService? ProjectService { get; }

    public GameRuntimeServicePlugin(IConfigSource config)
    {
        ArgumentNullException.ThrowIfNull(config);
        IConfig? section = config.Configs["GameRuntimeService"];
        bool enabled = section?.GetBoolean("Enabled", false) ?? false;
        if (!enabled)
            return;

        string workerPath = section!.GetString("WorkerAssemblyPath", string.Empty);
        if (!string.IsNullOrWhiteSpace(workerPath) && !Path.IsPathRooted(workerPath))
            workerPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, workerPath));

        var options = new GameRuntimeOptions
        {
            Enabled = true,
            WorkerAssemblyPath = workerPath,
            StartupTimeout = TimeSpan.FromSeconds(section.GetInt("StartupTimeoutSeconds", 15)),
            RequestTimeout = TimeSpan.FromSeconds(section.GetInt("RequestTimeoutSeconds", 5)),
            ShutdownTimeout = TimeSpan.FromSeconds(section.GetInt("ShutdownTimeoutSeconds", 10)),
            HeartbeatInterval = TimeSpan.FromSeconds(section.GetInt("HeartbeatIntervalSeconds", 5)),
            HeartbeatTimeout = TimeSpan.FromSeconds(section.GetInt("HeartbeatTimeoutSeconds", 3)),
            MaximumActiveInstances = section.GetInt("MaximumActiveInstances", 16)
        };

        ProjectService = new GameProjectService(config);
        _runtime = new GameRuntimeService(ProjectService, new GamePhysicsWorkerFactory(options), options);
    }

    public Task<GameInstanceResult> StartInstanceAsync(UUID authenticatedOwnerID, UUID projectID, long revision, CancellationToken cancellationToken = default)
    {
        if (_runtime is not null)
            return _runtime.StartInstanceAsync(authenticatedOwnerID, projectID, revision, cancellationToken);
        return Task.FromResult(new GameInstanceResult
        {
            Error = GameInstanceError.WorkerUnavailable,
            Message = "Game Runtime is disabled."
        });
    }

    public Task<GameInstanceResult> StopInstanceAsync(UUID authenticatedOwnerID, UUID instanceID, CancellationToken cancellationToken = default)
    {
        if (_runtime is not null)
            return _runtime.StopInstanceAsync(authenticatedOwnerID, instanceID, cancellationToken);
        return Task.FromResult(new GameInstanceResult
        {
            Error = GameInstanceError.WorkerUnavailable,
            Message = "Game Runtime is disabled."
        });
    }

    public Task<GameInstanceResult> GetInstanceAsync(UUID authenticatedOwnerID, UUID instanceID, CancellationToken cancellationToken = default)
    {
        if (_runtime is not null)
            return _runtime.GetInstanceAsync(authenticatedOwnerID, instanceID, cancellationToken);
        return Task.FromResult(new GameInstanceResult
        {
            Error = GameInstanceError.NotFound,
            Message = "Game Runtime is disabled."
        });
    }

    public Task<List<GameInstance>> GetOwnedInstancesAsync(UUID authenticatedOwnerID, int offset, int limit, CancellationToken cancellationToken = default)
    {
        if (_runtime is not null)
            return _runtime.GetOwnedInstancesAsync(authenticatedOwnerID, offset, limit, cancellationToken);
        return Task.FromResult(new List<GameInstance>());
    }

    public ValueTask DisposeAsync() => _runtime?.DisposeAsync() ?? ValueTask.CompletedTask;

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
