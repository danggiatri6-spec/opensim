using System.Collections;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.GameRuntime;
using OpenSim.Services.Interfaces;

internal static class GameCreatorApiHandlerTests
{
    public static void Run()
    {
        UUID creator = UUID.Random();
        var authService = new FakeAuthenticationService(creator, "creator-token");
        var runtime = new FakeRuntimeService();
        var authenticator = new GameCreatorRequestAuthenticator(authService);
        var handler = new GameCreatorApiHandler("POST", authenticator, runtime);

        FakeRequest insecure = Request("POST", "/creator/instances",
            "{\"project_id\":\"" + UUID.Random() + "\",\"revision\":1}", creator, secured: false);
        FakeResponse insecureResponse = new();
        handler.Handle("/creator/instances", insecure.InputStream, insecure, insecureResponse);
        Assert(insecureResponse.StatusCode == 403 && authService.VerifyCalls == 0,
            "Creator API rejects cleartext transport before verifying bearer credentials");

        FakeRequest invalidToken = Request("POST", "/creator/instances",
            "{\"project_id\":\"" + UUID.Random() + "\",\"revision\":1}", creator, token: "wrong");
        FakeResponse invalidResponse = new();
        handler.Handle("/creator/instances", invalidToken.InputStream, invalidToken, invalidResponse);
        Assert(invalidResponse.StatusCode == 401 && runtime.StartCalls == 0,
            "invalid bearer token cannot invoke Game Runtime");

        FakeRequest spoofedOwner = Request("POST", "/creator/instances",
            "{\"project_id\":\"" + UUID.Random() + "\",\"revision\":1,\"ownerId\":\"" + UUID.Random() + "\"}", creator);
        FakeResponse spoofedResponse = new();
        handler.Handle("/creator/instances", spoofedOwner.InputStream, spoofedOwner, spoofedResponse);
        Assert(spoofedResponse.StatusCode == 400 && runtime.StartCalls == 0,
            "alternate owner identity fields are rejected before runtime invocation");

        UUID project = UUID.Random();
        FakeRequest valid = Request("POST", "/creator/instances",
            "{\"project_id\":\"" + project + "\",\"revision\":1}", creator);
        FakeResponse validResponse = new();
        handler.Handle("/creator/instances", valid.InputStream, valid, validResponse);
        Assert(validResponse.StatusCode == 201 && runtime.StartCalls == 1 && runtime.LastOwnerID == creator,
            "valid Creator request passes only the token-authenticated principal to Game Runtime");

        FakeRequest oversized = Request("POST", "/creator/instances",
            new string('x', 16 * 1024 + 1), creator);
        FakeResponse oversizedResponse = new();
        handler.Handle("/creator/instances", oversized.InputStream, oversized, oversizedResponse);
        Assert(oversizedResponse.StatusCode == 400 && runtime.StartCalls == 1,
            "oversized Creator request body is rejected before runtime invocation");

        var getHandler = new GameCreatorApiHandler("GET", authenticator, runtime);
        FakeRequest list = Request("GET", "/creator/instances", string.Empty, creator);
        FakeResponse listResponse = new();
        getHandler.Handle("/creator/instances", list.InputStream, list, listResponse);
        Assert(listResponse.StatusCode == 200 && runtime.ListCalls == 1,
            "GET collection route lists only the authenticated creator's instances");

        UUID instanceID = UUID.Random();
        FakeRequest get = Request("GET", "/creator/instances/" + instanceID, string.Empty, creator);
        FakeResponse getResponse = new();
        getHandler.Handle("/creator/instances/" + instanceID, get.InputStream, get, getResponse);
        Assert(getResponse.StatusCode == 404 && runtime.GetCalls == 1,
            "GET instance route delegates ownership-scoped lookup");

        FakeRequest stop = Request("POST", "/creator/instances/" + instanceID + "/stop", string.Empty, creator);
        FakeResponse stopResponse = new();
        handler.Handle("/creator/instances/" + instanceID + "/stop", stop.InputStream, stop, stopResponse);
        Assert(stopResponse.StatusCode == 404 && runtime.StopCalls == 1,
            "POST stop route delegates ownership-scoped lifecycle operation");
    }

