using System.Reflection;
using Nini.Config;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

internal static class GameCreatorConnectorTests
{
    public static void Run()
    {
        IConfigSource config = new IniConfigSource();
        IHttpServer server = DispatchProxy.Create<IHttpServer, FakeHttpServerProxy>();
        string assemblyPath = Path.Combine(AppContext.BaseDirectory, "GameRuntime.dll");
        string pluginSpec = assemblyPath + ":GameCreatorServiceConnector";

        IServiceConnector connector = ServerUtils.LoadPlugin<IServiceConnector>(
            pluginSpec, new object[] { config, server, string.Empty });

        if (connector is not IDisposable disposable)
            throw new Exception("ASSERT FAILED: Robust Creator connector loads and supports disposal");

        disposable.Dispose();

        VerifyEnabledConnectorRegistrationAndDisposal();
    }

    private static void VerifyEnabledConnectorRegistrationAndDisposal()
    {
        var config = new IniConfigSource();
        config.AddConfig("GameRuntimeService").Set("Enabled", true);
        var server = DispatchProxy.Create<IHttpServer, RecordingHttpServerProxy>();
        var runtime = new FakeRuntimeService();
        var authentication = new FakeAuthenticationService();
        using var connector = new GameCreatorServiceConnector(
            config, server, string.Empty, runtime, authentication);

        var recordingServer = (RecordingHttpServerProxy)(object)server;
        if (recordingServer.AddedHandlers.Count != 2)
            throw new Exception("ASSERT FAILED: enabled Creator connector registers GET and POST routes");

        connector.Dispose();
        if (recordingServer.RemovedRoutes.Count != 2 || !runtime.Disposed)
            throw new Exception("ASSERT FAILED: connector disposal unregisters routes and disposes runtime");
    }

    public class RecordingHttpServerProxy : DispatchProxy
    {
        public List<IRequestHandler> AddedHandlers { get; } = new();
        public List<string> RemovedRoutes { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "AddStreamHandler")
            {
                AddedHandlers.Add((IRequestHandler)args![0]!);
                return null;
            }
            if (targetMethod?.Name == "RemoveStreamHandler")
            {
                RemovedRoutes.Add(args![0] + " " + args[1]);
                return null;
            }
            if (targetMethod?.ReturnType == typeof(bool))
                return false;
            if (targetMethod?.ReturnType == typeof(uint))
                return 0u;
            if (targetMethod?.ReturnType == typeof(string))
                return string.Empty;
            return null;
        }
    }

    private sealed class FakeRuntimeService : IGameRuntimeService, IDisposable
    {
        public bool Disposed { get; private set; }
        public Task<GameInstanceResult> StartInstanceAsync(UUID owner, UUID project, long revision, CancellationToken token = default) =>
            Task.FromResult(new GameInstanceResult { Error = GameInstanceError.NotFound });
        public Task<GameInstanceResult> StopInstanceAsync(UUID owner, UUID instance, CancellationToken token = default) =>
            Task.FromResult(new GameInstanceResult { Error = GameInstanceError.NotFound });
        public Task<GameInstanceResult> GetInstanceAsync(UUID owner, UUID instance, CancellationToken token = default) =>
            Task.FromResult(new GameInstanceResult { Error = GameInstanceError.NotFound });
        public Task<List<GameInstance>> GetOwnedInstancesAsync(UUID owner, int offset, int limit, CancellationToken token = default) =>
            Task.FromResult(new List<GameInstance>());
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeAuthenticationService : IAuthenticationService
    {
        public string Authenticate(UUID principalID, string password, int lifetime) => string.Empty;
        public string Authenticate(UUID principalID, string password, int lifetime, out UUID realID) { realID = UUID.Zero; return string.Empty; }
        public bool Verify(UUID principalID, string token, int lifetime) => false;
        public bool Release(UUID principalID, string token) => false;
        public bool SetPassword(UUID principalID, string passwd) => false;
        public AuthInfo GetAuthInfo(UUID principalID) => null;
        public bool SetAuthInfo(AuthInfo info) => false;
    }

    public class FakeHttpServerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                throw new InvalidOperationException("Missing HTTP server method metadata.");
            if (targetMethod.ReturnType == typeof(bool))
                return false;
            if (targetMethod.ReturnType == typeof(uint))
                return 0u;
            if (targetMethod.ReturnType == typeof(string))
                return string.Empty;
            return null;
        }
    }
}
