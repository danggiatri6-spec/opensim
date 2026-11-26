using System.Reflection;
using Nini.Config;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;

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
