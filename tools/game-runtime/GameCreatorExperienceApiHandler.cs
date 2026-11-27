using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;

namespace OpenSim.GameRuntime;

/// <summary>
/// Creator Experience/Place authoring API. Every operation uses the authenticated
/// principal and the same provider instance as the project service.
/// </summary>
public sealed class GameCreatorExperienceApiHandler : BaseStreamHandler
{
    private const int MaximumBodyBytes = GameExperienceService.MaximumDefinitionCharacters + 64 * 1024;
    private readonly GameCreatorRequestAuthenticator _authenticator;
    private readonly GameExperienceService _experiences;
    private readonly GameProjectV2Service _projectsV2;
    private readonly GameCreatorRequestRateLimiter _rateLimiter;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public GameCreatorExperienceApiHandler(string method, GameCreatorRequestAuthenticator authenticator,
        GameExperienceService experiences, GameProjectV2Service projectsV2,
        GameCreatorRequestRateLimiter? rateLimiter = null)
        : base(method, "/creator/projects")
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _experiences = experiences ?? throw new ArgumentNullException(nameof(experiences));
        _projectsV2 = projectsV2 ?? throw new ArgumentNullException(nameof(projectsV2));
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
            HttpStatusCode status = auth == GameCreatorAuthenticationStatus.AuthenticationUnavailable
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized;
            return Reply(response, status, new { error = "unauthorized" });
        }
        if (!_rateLimiter.TryAcquire(owner))
        {
            response.AddHeader("Retry-After", "60");
            return Reply(response, HttpStatusCode.TooManyRequests, new { error = "rate_limited" });
        }

        string[] s = request.UriPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (s.Length < 4 || s[0] != "creator" || s[1] != "projects"
            || !TryId(s[2], out UUID projectID))
            return Reply(response, HttpStatusCode.NotFound, new { error = "not_found" });

        if (s.Length == 4 && s[3] == "experiences")
        {
            if (HttpMethod == "GET")
                return Reply(response, HttpStatusCode.OK,
                    new { experiences = _experiences.GetExperiences(owner, projectID) });
            if (HttpMethod == "POST")
                return CreateExperience(requestData, request, response, owner, projectID);
        }

        if (s.Length >= 5 && s[3] == "experiences" && TryId(s[4], out UUID experienceID))
        {
            if (s.Length == 5 && HttpMethod == "GET")
            {
                var item = _experiences.GetExperiences(owner, projectID)
                    .FirstOrDefault(x => x.ExperienceID == experienceID);
                return item is null ? NotFound(response) : Reply(response, HttpStatusCode.OK, new { experience = item });
            }
            if (s.Length == 6 && s[5] == "revisions")
            {
                if (HttpMethod == "PUT")
                    return SaveExperienceRevision(requestData, request, response, owner, projectID, experienceID);
                if (HttpMethod == "GET")
                    return Reply(response, HttpStatusCode.BadRequest, new { error = "revision_number_required" });
            }
            if (s.Length == 7 && s[5] == "revisions" && HttpMethod == "GET"
                && long.TryParse(s[6], out long experienceRevision) && experienceRevision > 0)
            {
                var revision = _experiences.GetExperienceRevision(owner, projectID, experienceID, experienceRevision);
                return revision is null ? NotFound(response) : Reply(response, HttpStatusCode.OK, new { revision });
            }
            if (s.Length == 6 && s[5] == "places")
            {
                if (HttpMethod == "GET")
                    return Reply(response, HttpStatusCode.OK,
                        new { places = _experiences.GetPlaces(owner, projectID, experienceID) });
                if (HttpMethod == "POST")
                    return CreatePlace(requestData, request, response, owner, projectID, experienceID);
            }
            if (s.Length >= 7 && s[5] == "places" && TryId(s[6], out UUID placeID))
            {
                if (s.Length == 7 && HttpMethod == "GET")
                {
                    var place = _experiences.GetPlaces(owner, projectID, experienceID)
                        .FirstOrDefault(x => x.PlaceID == placeID);
                    return place is null ? NotFound(response) : Reply(response, HttpStatusCode.OK, new { place });
                }
                if (s.Length == 8 && s[7] == "revisions" && HttpMethod == "PUT")
                    return SavePlaceRevision(requestData, request, response, owner, projectID, experienceID, placeID);
                if (s.Length == 8 && s[7] == "revisions" && HttpMethod == "GET")
                    return Reply(response, HttpStatusCode.BadRequest, new { error = "revision_number_required" });
                if (s.Length == 9 && s[7] == "revisions" && HttpMethod == "GET"
                    && long.TryParse(s[8], out long placeRevision) && placeRevision > 0)
                {
                    var revision = _experiences.GetPlaceRevision(owner, projectID, experienceID, placeID, placeRevision);
                    return revision is null ? NotFound(response) : Reply(response, HttpStatusCode.OK, new { revision });
                }
            }
        }

        if (s.Length == 5 && s[3] == "revisions" && s[4] == "v2" && HttpMethod == "PUT")
            return SaveProjectV2Revision(requestData, request, response, owner, projectID);

        return Reply(response, HttpStatusCode.NotFound, new { error = "not_found" });
    }

    private byte[] CreateExperience(Stream body, IOSHttpRequest request, IOSHttpResponse response,
        UUID owner, UUID projectID)
    {
        if (!ReadObject(body, request, out JsonDocument? doc, out HttpStatusCode error))
            return Reply(response, error, new { error = "invalid_request" });
        using (doc)
        {
            if (HasOwnerField(doc!.RootElement)) return BadRequest(response);
            var created = _experiences.CreateExperience(owner, projectID);
            return created is null ? NotFound(response)
                : Reply(response, HttpStatusCode.Created, new { experience = created });
        }
    }

    private byte[] CreatePlace(Stream body, IOSHttpRequest request, IOSHttpResponse response,
        UUID owner, UUID projectID, UUID experienceID)
    {
        if (!ReadObject(body, request, out JsonDocument? doc, out HttpStatusCode error))
            return Reply(response, error, new { error = "invalid_request" });
        using (doc)
        {
            if (HasOwnerField(doc!.RootElement)) return BadRequest(response);
            var created = _experiences.CreatePlace(owner, projectID, experienceID);
            return created is null ? NotFound(response)
                : Reply(response, HttpStatusCode.Created, new { place = created });
        }
    }

    private byte[] SavePlaceRevision(Stream body, IOSHttpRequest request, IOSHttpResponse response,
        UUID owner, UUID projectID, UUID experienceID, UUID placeID)
    {
        if (!ReadObject(body, request, out JsonDocument? doc, out HttpStatusCode error))
            return Reply(response, error, new { error = "invalid_request" });
        using (doc)
        {
            JsonElement root = doc!.RootElement;
            if (HasOwnerField(root)
                || !Long(root, "expectedCurrentRevision", out long expected) || expected < 0
                || !Int(root, "schemaVersion", out int schema) || schema != 1
                || !String(root, "name", out string name)
                || !String(root, "description", out string description)
                || !root.TryGetProperty("definition", out JsonElement definition)
                || definition.ValueKind != JsonValueKind.Object)
                return BadRequest(response);

            bool saved = _experiences.SavePlaceRevision(owner, projectID, experienceID, placeID,
                expected, schema, name, description, definition.GetRawText());
            return saved ? Reply(response, HttpStatusCode.OK, new { saved = true, revision = expected + 1 })
                : Reply(response, HttpStatusCode.Conflict, new { error = "revision_conflict_or_invalid_graph" });
        }
    }

    private byte[] SaveExperienceRevision(Stream body, IOSHttpRequest request, IOSHttpResponse response,
        UUID owner, UUID projectID, UUID experienceID)
    {
        if (!ReadObject(body, request, out JsonDocument? doc, out HttpStatusCode error))
            return Reply(response, error, new { error = "invalid_request" });
        using (doc)
        {
            JsonElement root = doc!.RootElement;
            if (HasOwnerField(root) || !Long(root, "expectedCurrentRevision", out long expected) || expected < 0
                || !Int(root, "schemaVersion", out int schema) || schema != 1
                || !String(root, "name", out string name) || !TryIdProperty(root, "startPlaceID", out UUID start)
                || !root.TryGetProperty("places", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
                return BadRequest(response);

            var pins = new List<GameExperiencePlacePin>();
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryIdProperty(item, "placeID", out UUID place)
                    || !Long(item, "placeRevision", out long placeRevision)
                    || !Int(item, "sortOrder", out int order))
                    return BadRequest(response);
                pins.Add(new GameExperiencePlacePin { PlaceID = place, PlaceRevision = placeRevision, SortOrder = order });
                if (pins.Count > GameProjectManifestValidator.MaximumWorlds) return BadRequest(response);
            }

            bool saved = _experiences.SaveExperienceRevision(owner, projectID, experienceID,
                expected, schema, name, start, pins);
            return saved ? Reply(response, HttpStatusCode.OK, new { saved = true, revision = expected + 1 })
                : Reply(response, HttpStatusCode.Conflict, new { error = "revision_conflict_or_invalid_graph" });
        }
    }

    private byte[] SaveProjectV2Revision(Stream body, IOSHttpRequest request, IOSHttpResponse response,
        UUID owner, UUID projectID)
    {
        if (!ReadObject(body, request, out JsonDocument? doc, out HttpStatusCode error))
            return Reply(response, error, new { error = "invalid_request" });
        using (doc)
        {
            JsonElement root = doc!.RootElement;
            if (HasOwnerField(root) || !Long(root, "expectedCurrentRevision", out long expected) || expected < 0
                || !Int(root, "schemaVersion", out int schema) || schema != 2
                || !root.TryGetProperty("experiences", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
                return BadRequest(response);

            var pins = new List<GameProjectExperienceRevisionData>();
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryIdProperty(item, "experienceID", out UUID experience)
                    || !Long(item, "experienceRevision", out long revision)
                    || !Int(item, "sortOrder", out int order))
                    return BadRequest(response);
                pins.Add(new GameProjectExperienceRevisionData
                    { ExperienceID = experience, ExperienceRevision = revision, SortOrder = order });
                if (pins.Count > GameProjectManifestValidator.MaximumWorlds) return BadRequest(response);
            }

            string manifest = JsonSerializer.Serialize(new { schemaVersion = 2 }, JsonOptions);
            bool saved = _projectsV2.SaveDraftRevision(owner, projectID, expected, manifest, pins);
            return saved ? Reply(response, HttpStatusCode.OK, new { saved = true, revision = expected + 1, schemaVersion = 2 })
                : Reply(response, HttpStatusCode.Conflict, new { error = "revision_conflict_or_invalid_graph" });
        }
    }

    private static bool ReadObject(Stream body, IOSHttpRequest request, out JsonDocument? document,
        out HttpStatusCode error)
    {
        document = null;
        error = HttpStatusCode.BadRequest;
        if (request.ContentLength64 > MaximumBodyBytes) { error = HttpStatusCode.RequestEntityTooLarge; return false; }
        using var memory = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = body.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > MaximumBodyBytes) { error = HttpStatusCode.RequestEntityTooLarge; return false; }
            memory.Write(buffer, 0, read);
        }
        try
        {
            document = JsonDocument.Parse(memory.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static bool HasOwnerField(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty p in value.EnumerateObject())
            {
                string key = p.Name.Replace("_", "").Replace("-", "");
                if (key.Equals("owner", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("ownerid", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("authenticatedownerid", StringComparison.OrdinalIgnoreCase)) return true;
                if (HasOwnerField(p.Value)) return true;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) if (HasOwnerField(item)) return true;
        return false;
    }

    private static bool TryId(string value, out UUID id) => UUID.TryParse(value, out id) && id != UUID.Zero;
    private static bool TryIdProperty(JsonElement root, string name, out UUID id) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        && TryId(value.GetString() ?? "", out id) || (id = UUID.Zero) != UUID.Zero;
    private static bool Long(JsonElement root, string name, out long value) =>
        root.TryGetProperty(name, out JsonElement v) && v.TryGetInt64(out value) || (value = 0) != long.MinValue;
    private static bool Int(JsonElement root, string name, out int value) =>
        root.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out value) || (value = 0) != int.MinValue;
    private static bool String(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.String) return false;
        value = v.GetString() ?? string.Empty;
        return true;
    }

    private static byte[] NotFound(IOSHttpResponse response) =>
        Reply(response, HttpStatusCode.NotFound, new { error = "not_found" });
    private static byte[] BadRequest(IOSHttpResponse response) =>
        Reply(response, HttpStatusCode.BadRequest, new { error = "invalid_request" });
    private static byte[] Reply(IOSHttpResponse response, HttpStatusCode status, object payload)
    {
        response.StatusCode = (int)status;
        response.ContentType = "application/json; charset=utf-8";
        response.AddHeader("Cache-Control", "no-store");
        response.AddHeader("X-Content-Type-Options", "nosniff");
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
    }
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new UuidConverter());
        return options;
    }
    private sealed class UuidConverter : JsonConverter<UUID>
    {
        public override UUID Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && UUID.TryParse(reader.GetString(), out UUID id)
                ? id : throw new JsonException("Invalid UUID.");
        public override void Write(Utf8JsonWriter writer, UUID value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
