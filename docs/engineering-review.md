# Engineering review — 2026-09-25

## Result and scope

Both architectures are preserved. Runtime defects were fixed directly; this is not only a static review. All source, project/solution, configuration and documentation files were inspected. The supplied directory has no Git metadata, so no branch, commit or PR was created.

The review used a task-local .NET 8.0.425 SDK on macOS ARM64 because `dotnet` was initially absent from PATH. The untouched baseline builds without compiler warnings/errors. The important defects were runtime/lifecycle and integration claims, not compiler diagnostics. Final build/test evidence is recorded below after verification.

Tests use an actual loopback Kestrel WebSocket server and ClientWebSocket. They exercise framing and asynchronous responses rather than only mocking `SendAsync`. Salesforce, real Chromium WebAuthn ceremonies and Windows UiPath integration were **not** executed. No org credentials, robot environment or supported UiPath adapter were supplied. Passing local tests does not prove PRMFA enforcement acceptance.

## Findings and changes

| Priority | Finding | Resolution / remaining boundary |
|---|---|---|
| High | Concurrent `ClientWebSocket.SendAsync` calls were unsupported and could corrupt/fail traffic. | Serialize writes, preserve independent pending requests and one reader. |
| High | Close frames, cancelled receive loop and disposal could leave pending tasks unresolved. `CloseAsync` could wait indefinitely. | Terminal state atomically fails pending commands; abort socket on disposal/disconnect, await receive loop; disposal is idempotent. No automatic reconnect. |
| High | Failed sends leaked pending registrations; no command deadline existed. | Serialization before registration, unconditional cleanup, default 30-second deadline and cancellation. A failed/cancelled write terminates the connection because framing may be incomplete. |
| High | Option 1 reused UiPath's session ID on a different WebSocket. | Require page TargetId; attach with flatten=true on this connection. Reject foreign SessionId, correct misleading docs. |
| High | Readiness accepted `/lightning/` anywhere in `location.href`, including frontdoor/login return parameters or another host. | Require exact configured HTTPS origin, authenticated path, completed load and a configured authenticated-only DOM marker. This still needs org-specific validation and user identity confirmation. |
| High | Credential counters were never exported after authentication. | Add Option 1 capture API; Option 2 exports before disposal, including failed readiness after possible assertions. Atomic file replacement. Crash/disconnect recovery and concurrency remain POC limits. |
| Medium | Navigation ignored `Page.navigate.errorText`; successful CDP response does not imply successful navigation. | Reject errorText/download; bounded readiness polling tolerates transient execution-context loss. |
| Medium | Option 1 connect failure occurred outside cleanup's try/catch. | Dispose owned socket on setup/connect failure in runtime and bootstrap. |
| Medium | Export always selected first credential and reconstructed resident status rather than preserving it. | Require exactly one credential, round-trip resident flag/counter/backup flags and common optional fields. Validate base64, RP ID and P-256 PKCS#8 key. |
| Medium | `int` counter could not represent the full unsigned WebAuthn counter range. | Store `long`, including CDP's -1 no-counter sentinel; preserve updated values. |
| Medium | Credential save could fail on bare filenames and truncate a valid file on interrupted write. | Normalize full path, write a unique temporary file and replace atomically. Unix temp files are owner-only. Windows ACLs remain external. |
| Medium | Fixed debugging port could attach to an unrelated running Chrome. Discovery timeout did not bound a stalled HTTP request. | Preflight port availability, verify loopback endpoint/port, observe launched process exit, cancellation deadline across requests. TOCTOU/profile ownership is not fully solved. |
| Medium | Bootstrap `ReadLine` ignored cancellation and treated EOF as confirmation. | Async cancellable read; EOF fails. Operator must confirm server-side registration, not just local creation. |
| Medium | OAuth errors exposed arbitrary response bodies; CDP errors can echo sensitive navigation URLs. | Expose HTTP status / CDP error code without arbitrary server error bodies. |
| Low | JWT endpoint and audience were inseparable; missing configuration failed late. | Optional Audience setting, HTTPS-origin validation and required claim validation. RS256 remains unchanged. Default lifetime reduced to 180 seconds as a conservative POC choice; not a claim that 300 seconds was protocol-invalid. |
| Low | Frontdoor encoding was already correct but duplicated/private and unvalidated. | Shared Frontdoor helper, HTTPS origin and local return-path validation; test reserved characters. |

The original receive loop already assembled fragments and skipped events. Those were not invented missing features: tests now cover large fragmented UTF-8 payloads with events between reversed responses. Added protections include a message-size limit, text-only framing, session correlation, target-detach handling and terminal-state rejection of future commands.

## Protocol verification

