using Nini.Config;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.GameRuntime;

/// <summary>
/// Robust ServiceList connector for the Creator Game Runtime API.
/// The API is not registered unless [GameRuntimeService] Enabled=true.
/// </summary>
public sealed class GameCreatorServiceConnector : IServiceConnector, IDisposable
{
    private readonly IGameRuntimeService? _runtime;
    private readonly IDisposable? _runtimeDisposable;
    private readonly IHttpServer _server;
    private GameCreatorApiHandler? _getHandler;
    private GameCreatorApiHandler? _postHandler;
    private bool _disposed;

    public GameCreatorServiceConnector(IConfigSource config, IHttpServer server, string configName)
        : this(config, server, configName, null, null)
    {
    }

    // Dependency-injection overload for connector-level tests and embedded hosts.
    public GameCreatorServiceConnector(
        IConfigSource config,
        IHttpServer server,
        string configName,
        IGameRuntimeService? runtimeOverride,
        IAuthenticationService? authenticationOverride)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(server);
        _server = server;

        IConfig? section = config.Configs["GameRuntimeService"];
        if (section?.GetBoolean("Enabled", false) != true)
            return;

        if (runtimeOverride is not null)
        {
            _runtime = runtimeOverride;
            _runtimeDisposable = runtimeOverride as IDisposable;
        }
        else
        {
            var plugin = new GameRuntimeServicePlugin(config);
            _runtime = plugin;
            _runtimeDisposable = plugin;
        }

        try
        {
            IAuthenticationService authenticationService = authenticationOverride!;
            if (authenticationService is null)
            {
                IConfig? authenticationConfig = config.Configs["AuthenticationService"];
                string authenticationModule = authenticationConfig?.GetString("LocalServiceModule", string.Empty)
                    ?? string.Empty;
                if (string.IsNullOrWhiteSpace(authenticationModule))
                    throw new InvalidOperationException(
                        "[AuthenticationService] LocalServiceModule is required when Creator API is enabled.");

                authenticationService = ServerUtils.LoadPlugin<IAuthenticationService>(
                    authenticationModule, new object[] { config });
            }

            var authenticator = new GameCreatorRequestAuthenticator(authenticationService);
            var rateLimiter = new GameCreatorRequestRateLimiter();
            _getHandler = new GameCreatorApiHandler("GET", authenticator, _runtime, rateLimiter);
            _postHandler = new GameCreatorApiHandler("POST", authenticator, _runtime, rateLimiter);
            server.AddStreamHandler(_getHandler);
            server.AddStreamHandler(_postHandler);

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
        catch
        {
            try
            {
                if (_getHandler is not null)
                    server.RemoveStreamHandler("GET", "/creator/instances");
                if (_postHandler is not null)
                    server.RemoveStreamHandler("POST", "/creator/instances");
            }
            catch
            {
                // Preserve the original startup exception; the server may reject cleanup during startup.
            }
            finally
            {
                _runtimeDisposable?.Dispose();
            }
            throw;
        }
    }

    private void OnProcessExit(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        try
        {
            if (_getHandler is not null)
                _server.RemoveStreamHandler("GET", "/creator/instances");
            if (_postHandler is not null)
                _server.RemoveStreamHandler("POST", "/creator/instances");
        }
        catch
        {
            // Host shutdown may already have torn down the HTTP server.
        }
        finally
        {
            _runtimeDisposable?.Dispose();
        }
    }
}
