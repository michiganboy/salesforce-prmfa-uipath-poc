using Salesforce.Prmfa.Cdp;
using Salesforce.Prmfa.Shared;

namespace Salesforce.Prmfa.Option1;

/// <summary>
/// One-time bootstrap support for Option 1. BeginAsync installs an empty virtual
/// authenticator in the UiPath-owned Chromium target. UiPath then navigates the
/// same target through JWT/frontdoor.jsp and triggers Salesforce's Create Passkey
/// action. CaptureCredentialAsync retrieves the generated credential afterward.
/// </summary>
public sealed class BootstrapSalesforceWebAuthn(ICdpSessionProvider sessionProvider)
{
    public async Task<Option1BootstrapSession> BeginAsync(
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

            return new Option1BootstrapSession(cdp, webAuthn, authenticatorId);
        }
        catch
        {
            await cdp.DisposeAsync();
            throw;
        }
    }
}

public sealed class Option1BootstrapSession : IAsyncDisposable
{
    private readonly CdpClient _cdp;
    private readonly WebAuthnClient _webAuthn;

    internal Option1BootstrapSession(
        CdpClient cdp,
        WebAuthnClient webAuthn,
        string authenticatorId)
    {
        _cdp = cdp;
        _webAuthn = webAuthn;
        AuthenticatorId = authenticatorId;
    }

    public string AuthenticatorId { get; }

    /// <summary>
    /// Call only after UiPath has triggered Salesforce's Create Passkey action
    /// and Salesforce/Chromium has created the credential.
    /// </summary>
    public Task<SavedWebAuthnCredential> CaptureCredentialAsync(
        CancellationToken cancellationToken = default)
        => _webAuthn.GetFirstCredentialAsync(AuthenticatorId, cancellationToken);

    public ValueTask DisposeAsync() => _cdp.DisposeAsync();
}