Verified against the [official Chromium CDP schema](https://github.com/ChromeDevTools/devtools-protocol/blob/master/json/browser_protocol.json). Pin/test an actual Chromium version in deployment because WebAuthn is experimental and tip-of-tree can differ from an installed release.

| Command | Routing and payload |
|---|---|
| `Target.attachToTarget` | Browser connection: `targetId` plus `flatten:true`; use returned sessionId on that same socket. |
| `WebAuthn.enable` | Attached page session, empty params. Optional enableUI defaults false. |
| `WebAuthn.addVirtualAuthenticator` | Page session, options `protocol:ctap2`, `transport:internal`, resident-key support, UV support, verified=true and automatic presence=true. Retain returned authenticatorId. |
| `WebAuthn.addCredential` | Same session and authenticatorId; base64 credentialId/privateKey/userHandle, RP hostname, resident status, numeric counter and optional backup metadata. Private key is ECDSA P-256 PKCS#8 DER, not PEM/public key. |
| `WebAuthn.getCredentials` | Same session and authenticatorId; validate/export one credential after server registration or assertion. |
| `Page.enable` | Attached page session, empty params. |
| `Page.navigate` | Same page session, complete encoded URL; check navigation error/download flags and then wait independently for readiness. |

Saved credential defaults retain compatibility with old resident-credential JSON. `publicKey` was removed because CDP derives it from the private key and does not accept it as a credential field; older JSON's extra property is ignored. The model covers the POC's P-256 resident credentials and common metadata, not every experimental extension (for example CMTG). It is not a universal passkey backup format. Existing credential JSON must contain the correct RP/account and PKCS#8 material.

JWT generation is base64url header/claims, RS256 PKCS#1 signing, client ID as iss, username as sub, configured audience and Unix-seconds exp. Exchange uses form POST, the JWT bearer grant and assertion. Signature and HTTP-form behavior are tested. No scope parameter is added: Salesforce assigns scopes from app policy/prior approval. RSA PEM handling is POC key storage, not HSM integration. Retry, token refresh and enrollment policy are not automated.

Frontdoor builds from validated instance_url and separately encodes sid and retURL. Encoding does not make query-string bearer tokens safe. [Salesforce's frontdoor guidance](https://help.salesforce.com/s/articleView?id=security_frontdoorjsp.htm&language=en_US&type=5) recommends sending sensitive values in POST/header. [Single Access UI Bridge](https://help.salesforce.com/s/articleView?id=sf.frontdoor_singleaccess.htm&language=en_US&type=5) is an available alternative, but changing the requested bridge architecture was out of scope.

## Code maturity

- **Reusable, hardened components:** CDP transport, JWT signer/exchange and URL/credential helpers have automated regression coverage. They are candidates for production reuse after platform/integration qualification; the repository is not production-certified.
- **POC code:** plaintext credential store, simulated authenticator, manual enrollment, fixed-port local launch, CSS-marker readiness, CLI orchestration and browser handoff. No distributed credential locking, vault, signed deployment, recovery coordinator or robot lifecycle manager.
- **UiPath-dependent placeholders:** ICdpSessionProvider's real supported endpoint/target lookup, custom activity packaging/scope and Option 2 attach workflow. Provider wrappers are real code but contain no UiPath integration.
- **Cannot work as previously described:** borrowing a foreign CDP session ID; disposing the authenticator immediately after seeding; treating a frontdoor URL containing a return path as completion; using Chromium Automation to attach to Option 2's pre-existing desktop browser. Corrected contracts explicitly reject or avoid these patterns.

## Architectural concerns and remaining UiPath unknowns

1. Identify a supported public API/extension point for the endpoint and exact target, and permission for an independent CDP client. UiPath's use of CDP alone is insufficient evidence. A native-only CDP object needs additional transport work.
2. Verify the installed UiPath project/runtime supports net8.0; supply a scope lifetime that survives intervening activities without workflow serialization or process changes. No UiPath NuGet dependency is included deliberately.
3. Verify target continuity through redirects and PRMFA. New windows, replaced targets and out-of-process challenge frames are not covered by this POC's single-page attachment. Test with actual Salesforce challenge behavior.
4. Validate extension/native attach for Option 2 in the dedicated profile and same Windows desktop/robot identity; Chromium Automation explicitly does not attach to pre-existing desktop browsers. Avoid accidental new-browser launch or selecting the wrong page.
5. Decide how long the authenticator is required. It is held through initial readiness/capture, but later step-up challenges after helper exit will fail without additional lifecycle coordination.
6. Only one owner may mutate a credential/profile. Atomic save prevents truncation, not concurrent counter rollback. A crash after an assertion may leave stale state. Deployment needs locking, recovery and secret storage.
7. CDP grants browser control; the fixed-port availability check has a race, loopback is not authentication, and the port stays open while the browser runs. No automatic process kill/profile deletion occurs on errors.

## Remaining Salesforce assumptions

- The configured app/user/certificate and audience permit JWT bearer grants; policy/prior approval yields a UI-compatible session. JWT authorization and PRMFA browser authentication are distinct.
- The org permits the intended frontdoor session bridge, user UI access and redirects. Confirm scopes (`web`/appropriate permissions) and session restrictions; Experience Cloud is not covered.
- The enforcement flow accepts a virtual authenticator with simulated presence/verification. This is an explicit experimental assumption, not a claim of Salesforce support or policy compliance.
- Enrollment can occur through an approved existing identity-verification route. The server has actually registered the exported credential for the intended account and RP ID.
- RP ID and origin remain compatible after My Domain/sandbox/domain changes. Registration and subsequent assertions operate in the configured page context.
- The configured DOM marker and origin reliably signify successful login for the intended user. A pre-existing profile cookie can satisfy readiness without proving the newly seeded passkey was used. Acceptance testing must use a logged-out/controlled session and verify an actual challenge/assertion and server acceptance.
- Signature counter/backup flag behavior is acceptable to the RP; crashes and concurrent replicas must not replay stale counter state.

## Acceptance procedure still required

On a Windows robot with an approved test org, record UiPath/browser/package versions. Bootstrap a credential and confirm server registration. Start from a logged-out session, run each option, observe a real PRMFA assertion, confirm the resulting account and authenticated UI, export the updated counter, then execute a simple read-only UiPath action in the same browser. Repeat with invalid credential/RP ID, denied OAuth grant, delayed challenge, closed tab, browser disconnect and cancellation; every failure must stop business actions. Validate a second authentication from persisted state and any later step-up challenges. Do not log tokens or private keys as evidence.

## Official references

- [Chromium protocol schema](https://github.com/ChromeDevTools/devtools-protocol/blob/master/json/browser_protocol.json) and [WebAuthn emulator](https://developer.chrome.com/docs/devtools/webauthn).
- [UiPath Chromium Automation](https://docs.uipath.com/activities/other/latest/ui-automation/about-chromium-automation): CDP implementation and isolated-session/attach limitations; no endpoint-provider API established by this review.
- [Chrome remote debugging profile requirement](https://developer.chrome.com/blog/remote-debugging-port).
- [Salesforce JWT bearer flow](https://help.salesforce.com/s/articleView?id=xcloud.remoteaccess_oauth_jwt_flow.htm&language=en_US&type=5): JWT claims/signature, audience and policy-derived scopes. Its three-minute clock-skew allowance is not a maximum assertion lifetime.
- [Salesforce frontdoor](https://help.salesforce.com/s/articleView?id=security_frontdoorjsp.htm&language=en_US&type=5) and [UI Bridge](https://help.salesforce.com/s/articleView?id=sf.frontdoor_singleaccess.htm&language=en_US&type=5).

Exact developer procedures are in [Option 1](option-1-uipath-owned-browser.md) and [Option 2](option-2-local-auth-helper.md), with common prerequisites/build commands in the README.

## Executed verification and files changed

- Untouched baseline: restore and Release build succeeded, 0 warnings, 0 errors.
- Updated solution: restore and Release build succeeded, 0 warnings, 0 errors.
- Regression suite: 27 passed, 0 failed, 0 skipped (macOS ARM64, .NET SDK 8.0.425).
- Four application/library projects remain net8.0 with their original reference graph. One xUnit test project was added to the solution with both option projects referenced.
- Windows publish/runtime, live Chrome ceremonies, Salesforce and UiPath acceptance remain unexecuted.

Files added or modified:

```text
.gitignore
README.md
STATIC_REVIEW.md
Salesforce.Prmfa.UiPath.sln
docs/engineering-review.md
docs/option-1-uipath-owned-browser.md
docs/option-2-local-auth-helper.md
examples/appsettings.example.json
src/Cdp/BrowserSession.cs
src/Cdp/CdpClient.cs
src/Cdp/WebAuthnClient.cs
src/Option1.UiPathIntegration/BootstrapSalesforceWebAuthn.cs
src/Option1.UiPathIntegration/CdpSessionProviderExamples.cs
src/Option1.UiPathIntegration/ConfigureSalesforceWebAuthn.cs
src/Option2.AuthHelper/ChromeLauncher.cs
src/Option2.AuthHelper/SalesforceAuthHelper.cs
src/Shared/CredentialStore.cs
src/Shared/CredentialValidation.cs
src/Shared/Frontdoor.cs
src/Shared/JwtBearerClient.cs
src/Shared/Models.cs
tests/Engineering.Tests/CdpTests.cs
tests/Engineering.Tests/Engineering.Tests.csproj
tests/Engineering.Tests/FlowTests.cs
tests/Engineering.Tests/SocketServer.cs
```
