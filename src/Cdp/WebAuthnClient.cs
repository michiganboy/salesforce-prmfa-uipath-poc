using Salesforce.Prmfa.Shared;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Salesforce.Prmfa.Cdp;

public sealed class WebAuthnClient(CdpClient cdp, string sessionId)
{
    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        await cdp.SendAsync("WebAuthn.enable", new { }, sessionId, cancellationToken);
    }

    public async Task<string> CreateVirtualAuthenticatorAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await cdp.SendAsync(
            "WebAuthn.addVirtualAuthenticator",
            new
            {
                options = new
                {
                    protocol = "ctap2",
                    transport = "internal",
                    hasResidentKey = true,
                    hasUserVerification = true,
                    isUserVerified = true,
                    automaticPresenceSimulation = true
                }
            },
            sessionId,
            cancellationToken);

        return result.GetProperty("authenticatorId").GetString()
               ?? throw new InvalidOperationException("CDP did not return authenticatorId.");
    }

    public async Task SeedCredentialAsync(
        string authenticatorId,
        SavedWebAuthnCredential credential,
        CancellationToken cancellationToken = default)
    {
        CredentialValidation.Validate(credential);
        await cdp.SendAsync(
            "WebAuthn.addCredential",
            new
            {
                authenticatorId,
                credential = JsonSerializer.SerializeToElement(credential, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                })
            },
            sessionId,
            cancellationToken);
    }

    public async Task<SavedWebAuthnCredential> GetFirstCredentialAsync(
        string authenticatorId,
        CancellationToken cancellationToken = default)
    {
        var result = await cdp.SendAsync(
            "WebAuthn.getCredentials",
            new { authenticatorId },
            sessionId,
            cancellationToken);

        var credentials = result.GetProperty("credentials");
        if (credentials.GetArrayLength() != 1)
            throw new InvalidOperationException("Expected exactly one credential; refusing to select an arbitrary account.");

        var credential = credentials[0].Deserialize<SavedWebAuthnCredential>(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }) ?? throw new InvalidOperationException("Invalid CDP credential.");
        CredentialValidation.Validate(credential);
        return credential;
    }
}
