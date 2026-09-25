using Salesforce.Prmfa.Cdp;
using Salesforce.Prmfa.Shared;

namespace Salesforce.Prmfa.Option2;

public sealed class SalesforceAuthHelper(
    HttpClient httpClient,
    ChromeLauncher chromeLauncher)
{
    public async Task AuthenticateAsync(
        AppSettings settings,
        string returnUrl,
        CancellationToken cancellationToken = default)
    {
        _ = Frontdoor.HttpsOrigin(settings.Salesforce.AuthenticatedOrigin);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Salesforce.AuthenticatedCssSelector);
        using var browser = chromeLauncher.Launch(settings.Browser);
        var browserEndpoint = await chromeLauncher.WaitForBrowserWebSocketAsync(
            settings.Browser.DebugPort,
            TimeSpan.FromSeconds(20),
            cancellationToken, browser);

        await using var cdp = new CdpClient();
        await cdp.ConnectAsync(browserEndpoint, cancellationToken);

        var sessionId = await CreateAttachedPageAsync(cdp, cancellationToken);
        var credential = await CredentialStore.LoadAsync(
            settings.Salesforce.CredentialPath,
            cancellationToken);

        var webAuthn = new WebAuthnClient(cdp, sessionId);
        await webAuthn.EnableAsync(cancellationToken);
        var authenticatorId = await webAuthn.CreateVirtualAuthenticatorAsync(cancellationToken);
        await webAuthn.SeedCredentialAsync(authenticatorId, credential, cancellationToken);

        var jwtClient = new JwtBearerClient(httpClient);
        var token = await jwtClient.AuthenticateAsync(settings.Salesforce, cancellationToken);

        var frontdoorUrl = Frontdoor.BuildUrl(token, returnUrl);
        await BrowserSession.NavigateAsync(cdp, sessionId, frontdoorUrl, cancellationToken);

        Exception? authenticationError = null;
        try
        {
            await BrowserSession.WaitForAuthenticatedPageAsync(cdp, sessionId,
                settings.Salesforce.AuthenticatedOrigin, settings.Salesforce.AuthenticatedCssSelector,
                TimeSpan.FromSeconds(60), cancellationToken);
        }
        catch (Exception ex) { authenticationError = ex; throw; }
        finally
        {
            // Assertions can increment the counter even when later navigation fails.
            using var saveDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var updated = await webAuthn.GetFirstCredentialAsync(authenticatorId, saveDeadline.Token);
                await CredentialStore.SaveAsync(settings.Salesforce.CredentialPath, updated, saveDeadline.Token);
            }
            catch when (authenticationError is not null)
            {
                Console.Error.WriteLine("Credential counter could not be saved. Reconcile it before retrying; preserving the original authentication error.");
            }
        }

        Console.WriteLine("Authenticated Salesforce Lightning session established.");
        Console.WriteLine("The browser will remain open for UiPath to attach.");
    }

    public async Task BootstrapAsync(
        AppSettings settings,
        string returnUrl,
        CancellationToken cancellationToken = default)
    {
        using var browser = chromeLauncher.Launch(settings.Browser);
        var browserEndpoint = await chromeLauncher.WaitForBrowserWebSocketAsync(
            settings.Browser.DebugPort,
            TimeSpan.FromSeconds(20),
            cancellationToken, browser);

        await using var cdp = new CdpClient();
        await cdp.ConnectAsync(browserEndpoint, cancellationToken);

        var sessionId = await CreateAttachedPageAsync(cdp, cancellationToken);
        var webAuthn = new WebAuthnClient(cdp, sessionId);
        await webAuthn.EnableAsync(cancellationToken);
        var authenticatorId = await webAuthn.CreateVirtualAuthenticatorAsync(cancellationToken);

        var jwtClient = new JwtBearerClient(httpClient);
        var token = await jwtClient.AuthenticateAsync(settings.Salesforce, cancellationToken);

        var frontdoorUrl = Frontdoor.BuildUrl(token, returnUrl);
        await BrowserSession.NavigateAsync(cdp, sessionId, frontdoorUrl, cancellationToken);

        Console.WriteLine("Bootstrap browser opened.");
        Console.WriteLine("Complete the Salesforce 'Create Passkey' action in the browser.");
        Console.WriteLine("Press ENTER here after Salesforce finishes registering the passkey.");
        if (await Console.In.ReadLineAsync(cancellationToken) is null)
            throw new InvalidOperationException("Bootstrap requires an interactive confirmation; stdin reached EOF.");

        var credential = await webAuthn.GetFirstCredentialAsync(authenticatorId, cancellationToken);
        await CredentialStore.SaveAsync(settings.Salesforce.CredentialPath, credential, cancellationToken);

        Console.WriteLine($"Credential saved to: {settings.Salesforce.CredentialPath}");
        Console.WriteLine("Move this credential into an enterprise secret store before production use.");
    }

    private static async Task<string> CreateAttachedPageAsync(
        CdpClient cdp,
        CancellationToken cancellationToken)
    {
        var target = await cdp.SendAsync(
            "Target.createTarget",
            new { url = "about:blank" },
            cancellationToken: cancellationToken);

        var targetId = target.GetProperty("targetId").GetString()
                       ?? throw new InvalidOperationException("CDP did not return targetId.");

        var attached = await cdp.SendAsync(
            "Target.attachToTarget",
            new { targetId, flatten = true },
            cancellationToken: cancellationToken);

        return attached.GetProperty("sessionId").GetString()
               ?? throw new InvalidOperationException("CDP did not return sessionId.");
    }

}
