using System.Net;
using System.Text;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace OpenSim.GameRuntime;

/// <summary>
/// Small JSON REST surface for Creator-owned Game Instance lifecycle.
/// Owner identity is exclusively derived from a verified principal-bound bearer token.
/// </summary>
public sealed class GameCreatorApiHandler : BaseStreamHandler
{
    private const int MaximumBodyBytes = 16 * 1024;
    private readonly GameCreatorRequestAuthenticator _authenticator;
    private readonly IGameRuntimeService _runtime;
    private readonly GameCreatorRequestRateLimiter _rateLimiter;

    public GameCreatorApiHandler(
        string httpMethod,
        GameCreatorRequestAuthenticator authenticator,
        IGameRuntimeService runtime,
        GameCreatorRequestRateLimiter? rateLimiter = null)
        : base(httpMethod, "/creator/instances")
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _rateLimiter = rateLimiter ?? new GameCreatorRequestRateLimiter();
    }

    public override string ContentType => "application/json";

    protected override byte[] ProcessRequest(
        string path, Stream requestData, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        if (!httpRequest.IsSecured)
            return JsonResponse(httpResponse, HttpStatusCode.Forbidden, new { error = "https_required" });

        if (!TryAuthenticate(httpRequest, httpResponse, out UUID ownerID))
            return JsonResponse(httpResponse, (HttpStatusCode)httpResponse.StatusCode, new { error = "unauthorized" });

        if (!_rateLimiter.TryAcquire(ownerID))
        {
            httpResponse.AddHeader("Retry-After", "60");
            return JsonResponse(httpResponse, HttpStatusCode.TooManyRequests, new { error = "rate_limited" });
        }

        string[] segments = httpRequest.UriPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments[0] != "creator" || segments[1] != "instances")
            return JsonResponse(httpResponse, HttpStatusCode.NotFound, new { error = "not_found" });

        if (HttpMethod == "POST")
        {
            if (segments.Length == 2)
                return StartInstance(requestData, httpRequest, httpResponse, ownerID);
            if (segments.Length == 4 && segments[3] == "stop")
                return StopInstance(httpResponse, ownerID, segments[2]);
            return JsonResponse(httpResponse, HttpStatusCode.NotFound, new { error = "not_found" });
        }

        if (HttpMethod == "GET")
        {
            if (segments.Length == 2)
                return ListInstances(httpRequest, httpResponse, ownerID);
            if (segments.Length == 3)
                return GetInstance(httpResponse, ownerID, segments[2]);
        }

        return JsonResponse(httpResponse, HttpStatusCode.MethodNotAllowed, new { error = "method_not_allowed" });
    }

    private bool TryAuthenticate(IOSHttpRequest request, IOSHttpResponse response, out UUID ownerID)
    {
        GameCreatorAuthenticationStatus status = _authenticator.Authenticate(
            request.Headers["Authorization"],
            request.Headers["X-OpenSim-Principal"],
            out ownerID);

        if (status == GameCreatorAuthenticationStatus.Authenticated)
            return true;

        response.StatusCode = status == GameCreatorAuthenticationStatus.AuthenticationUnavailable
            ? (int)HttpStatusCode.ServiceUnavailable
            : (int)HttpStatusCode.Unauthorized;
        return false;
    }

    private byte[] StartInstance(Stream body, IOSHttpRequest request, IOSHttpResponse response, UUID ownerID)
    {
        if (!TryReadBody(body, request, out string json))
            return JsonResponse(response, HttpStatusCode.BadRequest, new { error = "invalid_or_oversized_body" });

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Any(p => IsOwnerIdentityField(p.Name))
                || !root.TryGetProperty("project_id", out JsonElement projectValue)
                || !root.TryGetProperty("revision", out JsonElement revisionValue)
                || projectValue.ValueKind != JsonValueKind.String
                || !UUID.TryParse(projectValue.GetString(), out UUID projectID)
                || projectID == UUID.Zero
                || !revisionValue.TryGetInt64(out long revision)
                || revision < 1)
                return JsonResponse(response, HttpStatusCode.BadRequest, new { error = "invalid_request" });

            GameInstanceResult result = _runtime.StartInstanceAsync(ownerID, projectID, revision)
                .GetAwaiter().GetResult();
            return WriteResult(response, result, HttpStatusCode.Created);
        }
        catch (JsonException)
        {
            return JsonResponse(response, HttpStatusCode.BadRequest, new { error = "invalid_json" });
        }
    }

    private byte[] StopInstance(IOSHttpResponse response, UUID ownerID, string instanceText)
    {
        if (!UUID.TryParse(instanceText, out UUID instanceID) || instanceID == UUID.Zero)
            return JsonResponse(response, HttpStatusCode.NotFound, new { error = "not_found" });

        GameInstanceResult result = _runtime.StopInstanceAsync(ownerID, instanceID)
            .GetAwaiter().GetResult();
        return WriteResult(response, result);
    }

    private byte[] GetInstance(IOSHttpResponse response, UUID ownerID, string instanceText)
    {
        if (!UUID.TryParse(instanceText, out UUID instanceID) || instanceID == UUID.Zero)
            return JsonResponse(response, HttpStatusCode.NotFound, new { error = "not_found" });

        GameInstanceResult result = _runtime.GetInstanceAsync(ownerID, instanceID)
            .GetAwaiter().GetResult();
        return WriteResult(response, result);
    }

    private byte[] ListInstances(IOSHttpRequest request, IOSHttpResponse response, UUID ownerID)
    {
        int offset = 0;
        int limit = 50;
        if (int.TryParse(request.QueryString["offset"], out int parsedOffset))
            offset = parsedOffset;
        if (int.TryParse(request.QueryString["limit"], out int parsedLimit))
            limit = parsedLimit;
        if (offset < 0 || limit < 1 || limit > 100)
            return JsonResponse(response, HttpStatusCode.BadRequest, new { error = "invalid_pagination" });

        List<GameInstance> instances = _runtime.GetOwnedInstancesAsync(ownerID, offset, limit)
            .GetAwaiter().GetResult();
        return JsonResponse(response, HttpStatusCode.OK, new { instances });
    }

    private static bool IsOwnerIdentityField(string name)
    {
        string normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Equals("ownerid", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("owner", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadBody(Stream body, IOSHttpRequest request, out string json)
    {
        json = string.Empty;
        if (request.ContentLength64 > MaximumBodyBytes)
            return false;

        using var memory = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = body.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > MaximumBodyBytes)
                return false;
            memory.Write(buffer, 0, read);
        }

        json = Encoding.UTF8.GetString(memory.ToArray());
        return !string.IsNullOrWhiteSpace(json);
    }

    private static byte[] WriteResult(
        IOSHttpResponse response, GameInstanceResult result, HttpStatusCode successStatus = HttpStatusCode.OK)
    {
        if (result.Success)
            return JsonResponse(response, successStatus, new { instance = result.Instance });

        HttpStatusCode status = result.Error switch
        {
            GameInstanceError.Forbidden => HttpStatusCode.NotFound,
            GameInstanceError.NotFound => HttpStatusCode.NotFound,
            GameInstanceError.InvalidRevision or GameInstanceError.InvalidManifest => HttpStatusCode.BadRequest,
            GameInstanceError.AlreadyRunning => HttpStatusCode.Conflict,
            GameInstanceError.CapacityExceeded or GameInstanceError.WorkerUnavailable => HttpStatusCode.ServiceUnavailable,
            GameInstanceError.InvalidState => HttpStatusCode.Conflict,
            _ => HttpStatusCode.InternalServerError
        };
        return JsonResponse(response, status, new { error = result.Error.ToString() });
    }

    private static byte[] JsonResponse(IOSHttpResponse response, HttpStatusCode status, object payload)
    {
        response.StatusCode = (int)status;
        response.ContentType = "application/json; charset=utf-8";
        response.AddHeader("Cache-Control", "no-store");
        response.AddHeader("X-Content-Type-Options", "nosniff");
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
    }

}
