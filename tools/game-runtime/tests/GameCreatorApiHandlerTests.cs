using System.Collections;
using System.Collections.Specialized;
using System.Net;
using System.Reflection;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.GameRuntime;
using OpenSim.Services.Interfaces;
using OpenSim.Data;
using OpenSim.Services.UserAccountService;

internal static class GameCreatorApiHandlerTests
{
    public static void Run()
    {
        TestRateLimiter();
        TestProjectRoutes();
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

    private static void TestProjectRoutes()
    {
        UUID owner = UUID.Random();
        var auth = new GameCreatorRequestAuthenticator(new FakeAuthenticationService(owner, "creator-token"));
        var projects = new FakeProjectService(owner);
        var get = new GameCreatorProjectsApiHandler("GET", auth, projects);
        var post = new GameCreatorProjectsApiHandler("POST", auth, projects);
        var put = new GameCreatorProjectsApiHandler("PUT", auth, projects);
        var patch = new GameCreatorProjectsApiHandler("PATCH", auth, projects);
        IGameProjectData emptyData = DispatchProxy.Create<IGameProjectData, EmptyGameProjectDataProxy>();
        var experienceService = new GameExperienceService(emptyData);
        var projectV2Service = new GameProjectV2Service(emptyData);
        var nestedGet = new GameCreatorProjectsApiHandler("GET", auth, projects, null, experienceService, projectV2Service);
        var nestedPost = new GameCreatorProjectsApiHandler("POST", auth, projects, null, experienceService, projectV2Service);
        var nestedPut = new GameCreatorProjectsApiHandler("PUT", auth, projects, null, experienceService, projectV2Service);

        FakeRequest experienceList = Request("GET", "/creator/projects/" + projects.ProjectID + "/experiences", string.Empty, owner);
        FakeResponse experienceListResponse = new();
        nestedGet.Handle("/creator/projects/" + projects.ProjectID + "/experiences",
            experienceList.InputStream, experienceList, experienceListResponse);
        Assert(experienceListResponse.StatusCode == 200
            && Encoding.UTF8.GetString(((MemoryStream)experienceListResponse.OutputStream).ToArray()).Contains("\"experiences\":[]"),
            "nested Experience routes share the existing authenticated project handler");

        FakeRequest experienceSpoof = Request("POST", "/creator/projects/" + projects.ProjectID + "/experiences",
            "{\"ownerId\":\"" + UUID.Random() + "\"}", owner);
        FakeResponse experienceSpoofResponse = new();
        nestedPost.Handle("/creator/projects/" + projects.ProjectID + "/experiences",
            experienceSpoof.InputStream, experienceSpoof, experienceSpoofResponse);
        Assert(experienceSpoofResponse.StatusCode == 400,
            "Experience identity creation rejects body-supplied owner identity");

        FakeRequest projectV2Spoof = Request("PUT", "/creator/projects/" + projects.ProjectID + "/revisions/v2",
            "{\"expectedCurrentRevision\":0,\"schemaVersion\":2,\"ownerId\":\"" + UUID.Random() + "\",\"experiences\":[]}", owner);
        FakeResponse projectV2SpoofResponse = new();
        nestedPut.Handle("/creator/projects/" + projects.ProjectID + "/revisions/v2",
            projectV2Spoof.InputStream, projectV2Spoof, projectV2SpoofResponse);
        Assert(projectV2SpoofResponse.StatusCode == 400,
            "v2 project revision route rejects body-supplied owner identity");

        FakeRequest create = Request("POST", "/creator/projects",
            "{\"name\":\"My Game\",\"description\":\"prototype\"}", owner);
        FakeResponse created = new();
        post.Handle("/creator/projects", create.InputStream, create, created);
        Assert(created.StatusCode == 201 && projects.CreateCalls == 1 && projects.LastOwner == owner,
            "Creator can create a project using only the authenticated principal");

        FakeRequest spoof = Request("POST", "/creator/projects",
            "{\"name\":\"Spoof\",\"owner_id\":\"" + UUID.Random() + "\"}", owner);
        FakeResponse spoofResponse = new();
        post.Handle("/creator/projects", spoof.InputStream, spoof, spoofResponse);
        Assert(spoofResponse.StatusCode == 400 && projects.CreateCalls == 1,
            "project creation rejects client-supplied owner identity");

        FakeRequest list = Request("GET", "/creator/projects", string.Empty, owner);
        FakeResponse listResponse = new();
        get.Handle("/creator/projects", list.InputStream, list, listResponse);
        Assert(listResponse.StatusCode == 200 && projects.ListCalls == 1,
            "project listing is scoped to the authenticated creator");

        UUID projectID = projects.ProjectID;
        FakeRequest read = Request("GET", "/creator/projects/" + projectID, string.Empty, owner);
        FakeResponse readResponse = new();
        get.Handle("/creator/projects/" + projectID, read.InputStream, read, readResponse);
        Assert(readResponse.StatusCode == 200 && projects.GetCalls == 1,
            "creator can read an owned project");

        string manifest = "{\"schemaVersion\":1,\"name\":\"My Game\",\"worlds\":[{\"worldID\":\"" +
            projects.WorldID + "\",\"name\":\"World\",\"sizeX\":256,\"sizeY\":256,\"spawnPoints\":[],\"assetReferences\":[]}]," +
            "\"settings\":{\"gameMode\":\"sandbox\",\"defaultWorldID\":\"" + projects.WorldID +
            "\",\"visibility\":\"private\",\"maxPlayers\":16,\"tags\":[]}}";
        FakeRequest nestedSpoof = Request("PUT", "/creator/projects/" + projectID + "/draft",
            "{\"expected_current_revision\":0,\"manifest\":{\"ownerId\":\"" + UUID.Random() + "\"}}", owner);
        FakeResponse nestedSpoofResponse = new();
        put.Handle("/creator/projects/" + projectID + "/draft", nestedSpoof.InputStream, nestedSpoof, nestedSpoofResponse);
        Assert(nestedSpoofResponse.StatusCode == 400 && projects.SaveCalls == 0,
            "nested manifest owner identity fields are rejected");

        FakeRequest save = Request("PUT", "/creator/projects/" + projectID + "/draft",
            "{\"expected_current_revision\":0,\"manifest\":" + manifest + "}", owner);
        FakeResponse saveResponse = new();
        put.Handle("/creator/projects/" + projectID + "/draft", save.InputStream, save, saveResponse);
        Assert(saveResponse.StatusCode == 200 && projects.SaveCalls == 1 && projects.LastOwner == owner,
            "saving a draft delegates a validated manifest and expected revision to the project service");

        FakeRequest revision = Request("GET", "/creator/projects/" + projectID + "/revisions/1", string.Empty, owner);
        FakeResponse revisionResponse = new();
        get.Handle("/creator/projects/" + projectID + "/revisions/1", revision.InputStream, revision, revisionResponse);
        Assert(revisionResponse.StatusCode == 200 && projects.RevisionCalls == 1,
            "immutable project revisions can be retrieved by their owner");

        FakeRequest metadata = Request("PATCH", "/creator/projects/" + projectID,
            "{\"expected_current_revision\":1,\"name\":\"Renamed\",\"description\":\"updated\"}", owner);
        FakeResponse metadataResponse = new();
        patch.Handle("/creator/projects/" + projectID, metadata.InputStream, metadata, metadataResponse);
        Assert(metadataResponse.StatusCode == 200 && projects.MetadataCalls == 1,
            "metadata updates require the current revision for optimistic concurrency");

        FakeRequest publish = Request("POST", "/creator/projects/" + projectID + "/publish",
            "{\"expected_current_revision\":1}", owner);
        FakeResponse publishResponse = new();
        post.Handle("/creator/projects/" + projectID + "/publish", publish.InputStream, publish, publishResponse);
        Assert(publishResponse.StatusCode == 200 && projects.PublishCalls == 1,
            "creator can publish using an explicit expected current revision");

        FakeRequest stalePublish = Request("POST", "/creator/projects/" + projectID + "/publish",
            "{\"expected_current_revision\":2}", owner);
        FakeResponse stalePublishResponse = new();
        post.Handle("/creator/projects/" + projectID + "/publish", stalePublish.InputStream, stalePublish, stalePublishResponse);
        Assert(stalePublishResponse.StatusCode == 409,
            "publish maps a revision conflict to HTTP 409");

        FakeRequest publishSpoof = Request("POST", "/creator/projects/" + projectID + "/publish",
            "{\"expected_current_revision\":1,\"owner_id\":\"" + UUID.Random() + "\"}", owner);
        FakeResponse publishSpoofResponse = new();
        post.Handle("/creator/projects/" + projectID + "/publish", publishSpoof.InputStream, publishSpoof, publishSpoofResponse);
        Assert(publishSpoofResponse.StatusCode == 400 && projects.PublishCalls == 2,
            "publish rejects client-supplied owner identity");

        FakeRequest archive = Request("POST", "/creator/projects/" + projectID + "/archive", string.Empty, owner);
        FakeResponse archiveResponse = new();
        post.Handle("/creator/projects/" + projectID + "/archive", archive.InputStream, archive, archiveResponse);
        Assert(archiveResponse.StatusCode == 200 && projects.ArchiveCalls == 1,
            "creator can archive an owned project");
    }

    public class EmptyGameProjectDataProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.ReturnType == typeof(bool)) return false;
            if (targetMethod.ReturnType == typeof(List<GameExperienceData>)) return new List<GameExperienceData>();
            if (targetMethod.ReturnType == typeof(List<GamePlaceData>)) return new List<GamePlaceData>();
            if (targetMethod.ReturnType == typeof(List<GameProjectData>)) return new List<GameProjectData>();
            return null;
        }
    }

    private sealed class FakeProjectService : IGameProjectService
    {
        private readonly UUID _owner;
        public UUID ProjectID { get; } = UUID.Random();
        public UUID WorldID { get; } = UUID.Random();
        public UUID LastOwner { get; private set; }
        public int CreateCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int GetCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public int RevisionCalls { get; private set; }
        public int MetadataCalls { get; private set; }
        public int ArchiveCalls { get; private set; }
        public int PublishCalls { get; private set; }
        public FakeProjectService(UUID owner) { _owner = owner; }
        private GameProject Project => new() { ProjectID = ProjectID, OwnerID = _owner, Name = "My Game", CurrentRevision = 1 };
        public GameProjectResult CreateProject(UUID owner, string name, string description)
        {
            CreateCalls++; LastOwner = owner;
            return new GameProjectResult { Error = GameProjectError.None, Project = Project };
        }
        public GameProjectResult GetProject(UUID owner, UUID id)
        {
            GetCalls++; LastOwner = owner;
            return owner == _owner && id == ProjectID
                ? new GameProjectResult { Error = GameProjectError.None, Project = Project }
                : new GameProjectResult { Error = GameProjectError.NotFound };
        }
        public List<GameProject> GetOwnedProjects(UUID owner, int offset, int limit)
        {
            ListCalls++; LastOwner = owner;
            return owner == _owner ? new List<GameProject> { Project } : new List<GameProject>();
        }
        public GameProjectResult SaveDraft(UUID owner, UUID id, long expected, GameProjectManifest manifest)
        {
            SaveCalls++; LastOwner = owner;
            return new GameProjectResult { Error = GameProjectError.None, Project = Project };
        }
        public GameProjectRevision GetRevision(UUID owner, UUID id, long revision)
        {
            RevisionCalls++; LastOwner = owner;
            return owner == _owner && id == ProjectID && revision == 1
                ? new GameProjectRevision { ProjectID = id, Revision = 1, CreatedBy = owner, ManifestJson = "{}" }
                : null;
        }
        public GameProjectResult UpdateMetadata(UUID owner, UUID id, long expected, string name, string description)
        {
            MetadataCalls++; LastOwner = owner;
            return new GameProjectResult { Error = GameProjectError.None, Project = Project };
        }
        public GameProjectResult PublishProject(UUID owner, UUID id, long expected)
        {
            PublishCalls++; LastOwner = owner;
            if (owner != _owner || id != ProjectID)
                return new GameProjectResult { Error = GameProjectError.NotFound };
            return expected == 1
                ? new GameProjectResult { Error = GameProjectError.None, Project = Project }
                : new GameProjectResult { Error = GameProjectError.RevisionConflict };
        }
        public bool ArchiveProject(UUID owner, UUID id)
        {
            ArchiveCalls++; LastOwner = owner;
            return owner == _owner && id == ProjectID;
        }
    }

    private static void TestRateLimiter()
    {
        var limiter = new GameCreatorRequestRateLimiter(2, 2, TimeSpan.FromMinutes(1));
        UUID principal = UUID.Random();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        Assert(limiter.TryAcquire(principal, start), "first request in rate window is allowed");
        Assert(limiter.TryAcquire(principal, start.AddSeconds(1)), "second request in rate window is allowed");
        Assert(!limiter.TryAcquire(principal, start.AddSeconds(2)), "request above per-principal limit is rejected");
        Assert(limiter.TryAcquire(principal, start.AddMinutes(1)), "request is allowed after its window resets");

        UUID secondPrincipal = UUID.Random();
        Assert(limiter.TryAcquire(secondPrincipal, start), "another principal has an independent rate window");
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
