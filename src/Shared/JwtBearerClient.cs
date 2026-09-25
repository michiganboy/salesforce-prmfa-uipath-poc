using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Salesforce.Prmfa.Shared;

public sealed class JwtBearerClient(HttpClient httpClient)
{
    public async Task<SalesforceTokenResponse> AuthenticateAsync(
        SalesforceSettings settings,
        CancellationToken cancellationToken = default)
    {
        _ = Frontdoor.HttpsOrigin(settings.LoginUrl);
        var privateKeyPem = await File.ReadAllTextAsync(settings.PrivateKeyPath, cancellationToken);
        var assertion = CreateJwtAssertion(
            settings.ClientId,
            settings.Username,
            settings.Audience ?? settings.LoginUrl,
            privateKeyPem);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion
        });

        var endpoint = settings.LoginUrl.TrimEnd('/') + "/services/oauth2/token";
        using var response = await httpClient.PostAsync(endpoint, form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Salesforce JWT token exchange failed (HTTP {(int)response.StatusCode}). Check app authorization, audience and certificate.");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var token = new SalesforceTokenResponse
        {
            AccessToken = root.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Salesforce response did not contain access_token."),
            InstanceUrl = root.GetProperty("instance_url").GetString()
                ?? throw new InvalidOperationException("Salesforce response did not contain instance_url.")
        };
        ArgumentException.ThrowIfNullOrWhiteSpace(token.AccessToken);
        _ = Frontdoor.HttpsOrigin(token.InstanceUrl);
        return token;
    }

    public static string CreateJwtAssertion(
        string clientId,
        string username,
        string loginUrl,
        string privateKeyPem,
        int lifetimeSeconds = 180)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        _ = Frontdoor.HttpsOrigin(loginUrl);
        if (lifetimeSeconds is <= 0 or > 180) throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var headerJson = JsonSerializer.Serialize(new
        {
            alg = "RS256",
            typ = "JWT"
        });

        var payloadJson = JsonSerializer.Serialize(new
        {
            iss = clientId,
            sub = username,
            aud = loginUrl.TrimEnd('/'),
            exp = now + lifetimeSeconds
        });

        var header = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{header}.{payload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
