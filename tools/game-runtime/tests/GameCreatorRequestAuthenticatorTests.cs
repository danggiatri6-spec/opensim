using OpenMetaverse;
using OpenSim.GameRuntime;
using OpenSim.Services.Interfaces;

internal static class GameCreatorRequestAuthenticatorTests
{
    public static void Run()
    {
        UUID ownerA = UUID.Random();
        UUID ownerB = UUID.Random();
        var authService = new FakeAuthenticationService(ownerA, "valid-token");
        var authenticator = new GameCreatorRequestAuthenticator(authService);

        var status = authenticator.Authenticate(
            "Bearer valid-token", ownerA.ToString(), out UUID authenticated);
        Assert(status == GameCreatorAuthenticationStatus.Authenticated && authenticated == ownerA,
            "valid principal-bound token authenticates the matching principal");
        Assert(authService.LastPrincipalID == ownerA
            && authService.LastLifetime == GameCreatorRequestAuthenticator.TokenVerificationLifetimeMinutes,
            "verification uses the supplied principal and fixed 30-minute lifetime");

        status = authenticator.Authenticate(
            "Bearer valid-token", ownerB.ToString(), out authenticated);
        Assert(status == GameCreatorAuthenticationStatus.InvalidCredentials && authenticated == UUID.Zero,
            "a token for principal A cannot authenticate principal B");

        status = authenticator.Authenticate(null, ownerA.ToString(), out authenticated);
        Assert(status == GameCreatorAuthenticationStatus.MissingCredentials,
            "missing bearer credentials are rejected");

        status = authenticator.Authenticate(
            "Basic valid-token", ownerA.ToString(), out authenticated);
        Assert(status == GameCreatorAuthenticationStatus.InvalidCredentials,
            "Basic service credentials are not accepted as Creator user tokens");

        status = authenticator.Authenticate(
            "Bearer valid-token", "not-a-uuid", out authenticated);
        Assert(status == GameCreatorAuthenticationStatus.InvalidCredentials,
            "malformed principal header is rejected");

        status = authenticator.Authenticate(
            "Bearer valid-token", UUID.Zero.ToString(), out authenticated);
        Assert(status == GameCreatorAuthenticationStatus.InvalidCredentials,
            "zero principal UUID is rejected");

        status = authenticator.Authenticate(
            "Bearer token with-spaces", ownerA.ToString(), out authenticated);
        Assert(status == GameCreatorAuthenticationStatus.InvalidCredentials,
            "bearer tokens containing whitespace are rejected");

        authService.ThrowOnVerify = true;
        status = authenticator.Authenticate(
            "Bearer valid-token", ownerA.ToString(), out authenticated);
        Assert(status == GameCreatorAuthenticationStatus.AuthenticationUnavailable
            && authenticated == UUID.Zero,
            "authentication backend failure is distinct from invalid credentials");
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception("ASSERT FAILED: " + message);
    }

    private sealed class FakeAuthenticationService : IAuthenticationService
    {
        private readonly UUID _expectedPrincipal;
        private readonly string _expectedToken;

        public FakeAuthenticationService(UUID expectedPrincipal, string expectedToken)
        {
            _expectedPrincipal = expectedPrincipal;
            _expectedToken = expectedToken;
        }

        public UUID LastPrincipalID { get; private set; }
        public int LastLifetime { get; private set; }
        public bool ThrowOnVerify { get; set; }

        public string Authenticate(UUID principalID, string password, int lifetime) => string.Empty;
        public string Authenticate(UUID principalID, string password, int lifetime, out UUID realID)
        {
            realID = UUID.Zero;
            return string.Empty;
        }

        public bool Verify(UUID principalID, string token, int lifetime)
        {
            LastPrincipalID = principalID;
            LastLifetime = lifetime;
            if (ThrowOnVerify) throw new InvalidOperationException("test authentication backend failure");
            return principalID == _expectedPrincipal && token == _expectedToken;
        }

        public bool Release(UUID principalID, string token) => false;
        public bool SetPassword(UUID principalID, string passwd) => false;
        public AuthInfo GetAuthInfo(UUID principalID) => null;
        public bool SetAuthInfo(AuthInfo info) => false;
    }
}
