using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Erp.IntegrationTests;

/// <summary>
/// An in-process OpenID Connect provider (discovery, JWKS, token endpoint) behind the "erp-oidc" HttpClient, so external
/// sign-in is tested end to end without network access. Tests play the browser: they read the authorization request
/// produced by the API, "authenticate" the user by registering a code, then call the API's callback.
/// </summary>
public sealed class FakeOidcProvider
{
    public const string Issuer = "https://idp.test";
    public const string ClientId = "erp-test-client";
    public const string SecretName = "test-idp";
    public const string ClientSecret = "fake-client-secret";

    public static readonly FakeOidcProvider Instance = new();

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new(StringComparer.Ordinal);

    private FakeOidcProvider()
    {
        Key = new RsaSecurityKey(_rsa) { KeyId = "fake-key-1" };
    }

    public RsaSecurityKey Key { get; }

    private sealed record PendingCode(string Email, bool? EmailVerified, string Nonce, string CodeChallenge, string RedirectUri);

    /// <summary>Simulates the user signing in at the provider: the code will redeem to an ID token for this email.</summary>
    public string IssueCode(string authorizationUrl, string email, bool? emailVerified = true)
    {
        var query = ParseQuery(new Uri(authorizationUrl).Query);
        if (query["client_id"] != ClientId || query["response_type"] != "code" || query["code_challenge_method"] != "S256")
        {
            throw new InvalidOperationException("Unexpected authorization request: " + authorizationUrl);
        }

        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _codes[code] = new PendingCode(email, emailVerified, query["nonce"], query["code_challenge"], query["redirect_uri"]);
        return code;
    }

    public static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : string.Empty);

    internal async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.RequestUri.GetLeftPart(UriPartial.Authority) != Issuer)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return path switch
        {
            "/.well-known/openid-configuration" => Json(new Dictionary<string, object>
            {
                ["issuer"] = Issuer,
                ["authorization_endpoint"] = Issuer + "/authorize",
                ["token_endpoint"] = Issuer + "/token",
                ["jwks_uri"] = Issuer + "/jwks",
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            }),
            "/jwks" => Jwks(),
            "/token" when request.Method == HttpMethod.Post => await TokenAsync(request, cancellationToken),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private HttpResponseMessage Jwks()
    {
        var p = _rsa.ExportParameters(false);
        return Json(new Dictionary<string, object>
        {
            ["keys"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["kty"] = "RSA", ["use"] = "sig", ["alg"] = "RS256", ["kid"] = Key.KeyId,
                    ["n"] = Base64UrlEncoder.Encode(p.Modulus), ["e"] = Base64UrlEncoder.Encode(p.Exponent),
                },
            },
        });
    }

    private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (form.GetValueOrDefault("client_secret") != ClientSecret || form.GetValueOrDefault("client_id") != ClientId ||
            form.GetValueOrDefault("grant_type") != "authorization_code" ||
            !_codes.TryRemove(form.GetValueOrDefault("code") ?? string.Empty, out var pending) ||
            pending.RedirectUri != form.GetValueOrDefault("redirect_uri"))
        {
            return Json(new Dictionary<string, object> { ["error"] = "invalid_grant" }, HttpStatusCode.BadRequest);
        }

        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form.GetValueOrDefault("code_verifier") ?? string.Empty)));
        if (challenge != pending.CodeChallenge)
        {
            return Json(new Dictionary<string, object> { ["error"] = "invalid_grant", ["error_description"] = "PKCE" }, HttpStatusCode.BadRequest);
        }

        var claims = new Dictionary<string, object> { ["email"] = pending.Email, ["nonce"] = pending.Nonce };
        if (pending.EmailVerified is { } verified)
        {
            claims["email_verified"] = verified;
        }

        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            Subject = new ClaimsIdentity([new Claim("sub", "ext-" + pending.Email)]),
            Claims = claims,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        });
        return Json(new Dictionary<string, object> { ["id_token"] = idToken, ["access_token"] = "unused", ["token_type"] = "Bearer", ["expires_in"] = 300 });
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

/// <summary>Per-HttpClient handler (the factory may dispose it) delegating to the shared fake provider.</summary>
public sealed class FakeOidcHandler(FakeOidcProvider provider) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        provider.HandleAsync(request, cancellationToken);
}
