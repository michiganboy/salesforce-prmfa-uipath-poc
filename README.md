# Salesforce PRMFA / UiPath proof of concept

Two architectures are preserved:

1. **Option 1:** UiPath owns Chromium. A custom integration supplies its browser WebSocket endpoint and the exact page **TargetId**; this library attaches its own CDP session and seeds a registered virtual WebAuthn credential. UiPath navigates and continues in that same page.
2. **Option 2:** A local .NET helper launches Chrome, owns CDP and completes the authentication sequence. It leaves Chrome running for a separately validated UiPath extension/native UI attach workflow.

## Build and test

Install a .NET 8 SDK. From the repository root:

```sh
dotnet restore Salesforce.Prmfa.UiPath.sln
dotnet build Salesforce.Prmfa.UiPath.sln -c Release --no-restore
dotnet test Salesforce.Prmfa.UiPath.sln -c Release --no-build
```

All projects target `net8.0` with nullable reference types and implicit usings. C# 12 comes from the .NET 8 SDK. References are acyclic: Shared → no project dependencies; Cdp → Shared; both options → Cdp + Shared; Engineering.Tests → both options. The helper assembly is `SalesforceAuthHelper`. Namespaces intentionally use `Salesforce.Prmfa.*`, independent of project folder/assembly names.

## Before running either option

- Have an administrator configure JWT bearer authorization, the automation user, app client ID and the certificate matching the RSA PEM private key. The user must be entitled to browser UI access. Confirm the token's granted scopes/session type support UI bridging; an API token is not automatically a browser session.
- Use `https://test.salesforce.com` for sandbox or `https://login.salesforce.com` for production unless the org requires a different token endpoint. `Audience` optionally separates the JWT audience from `LoginUrl`; confirm the allowed audience with the org. This POC accepts HTTPS origins only, not Experience Cloud site paths.
- Establish an approved initial login/enrollment route. JWT/frontdoor may itself demand an already enrolled factor: bootstrap cannot solve that circular dependency. Complete any initial verification by an approved route in the SAME configured page.
- Register the passkey using this virtual authenticator. A hardware/platform passkey is generally not exportable; this repository cannot extract one from Windows Hello, a security key or a password manager.
- Configure and verify the exact RP ID, account, authenticated browser origin and an authenticated-only DOM selector. No Salesforce selector is universally assumed. Never use `body` as the readiness marker.
- Use one run per saved credential/profile at a time. The credential includes an exportable private key and mutable signature counter. Atomic replacement is implemented; distributed locking, encryption and Windows ACL management are not.

Copy `examples/appsettings.example.json` to `appsettings.local.json` and replace every placeholder. Paths should be absolute. `AuthenticatedOrigin` is the final Lightning origin, which may differ from `instance_url`. `AuthenticatedCssSelector` is a verified CSS selector for an element present only after successful login. Protect this file, the RSA key, exported passkey and profile with the robot account's ACLs.

## Run

- [Option 1: exact integration and lifetime steps](docs/option-1-uipath-owned-browser.md)
- [Option 2: bootstrap, authenticate, publish and attach](docs/option-2-local-auth-helper.md)

The helper's exit code 0 means its configured readiness check and credential save completed. UiPath must still verify the intended logged-in user and page before business operations. A timeout, cancellation, navigation error, token failure or save failure must prevent the workflow from continuing.

## Operational limits

The CDP transport has one receive loop, serialized writes, independent command correlation, a 30-second command deadline and a 16 MiB incoming-message limit. It reassembles fragmented messages before JSON parsing, ignores ordinary events, fails commands on disconnect, and fails affected pending commands on target detach. It deliberately does not reconnect: reconnecting loses session/authenticator state. Disposal aborts the CDP connection without closing Chrome. Sending a command that times out or is cancelled does not undo browser side effects; do not blindly retry mutations.

WebAuthn uses CTAP2/internal, resident-key support, simulated presence and simulated user verification. Keep the owning CDP connection and target alive through the challenge. New tabs/popups/target replacement need separate integration work. After successful use, export and persist the updated signature counter before disposal. Reconcile state after crashes; do not run cloned credentials concurrently.

The requested `frontdoor.jsp?sid=...&retURL=...` route is retained and encoded. It exposes a bearer token in the browser URL/history; do not log it. Salesforce recommends POST or the Single Access UI Bridge API for new integrations. That migration is deliberately outside this POC's preserved architecture. See [Salesforce frontdoor guidance](https://help.salesforce.com/s/articleView?id=security_frontdoorjsp.htm&language=en_US&type=5) and [Single Access UI Bridge](https://help.salesforce.com/s/articleView?id=sf.frontdoor_singleaccess.htm&language=en_US&type=5).
