# Option 2 — local authentication helper

## Run on the robot machine

1. Install .NET 8 SDK for development and a compatible Chrome/Edge browser. Build/test from the repository root as shown in the README.
2. Copy `examples/appsettings.example.json` to `appsettings.local.json`. Supply app/user/RSA key paths, credential output path, the browser executable, an **absolute dedicated non-default** profile path and an unused loopback debug port (default 9223). Supply the verified final Lightning HTTPS origin and authenticated-only CSS marker.
3. Close browsers using this dedicated profile before each helper launch. Do not reuse the ordinary user's profile. Only one helper/robot run may use a profile or credential at a time. Provision the UiPath browser extension in this profile if the chosen attach mode needs it. Do not launch another browser while this helper starts.
4. Bootstrap once in an attended terminal:

```powershell
dotnet run --project src/Option2.AuthHelper -- bootstrap --config appsettings.local.json
```

The helper launches Chrome, creates and attaches a new blank page, installs an empty virtual authenticator, obtains a JWT bearer access token and navigates through frontdoor. Use the approved initial-login/enrollment route in **that page**, trigger Create Passkey and verify Salesforce accepted it. Press Enter only after registration completes. The helper reads exactly one credential and saves it. EOF on stdin is an error. If initial login already demands an enrolled factor, resolve this with the administrator; the helper does not bypass enrollment policy.

5. Close that browser before the next launch, then authenticate:

```powershell
dotnet run --project src/Option2.AuthHelper -- authenticate --config appsettings.local.json --return-url /lightning/page/home
```

The helper attaches its new page, seeds the credential, obtains a token, navigates and waits up to 60 seconds for the configured HTTPS origin, a `/lightning/` or `/setup/` path, completed document load and configured DOM marker. These checks are evaluated in the same target. Individual CDP commands also have a 30-second deadline. It saves the updated credential counter and exits, leaving the browser open. No stdout message contains the bearer token/frontdoor URL.

6. For robot deployment, publish on a machine that can restore the Windows runtime packs:

```powershell
dotnet publish src/Option2.AuthHelper/Option2.AuthHelper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Invoke the resulting `src/Option2.AuthHelper/bin/Release/net8.0/win-x64/publish/SalesforceAuthHelper.exe` from UiPath:

```powershell
SalesforceAuthHelper.exe authenticate --config C:\RPA\SalesforcePrmfa\appsettings.local.json --return-url /lightning/page/home
```

Wait for exit and require exit code 0. Then attach to the existing browser/page through the installed, validated extension/native UI automation mode. Confirm the intended Salesforce account and application before business actions. Test the published executable and browser attachment on the target Windows robot before running the full workflow.

## Ownership and lifecycle

Chrome launches with `--remote-debugging-address=127.0.0.1`, the configured fixed debugging port and `--user-data-dir`. The launcher rejects an occupied port, observes early process exit and uses a cancellable 20-second discovery deadline. The port check is not an atomic reservation/ownership proof; other local processes are trusted in this POC. Configure profile isolation and loopback access controls, and manage profile cleanup on the robot machine.

Chrome remains open on success **and on failure** for inspection. Disposing its .NET `Process` wrapper does not kill it. Close the owned browser before retrying, or the port/profile checks should fail. The helper never closes unrelated browsers or kills a process tree. The debugging port remains available while Chrome runs; close the browser when automation is complete.

The CDP connection and virtual authenticator remain alive through the readiness check and counter capture. Helper exit ends that connection, so subsequent WebAuthn step-up requests are not handled. If business operations require later PRMFA challenges, extend the helper lifetime with an explicit handoff protocol before deployment; this POC does not implement that protocol.

[UiPath's Chromium Automation documentation](https://docs.uipath.com/activities/other/latest/ui-automation/about-chromium-automation) says it starts isolated sessions and cannot attach to an already-open desktop browser. **Do not use Chromium Automation to relaunch Option 2's browser.** Extension/native attachment, profile extension provisioning, robot desktop/session identity and page selection must be validated for your UiPath version.

[Chrome requires a non-default profile for remote debugging from version 136](https://developer.chrome.com/blog/remote-debugging-port).
