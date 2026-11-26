using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace OpenSim.GameRuntime;

/// <summary>
/// Authenticated Creator project-management API. All ownership is derived from
/// the verified principal; project revisions are immutable service snapshots.
/// </summary>
public sealed class GameCreatorProjectsApiHandler : BaseStreamHandler
{
    private const int MaximumBodyBytes = GameProjectManifestValidator.MaximumManifestCharacters + 64 * 1024;
    private readonly GameCreatorRequestAuthenticator _authenticator;
    private readonly IGameProjectService _projects;
    private readonly GameCreatorRequestRateLimiter _rateLimiter;
    private readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    public GameCreatorProjectsApiHandler(string method, GameCreatorRequestAuthenticator authenticator,
        IGameProjectService projects, GameCreatorRequestRateLimiter? rateLimiter = null)
        : base(method, "/creator/projects")
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _rateLimiter = rateLimiter ?? new GameCreatorRequestRateLimiter();
    }

    public override string ContentType => "application/json";

    protected override byte[] ProcessRequest(string path, Stream requestData,
        IOSHttpRequest request, IOSHttpResponse response)
    {
        if (!request.IsSecured)
            return Reply(response, HttpStatusCode.Forbidden, new { error = "https_required" });

        GameCreatorAuthenticationStatus auth = _authenticator.Authenticate(
            request.Headers["Authorization"], request.Headers["X-OpenSim-Principal"], out UUID owner);
        if (auth != GameCreatorAuthenticationStatus.Authenticated)
        {
            response.StatusCode = auth == GameCreatorAuthenticationStatus.AuthenticationUnavailable
                ? (int)HttpStatusCode.ServiceUnavailable : (int)HttpStatusCode.Unauthorized;
            return Reply(response, (HttpStatusCode)response.StatusCode, new { error = "unauthorized" });
        }

        if (!_rateLimiter.TryAcquire(owner))
        {
            response.AddHeader("Retry-After", "60");
            return Reply(response, HttpStatusCode.TooManyRequests, new { error = "rate_limited" });
        }

        string[] segments = request.UriPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments[0] != "creator" || segments[1] != "projects")
            return Reply(response, HttpStatusCode.NotFound, new { error = "not_found" });

        if (segments.Length == 2)
        {
            if (HttpMethod == "GET") return List(request, response, owner);
            if (HttpMethod == "POST") return Create(requestData, request, response, owner);
            return Reply(response, HttpStatusCode.MethodNotAllowed, new { error = "method_not_allowed" });
        }

        if (!TryUUID(segments[2], out UUID projectID))
            return Reply(response, HttpStatusCode.NotFound, new { error = "not_found" });

        if (segments.Length == 3)
        {
            if (HttpMethod == "GET") return Get(response, owner, projectID);
            if (HttpMethod == "PATCH") return UpdateMetadata(requestData, request, response, owner, projectID);
        }
        else if (segments.Length == 4 && segments[3] == "draft" && HttpMethod == "PUT")
            return SaveDraft(requestData, request, response, owner, projectID);
        else if (segments.Length == 4 && segments[3] == "revisions" && HttpMethod == "GET")
            return Reply(response, HttpStatusCode.BadRequest, new { error = "revision_number_required" });
        else if (segments.Length == 5 && segments[3] == "revisions" && HttpMethod == "GET")
            return GetRevision(response, owner, projectID, segments[4]);
        else if (segments.Length == 4 && segments[3] == "archive" && HttpMethod == "POST")
            return Archive(response, owner, projectID);

        return Reply(response, HttpStatusCode.NotFound, new { error = "not_found" });
    }

    private byte[] Create(Stream body, IOSHttpRequest request, IOSHttpResponse response, UUID owner)
    {
        if (!ReadBody(body, request, out string json))
            return Reply(response, HttpStatusCode.BadRequest, new { error = "invalid_or_oversized_body" });
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasOwnerField(root)
                || !root.TryGetProperty("name", out JsonElement name)
                || name.ValueKind != JsonValueKind.String)
                return Reply(response, HttpStatusCode.BadRequest, new { error = "invalid_request" });
            string description = root.TryGetProperty("description", out JsonElement desc)
                && desc.ValueKind == JsonValueKind.String ? desc.GetString() ?? string.Empty : string.Empty;
            GameProjectResult result = _projects.CreateProject(owner, name.GetString() ?? string.Empty, description);
            return WriteResult(response, result, HttpStatusCode.Created);
        }
        catch (JsonException) { return Reply(response, HttpStatusCode.BadRequest, new { error = "invalid_json" }); }
    }

    private byte[] List(IOSHttpRequest request, IOSHttpResponse response, UUID owner)
    {
        int offset = 0, limit = 50;
        if (int.TryParse(request.QueryString["offset"], out int o)) offset = o;
        if (int.TryParse(request.QueryString["limit"], out int l)) limit = l;
        if (offset < 0 || limit < 1 || limit > 100)
            return Reply(response, HttpStatusCode.BadRequest, new { error = "invalid_pagination" });
        return Reply(response, HttpStatusCode.OK, new { projects = _projects.GetOwnedProjects(owner, offset, limit) });
    }

    private byte[] Get(IOSHttpResponse response, UUID owner, UUID projectID)
    {
        GameProjectResult result = _projects.GetProject(owner, projectID);
        return WriteResult(response, result);
    }

    private byte[] SaveDraft(Stream body, IOSHttpRequest request, IOSHttpResponse response, UUID owner, UUID projectID)
    {
        if (!ReadBody(body, request, out string json))
            return Reply(response, HttpStatusCode.BadRequest, new { error = "invalid_or_oversized_body" });
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasOwnerField(root)
                || !root.TryGetProperty("expected_current_revision", out JsonElement expected)
                || !expected.TryGetInt64(out long expectedRevision) || expectedRevision < 0
                || !root.TryGetProperty("manifest", out JsonElement manifestElement))
                return Reply(response, HttpStatusCode.BadRequest, new { error = "invalid_request" });

            GameProjectManifest? manifest = JsonSerializer.Deserialize<GameProjectManifest>(
                manifestElement.GetRawText(), _jsonOptions);
            if (manifest is null)
                return Reply(response, HttpStatusCode.BadRequest, new { error = "invalid_manifest" });
            GameProjectResult result = _projects.SaveDraft(owner, projectID, expectedRevision, manifest);
            return WriteResult(response, result);
        }
        catch (JsonException) { return Reply(response, HttpStatusCode.BadRequest, new { error = "invalid_json_or_manifest" }); }
    }

    private byte[] UpdateMetadata(Stream body, IOSHttpRequest request, IOSHttpResponse response, UUID owner, UUID projectID)
    {
        if (!ReadBody(body, request, out string json))
            return Reply(response, HttpStatusCode.BadRequest, new { error = "invalid_or_oversized_body" });
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasOwnerField(root)
                || !root.TryGetProperty("expected_current_revision", out JsonElement expected)
                || !expected.TryGetInt64(out long expectedRevision) || expectedRevision < 0
                || !root.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String)
                return Reply(response, HttpStatusCode.BadRequest, new { error = "invalid_request" });
            string description = root.TryGetProperty("description", out JsonElement desc)
                && desc.ValueKind == JsonValueKind.String ? desc.GetString() ?? string.Empty : string.Empty;
            return WriteResult(response, _projects.UpdateMetadata(owner, projectID, expectedRevision,
                name.GetString() ?? string.Empty, description));
        }
        catch (JsonException) { return Reply(response, HttpStatusCode.BadRequest, new { error = "invalid_json" }); }
    }

    private byte[] GetRevision(IOSHttpResponse response, UUID owner, UUID projectID, string revisionText)
    {
        if (!long.TryParse(revisionText, out long revision) || revision < 1)
            return Reply(response, HttpStatusCode.NotFound, new { error = "not_found" });
        GameProjectRevision? snapshot = _projects.GetRevision(owner, projectID, revision);
        return snapshot is null
            ? Reply(response, HttpStatusCode.NotFound, new { error = "not_found" })
            : Reply(response, HttpStatusCode.OK, new { revision = snapshot });
    }

    private byte[] Archive(IOSHttpResponse response, UUID owner, UUID projectID)
    {
        bool archived = _projects.ArchiveProject(owner, projectID);
        return archived
            ? Reply(response, HttpStatusCode.OK, new { archived = true })
            : Reply(response, HttpStatusCode.NotFound, new { error = "not_found" });
    }

    private static byte[] WriteResult(IOSHttpResponse response, GameProjectResult result,
        HttpStatusCode successStatus = HttpStatusCode.OK)
    {
        if (result.Success) return Reply(response, successStatus, new { project = result.Project });
        HttpStatusCode status = result.Error switch
        {
            GameProjectError.NotFound or GameProjectError.Forbidden => HttpStatusCode.NotFound,
            GameProjectError.InvalidData => HttpStatusCode.BadRequest,
            GameProjectError.RevisionConflict => HttpStatusCode.Conflict,
            GameProjectError.QuotaExceeded => HttpStatusCode.TooManyRequests,
            GameProjectError.StorageUnavailable => HttpStatusCode.ServiceUnavailable,
            _ => HttpStatusCode.InternalServerError
        };
        return Reply(response, status, new { error = result.Error.ToString() });
    }

    private static bool TryUUID(string value, out UUID id) =>
        UUID.TryParse(value, out id) && id != UUID.Zero;

    private static bool HasOwnerField(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string key = property.Name.Replace("_", string.Empty, StringComparison.Ordinal)
                    .Replace("-", string.Empty, StringComparison.Ordinal);
                if (key.Equals("owner", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("ownerid", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("authenticatedownerid", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (HasOwnerField(property.Value)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                if (HasOwnerField(item)) return true;
        }
        return false;
    }

    private static bool ReadBody(Stream body, IOSHttpRequest request, out string json)
    {
        json = string.Empty;
        if (request.ContentLength64 > MaximumBodyBytes) return false;
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = body.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaximumBodyBytes) return false;
            buffer.Write(chunk, 0, read);
        }
        json = Encoding.UTF8.GetString(buffer.ToArray());
        return !string.IsNullOrWhiteSpace(json);
    }

    private static byte[] Reply(IOSHttpResponse response, HttpStatusCode status, object payload)
    {
        response.StatusCode = (int)status;
        response.ContentType = "application/json; charset=utf-8";
        response.AddHeader("Cache-Control", "no-store");
        response.AddHeader("X-Content-Type-Options", "nosniff");
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new OpenMetaverseUuidConverter());
        return options;
    }

    private sealed class OpenMetaverseUuidConverter : JsonConverter<UUID>
    {
        public override UUID Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !UUID.TryParse(reader.GetString(), out UUID id))
                throw new JsonException("UUID must be a valid string.");
            return id;
        }
        public override void Write(Utf8JsonWriter writer, UUID value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
