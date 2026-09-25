# Option 1 — UiPath-owned Chromium

## Status and contract

The library implements attach → WebAuthn.enable → addVirtualAuthenticator → addCredential and retains the connection in `Option1WebAuthnSession`. Bootstrap installs an empty authenticator and exports its credential. JWT and frontdoor URL helpers are shared. The workflow must orchestrate navigation, readiness, capture/persistence and disposal.

`ICdpSessionProvider` is an integration boundary, not a UiPath implementation. No verified UiPath endpoint/target API, activity package, XAML workflow or native transport adapter is included. `ConfiguredCdpSessionProvider` and `DelegateCdpSessionProvider` merely wrap values/lookups.

A provider must return:

```csharp
new CdpSessionHandle(browserWebSocketEndpoint, TargetId: exactUiPathPageTargetId)
```

**Do not pass UiPath's SessionId.** CDP sessions belong to the connection that attached them. This library opens a new WebSocket and must call `Target.attachToTarget` with `flatten = true` itself. The legacy `SessionId` parameter is retained for source compatibility but rejected at runtime, including when a TargetId is also supplied. A native UiPath session object would require a transport adapter that is not implemented here.

UiPath documents that Chromium Automation uses CDP. This does not document a supported public API for borrowing its connection or enabling a second client. Obtain confirmation for the installed UiPath version and test that a second CDP attachment does not disrupt the workflow. Check that its .NET runtime can load these `net8.0` assemblies; Windows Legacy/.NET Framework cannot.

## Developer procedure

1. Restore/build/test using the README commands. Reference `Option1.UiPathIntegration` from a compatible custom integration host and deploy its `Cdp` and `Shared` dependencies.
2. Implement `ICdpSessionProvider` using a **documented UiPath-supported mechanism** that returns the browser endpoint and exact current page target. Until that mechanism is identified, Option 1 cannot be run end-to-end as a UiPath workflow. Do not infer support from process discovery or internal fields.
3. Launch Chromium using UiPath and create its page before calling the library. Prevent UiPath from replacing the page/target during authentication.
4. Bootstrap once: call `BootstrapSalesforceWebAuthn.BeginAsync`, retain the returned object in the running host, establish the approved initial session in the same page and trigger Salesforce's registration action. Confirm Salesforce accepted the registration, then call `CaptureCredentialAsync` and securely save it before disposing. It must contain exactly one credential; ambiguous selections fail.
5. Normal run: load the saved credential; call `ConfigureSalesforceWebAuthn.BeginAsync`; keep the returned object alive. Obtain the OAuth token through `JwtBearerClient`, construct `Frontdoor.BuildUrl`, and have UiPath navigate the **same target** to that URL.
6. Wait for the org-specific authenticated page/user marker, with a bounded timeout. Neither issuing navigation nor a URL containing `/lightning/` establishes success. Do not continue on error.
7. While the session is alive, call `CaptureCredentialAsync` and persist its updated counter. Attempt capture even if navigation times out after an assertion, using a separate bounded cleanup token. If browser disconnect prevents capture, reconcile the stored counter before reuse.
8. Dispose only after capture and completion of any WebAuthn challenges needed by this workflow. UiPath continues in the same browser. Later step-up challenges require retaining the authenticator longer or a new explicitly managed setup.

A host orchestration outline (the named UiPath operations below are delegates you implement, **not UiPath APIs**):

```csharp
async Task AuthenticateInUiPathAsync(
    ICdpSessionProvider provider, SalesforceSettings settings, HttpClient http,
    Func<string, CancellationToken, Task> navigateSameUiPathPage,
    Func<CancellationToken, Task> waitForAuthenticatedUser,
    CancellationToken ct)
{
    var credential = await CredentialStore.LoadAsync(settings.CredentialPath, ct);
    var configure = new ConfigureSalesforceWebAuthn(provider);
    await using var session = await configure.BeginAsync(credential, ct);
    try
    {
        var token = await new JwtBearerClient(http).AuthenticateAsync(settings, ct);
        await navigateSameUiPathPage(Frontdoor.BuildUrl(token, "/lightning/page/home"), ct);
        await waitForAuthenticatedUser(ct);
    }
    finally
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var updated = await session.CaptureCredentialAsync(cleanup.Token);
        await CredentialStore.SaveAsync(settings.CredentialPath, updated, cleanup.Token);
    }
}
```

The outline uses the local POC store; replace it with a vault in deployment and preserve the primary error if cleanup also fails. The session object contains a live socket and must not be serialized across workflow persistence, jobs, processes or machines. An activity that exits a `using` scope immediately after setup destroys the authenticator too early. Implement a scope activity or a host lifetime spanning the challenge.

A supplied supported **endpoint + TargetId** completes the transport boundary, but not the workflow: enrollment, navigation, success/user detection, durable counter updates, target continuity, runtime packaging and error handling still have to be wired into UiPath.

Source: [UiPath Chromium Automation](https://docs.uipath.com/activities/other/latest/ui-automation/about-chromium-automation).
