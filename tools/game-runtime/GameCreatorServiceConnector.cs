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
    private readonly GameRuntimeServicePlugin? _runtime;
    private readonly IHttpServer _server;
    private GameCreatorApiHandler? _getHandler;
    private GameCreatorApiHandler? _postHandler;
    private bool _disposed;

    public GameCreatorServiceConnector(IConfigSource config, IHttpServer server, string configName)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(server);
        _server = server;

        IConfig? section = config.Configs["GameRuntimeService"];
        if (section?.GetBoolean("Enabled", false) != true)
            return;

        _runtime = new GameRuntimeServicePlugin(config);

        try
        {
            IConfig? authenticationConfig = config.Configs["AuthenticationService"];
            string authenticationModule = authenticationConfig?.GetString("LocalServiceModule", string.Empty)
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(authenticationModule))
                throw new InvalidOperationException(
                    "[AuthenticationService] LocalServiceModule is required when Creator API is enabled.");

            IAuthenticationService authenticationService =
                ServerUtils.LoadPlugin<IAuthenticationService>(
                    authenticationModule, new object[] { config });

            var authenticator = new GameCreatorRequestAuthenticator(authenticationService);
            _getHandler = new GameCreatorApiHandler("GET", authenticator, _runtime);
            _postHandler = new GameCreatorApiHandler("POST", authenticator, _runtime);
            server.AddStreamHandler(_getHandler);
            server.AddStreamHandler(_postHandler);

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
        catch
        {
            _runtime.Dispose();
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
        if (_getHandler is not null)
            _server.RemoveStreamHandler("GET", "/creator/instances");
        if (_postHandler is not null)
            _server.RemoveStreamHandler("POST", "/creator/instances");
        _runtime?.Dispose();
    }
}