    private static FakeRequest Request(
        string method, string path, string body, UUID principal, string token = "creator-token", bool secured = true)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        var headers = new NameValueCollection
        {
            ["Authorization"] = "Bearer " + token,
            ["X-OpenSim-Principal"] = principal.ToString()
        };
        return new FakeRequest(method, path, new MemoryStream(bytes), bytes.Length, headers, secured);
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception("ASSERT FAILED: " + message);
    }

    private sealed class FakeAuthenticationService : IAuthenticationService
    {
        private readonly UUID _principal;
        private readonly string _token;
        public FakeAuthenticationService(UUID principal, string token) { _principal = principal; _token = token; }
        public int VerifyCalls { get; private set; }
        public string Authenticate(UUID principalID, string password, int lifetime) => string.Empty;
        public string Authenticate(UUID principalID, string password, int lifetime, out UUID realID) { realID = UUID.Zero; return string.Empty; }
        public bool Verify(UUID principalID, string token, int lifetime) { VerifyCalls++; return principalID == _principal && token == _token; }
        public bool Release(UUID principalID, string token) => false;
        public bool SetPassword(UUID principalID, string passwd) => false;
        public AuthInfo GetAuthInfo(UUID principalID) => null;
        public bool SetAuthInfo(AuthInfo info) => false;
    }

    private sealed class FakeRuntimeService : IGameRuntimeService
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int GetCalls { get; private set; }
        public int ListCalls { get; private set; }
        public UUID LastOwnerID { get; private set; }
        public Task<GameInstanceResult> StartInstanceAsync(UUID authenticatedOwnerID, UUID projectID, long revision, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            LastOwnerID = authenticatedOwnerID;
            return Task.FromResult(new GameInstanceResult
            {
                Error = GameInstanceError.None,
                Instance = new GameInstance { InstanceID = UUID.Random(), OwnerID = authenticatedOwnerID, ProjectID = projectID, ProjectRevision = revision, State = GameInstanceState.Running }
            });
        }
        public Task<GameInstanceResult> StopInstanceAsync(UUID authenticatedOwnerID, UUID instanceID, CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return Task.FromResult(new GameInstanceResult { Error = GameInstanceError.NotFound });
        }
        public Task<GameInstanceResult> GetInstanceAsync(UUID authenticatedOwnerID, UUID instanceID, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(new GameInstanceResult { Error = GameInstanceError.NotFound });
        }
        public Task<List<GameInstance>> GetOwnedInstancesAsync(UUID authenticatedOwnerID, int offset, int limit, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(new List<GameInstance>());
        }
    }

    private sealed class FakeRequest : IOSHttpRequest
    {
        public FakeRequest(string method, string path, Stream input, long length, NameValueCollection headers, bool secured)
        {
            HttpMethod = method; UriPath = path; InputStream = input; ContentLength64 = length; ContentLength = length;
            Headers = headers; IsSecured = secured; Url = new Uri("https://localhost" + path);
            QueryString = new NameValueCollection(); Query = new Hashtable(); QueryFlags = new HashSet<string>();
            QueryAsDictionary = new Dictionary<string, string>(); ContentEncoding = Encoding.UTF8;
        }
        public string[] AcceptTypes => Array.Empty<string>();
        public Encoding ContentEncoding { get; }
        public long ContentLength { get; }
        public long ContentLength64 { get; }
        public string ContentType => "application/json";
        public bool HasEntityBody => ContentLength64 > 0;
        public NameValueCollection Headers { get; }
        public string HttpMethod { get; }
        public Stream InputStream { get; }
        public bool IsSecured { get; }
        public bool KeepAlive => false;
        public NameValueCollection QueryString { get; }
        public Hashtable Query { get; }
        public HashSet<string> QueryFlags { get; }
        public Dictionary<string, string> QueryAsDictionary { get; }
        public string RawUrl => UriPath;
        public IPEndPoint RemoteIPEndPoint => new(IPAddress.Loopback, 12345);
        public IPEndPoint LocalIPEndPoint => new(IPAddress.Loopback, 9000);
        public Uri Url { get; }
        public string UriPath { get; }
        public string UserAgent => "GameCreatorApiHandlerTests";
        public double ArrivalTS => 0;
    }

    private sealed class FakeResponse : IOSHttpResponse
    {
        private readonly NameValueCollection _headers = new();
        public string ContentType { get; set; } = string.Empty;
        public long ContentLength { get; set; }
        public long ContentLength64 { get => ContentLength; set => ContentLength = value; }
        public Encoding ContentEncoding { get; set; } = Encoding.UTF8;
        public bool KeepAlive { get; set; }
        public int KeepAliveTimeout { get; set; }
        public Stream OutputStream { get; } = new MemoryStream();
        public string ProtocolVersion { get; set; } = "HTTP/1.1";
        public int Priority { get; set; }
        public byte[] RawBuffer { get; set; } = Array.Empty<byte>();
        public int RawBufferStart { get; set; }
        public int RawBufferLen { get; set; }
        public Stream Body => OutputStream;
        public bool SendChunked { get; set; }
        public int StatusCode { get; set; } = 200;
        public string StatusDescription { get; set; } = string.Empty;
        public double RequestTS => 0;
        public void Redirect(string url, HttpStatusCode redirStatusCode = HttpStatusCode.Redirect) { }
        public void AddHeader(string key, string value) => _headers.Add(key, value);
    }
}
