using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.GameRuntime;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;

static void Assert(bool value, string message)
{
    if (!value) throw new Exception("ASSERT FAILED: " + message);
}

string temp = Path.Combine(Path.GetTempPath(), "opensim-next-creator-e2e-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
string database = Path.Combine(temp, "creator-e2e.db");
string connection = "URI=file:" + database + ";UseUTF16Encoding=True";
string certPath = Path.Combine(temp, "localhost.pfx");
const string certPassword = "e2e-only-password";
using (RSA rsa = RSA.Create(2048))
{
    var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost");
    san.AddIpAddress(IPAddress.Loopback);
    request.CertificateExtensions.Add(san.Build());
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2));
    File.WriteAllBytes(certPath, certificate.Export(X509ContentType.Pfx, certPassword));
}

var config = new IniConfigSource();
IConfig runtimeConfig = config.AddConfig("GameRuntimeService");
runtimeConfig.Set("Enabled", true);
runtimeConfig.Set("WorkerAssemblyPath", "worker/GamePhysicsWorker.dll");
IConfig authConfig = config.AddConfig("AuthenticationService");
authConfig.Set("LocalServiceModule", Path.Combine(AppContext.BaseDirectory, "OpenSim.Services.AuthenticationService.dll") + ":WebkeyAuthenticationService");
authConfig.Set("StorageProvider", Path.Combine(AppContext.BaseDirectory, "OpenSim.Data.SQLite.dll") + ":SQLiteAuthenticationData");
authConfig.Set("ConnectionString", connection);
authConfig.Set("Realm", "auth");
IConfig projectConfig = config.AddConfig("GameProjectService");
projectConfig.Set("StorageProvider", Path.Combine(AppContext.BaseDirectory, "OpenSim.Data.SQLite.dll") + ":SQLiteGameProjectData");
projectConfig.Set("ConnectionString", connection);

string sqliteAssemblyPath = Path.Combine(AppContext.BaseDirectory, "OpenSim.Data.SQLite.dll");
Assembly sqliteAssembly = Assembly.LoadFrom(sqliteAssemblyPath);
Type sqliteAuthType = sqliteAssembly.GetType("OpenSim.Data.SQLite.SQLiteAuthenticationData", true)!;
Assert(typeof(IAuthenticationData).IsAssignableFrom(sqliteAuthType),
    "production SQLiteAuthenticationData implements the host storage interface");
try
{
    _ = Activator.CreateInstance(sqliteAuthType, new object[] { connection, "auth" });
}
catch (Exception exception)
{
    throw new InvalidOperationException("Could not initialize production SQLite authentication storage.", exception);
}

string authAssemblyPath = Path.Combine(AppContext.BaseDirectory, "OpenSim.Services.AuthenticationService.dll");
Assembly authAssembly = Assembly.LoadFrom(authAssemblyPath);
Type authType = authAssembly.GetType("OpenSim.Services.AuthenticationService.WebkeyAuthenticationService", true)!;
Assert(typeof(IAuthenticationService).IsAssignableFrom(authType),
    "production WebkeyAuthenticationService implements the host IAuthenticationService contract");
IAuthenticationService auth;
try
{
    auth = (IAuthenticationService)Activator.CreateInstance(authType, new object[] { config })!;
}
catch (Exception exception)
{
    throw new InvalidOperationException("Could not construct production WebkeyAuthenticationService with configured SQLite auth provider.", exception);
}
UUID principal = UUID.Random();
string webKey = UUID.Random().ToString();
Assert(auth.SetAuthInfo(new AuthInfo
{
    PrincipalID = principal,
    AccountType = "UserAccount",
    WebLoginKey = webKey,
    PasswordHash = string.Empty,
    PasswordSalt = string.Empty
}), "production SQLite authentication provider stores the test principal");
string token = auth.Authenticate(principal, webKey, 30);
Assert(!string.IsNullOrWhiteSpace(token) && auth.Verify(principal, token, 30),
    "production WebkeyAuthenticationService issues a verifiable principal-bound token");

using var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();

