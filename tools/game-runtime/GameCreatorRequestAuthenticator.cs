using OpenMetaverse;
using OpenSim.Services.Interfaces;

namespace OpenSim.GameRuntime;

public enum GameCreatorAuthenticationStatus
{
    Authenticated = 0,
    MissingCredentials = 1,
    InvalidCredentials = 2,
    AuthenticationUnavailable = 3
}

/// <summary>
/// Validates a principal-bound OpenSim authentication token for Creator API calls.
/// The principal is never taken from request data without successful token verification.
/// </summary>
public sealed class GameCreatorRequestAuthenticator
{
    public const int TokenVerificationLifetimeMinutes = 30;
    private readonly IAuthenticationService _authenticationService;

    public GameCreatorRequestAuthenticator(IAuthenticationService authenticationService)
    {
        _authenticationService = authenticationService
            ?? throw new ArgumentNullException(nameof(authenticationService));
    }

    public GameCreatorAuthenticationStatus Authenticate(
        string? authorizationHeader,
        string? principalHeader,
        out UUID authenticatedPrincipalID)
    {
        authenticatedPrincipalID = UUID.Zero;

        if (string.IsNullOrWhiteSpace(authorizationHeader)
            || string.IsNullOrWhiteSpace(principalHeader))
            return GameCreatorAuthenticationStatus.MissingCredentials;

        if (!UUID.TryParse(principalHeader.Trim(), out UUID principalID)
            || principalID == UUID.Zero)
            return GameCreatorAuthenticationStatus.InvalidCredentials;

        const string bearerPrefix = "Bearer ";
        if (!authorizationHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            return GameCreatorAuthenticationStatus.InvalidCredentials;

        string token = authorizationHeader.Substring(bearerPrefix.Length);
        if (string.IsNullOrWhiteSpace(token)
            || token.Length > 4096
            || token.Any(char.IsWhiteSpace))
            return GameCreatorAuthenticationStatus.InvalidCredentials;

        bool valid;
        try
        {
            valid = _authenticationService.Verify(
                principalID, token, TokenVerificationLifetimeMinutes);
        }
        catch
        {
            // Keep authentication backend failures distinct from invalid credentials.
            return GameCreatorAuthenticationStatus.AuthenticationUnavailable;
        }

        if (!valid)
            return GameCreatorAuthenticationStatus.InvalidCredentials;

        authenticatedPrincipalID = principalID;
        return GameCreatorAuthenticationStatus.Authenticated;
    }
}
