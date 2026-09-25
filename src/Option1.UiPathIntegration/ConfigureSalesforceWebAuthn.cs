using Salesforce.Prmfa.Cdp;
using Salesforce.Prmfa.Shared;

namespace Salesforce.Prmfa.Option1;

/// <summary>
/// UiPath integration boundary. A supported adapter must supply the browser
/// WebSocket endpoint and exact page TargetId. No UiPath API is implemented here.
/// </summary>
public interface ICdpSessionProvider
{
    Task<CdpSessionHandle> GetActiveChromiumSessionAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes the page to attach on a NEW connection. SessionId is retained only
/// for source compatibility and is rejected: sessions cannot cross connections.
/// </summary>
public sealed record CdpSessionHandle(
    Uri BrowserWebSocketEndpoint,
    string? SessionId = null,
    string? TargetId = null);

/// <summary>
/// Installs the persisted Salesforce WebAuthn credential into the exact
/// Chromium target UiPath will navigate through frontdoor.jsp.
///
/// IMPORTANT: Keep the returned Option1WebAuthnSession alive until Salesforce
/// has completed the PRMFA challenge and the browser has reached the authenticated
/// Lightning/Setup page. Disposing it earlier closes the CDP connection used to
/// host the virtual authenticator.
/// </summary>
public sealed class ConfigureSalesforceWebAuthn(ICdpSessionProvider sessionProvider)
{
    public async Task<Option1WebAuthnSession> BeginAsync(
        SavedWebAuthnCredential credential,
        CancellationToken cancellationToken = default)
    {
        var handle = await sessionProvider.GetActiveChromiumSessionAsync(cancellationToken);

        var cdp = new CdpClient();
        try
        {
            await cdp.ConnectAsync(handle.BrowserWebSocketEndpoint, cancellationToken);
            var sessionId = await CdpSessionResolver.ResolveSessionIdAsync(
                cdp,
                handle,
                cancellationToken);

            var webAuthn = new WebAuthnClient(cdp, sessionId);
            await webAuthn.EnableAsync(cancellationToken);
            var authenticatorId = await webAuthn.CreateVirtualAuthenticatorAsync(cancellationToken);
            await webAuthn.SeedCredentialAsync(authenticatorId, credential, cancellationToken);

            return new Option1WebAuthnSession(cdp, webAuthn, sessionId, authenticatorId);
        }
        catch
        {
            await cdp.DisposeAsync();
            throw;
        }
    }
}

/// <summary>
/// Keeps the CDP/WebAuthn session alive while UiPath performs the JWT/frontdoor
/// navigation. Dispose only after PRMFA is complete.
/// </summary>
public sealed class Option1WebAuthnSession : IAsyncDisposable
{
    private readonly CdpClient _cdp;
    private readonly WebAuthnClient _webAuthn;

    internal Option1WebAuthnSession(
        CdpClient cdp,
        WebAuthnClient webAuthn,
        string sessionId,
        string authenticatorId)
    {
        _cdp = cdp;
        _webAuthn = webAuthn;
        SessionId = sessionId;
        AuthenticatorId = authenticatorId;
    }

    public string SessionId { get; }
    public string AuthenticatorId { get; }

    public Task<SavedWebAuthnCredential> CaptureCredentialAsync(CancellationToken cancellationToken = default)
        => _webAuthn.GetFirstCredentialAsync(AuthenticatorId, cancellationToken);

    public ValueTask DisposeAsync() => _cdp.DisposeAsync();
}

internal static class CdpSessionResolver
{
    public static async Task<string> ResolveSessionIdAsync(
        CdpClient cdp,
        CdpSessionHandle handle,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(handle.SessionId))
            throw new InvalidOperationException("SessionId belongs to its originating WebSocket. Supply TargetId so this connection can attach.");

        if (string.IsNullOrWhiteSpace(handle.TargetId))
        {
            throw new InvalidOperationException(
                "The UiPath CDP adapter must provide TargetId.");
        }

        var attached = await cdp.SendAsync(
            "Target.attachToTarget",
            new
            {
                targetId = handle.TargetId,
                flatten = true
            },
            cancellationToken: cancellationToken);

        return attached.GetProperty("sessionId").GetString()
               ?? throw new InvalidOperationException(
                   "Target.attachToTarget did not return sessionId.");
    }
}