var server = new BaseHttpServer((uint)port, true, certPath, certPassword);
GameCreatorServiceConnector? connector = null;
try
{
    connector = new GameCreatorServiceConnector(config, server, string.Empty, null, auth);
    server.Start();

    using var handler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    };
    using var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:" + port) };
    client.DefaultRequestHeaders.Add("X-OpenSim-Principal", principal.ToString());
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    using HttpResponseMessage create = await client.PostAsync("/creator/projects",
        new StringContent("{\"name\":\"Robust live E2E\",\"description\":\"Production auth and SQLite\"}", System.Text.Encoding.UTF8, "application/json"));
    string createBody = await create.Content.ReadAsStringAsync();
    Assert(create.StatusCode == HttpStatusCode.Created, "live HTTPS Creator create route returns 201; got " + (int)create.StatusCode + " " + createBody);

    using JsonDocument created = JsonDocument.Parse(createBody);
    JsonElement project = created.RootElement.GetProperty("project");
    string projectIdText = project.EnumerateObject().First(p => p.Name.Equals("ProjectID", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
    Assert(UUID.TryParse(projectIdText, out UUID projectId) && projectId != UUID.Zero,
        "live Creator response returns a generated project UUID");

    using HttpResponseMessage list = await client.GetAsync("/creator/projects?offset=0&limit=50");
    string listBody = await list.Content.ReadAsStringAsync();
    Assert(list.StatusCode == HttpStatusCode.OK && listBody.Contains(projectIdText, StringComparison.OrdinalIgnoreCase),
        "live HTTPS list route reads the project back from the configured SQLite provider");

    string worldId = UUID.Random().ToString();
    string manifest = "{\"schemaVersion\":1,\"name\":\"Robust live E2E\",\"worlds\":[{\"worldID\":\"" + worldId +
        "\",\"name\":\"Main World\",\"sizeX\":256,\"sizeY\":256,\"spawnPoints\":[],\"assetReferences\":[]}]," +
        "\"settings\":{\"gameMode\":\"sandbox\",\"defaultWorldID\":\"" + worldId +
        "\",\"visibility\":\"private\",\"maxPlayers\":16,\"tags\":[]}}";
    using HttpResponseMessage saveDraft = await client.PutAsync("/creator/projects/" + projectIdText + "/draft",
        new StringContent("{\"expected_current_revision\":0,\"manifest\":" + manifest + "}", System.Text.Encoding.UTF8, "application/json"));
    string saveBody = await saveDraft.Content.ReadAsStringAsync();
    Assert(saveDraft.StatusCode == HttpStatusCode.OK,
        "live HTTPS draft save persists a validated manifest; got " + (int)saveDraft.StatusCode + " " + saveBody);

    using HttpResponseMessage publish = await client.PostAsync("/creator/projects/" + projectIdText + "/publish",
        new StringContent("{\"expected_current_revision\":1}", System.Text.Encoding.UTF8, "application/json"));
    string publishBody = await publish.Content.ReadAsStringAsync();
    Assert(publish.StatusCode == HttpStatusCode.OK,
        "live HTTPS publish validates and atomically publishes revision 1; got " + (int)publish.StatusCode + " " + publishBody);

    using HttpResponseMessage publishAgain = await client.PostAsync("/creator/projects/" + projectIdText + "/publish",
        new StringContent("{\"expected_current_revision\":1}", System.Text.Encoding.UTF8, "application/json"));
    Assert(publishAgain.StatusCode == HttpStatusCode.Conflict,
        "live HTTPS rejects repeated publication after the project leaves Draft");

    using var foreignClient = new HttpClient(handler) { BaseAddress = client.BaseAddress };
    foreignClient.DefaultRequestHeaders.Add("X-OpenSim-Principal", UUID.Random().ToString());
    foreignClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    using HttpResponseMessage foreign = await foreignClient.GetAsync("/creator/projects/" + projectIdText);
    Assert(foreign.StatusCode == HttpStatusCode.Unauthorized,
        "principal-bound token cannot be replayed under another creator UUID");

    Console.WriteLine("PASS: live BaseHttpServer HTTPS + production WebkeyAuthenticationService + SQLite Creator API create/list/draft-save/publish/owner isolation");
}
finally
{
    connector?.Dispose();
    server.Stop();
}
