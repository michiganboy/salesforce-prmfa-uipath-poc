using System.Diagnostics;
using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;
using Salesforce.Prmfa.Shared;

namespace Salesforce.Prmfa.Option2;

public sealed class ChromeLauncher(HttpClient httpClient)
{
    public Process Launch(BrowserSettings settings)
    {
        if (!File.Exists(settings.ExecutablePath)) throw new FileNotFoundException("Browser executable does not exist.");
        if (!Path.IsPathFullyQualified(settings.UserDataDirectory)) throw new ArgumentException("Use an absolute dedicated browser profile path.");
        if (settings.DebugPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(settings.DebugPort));
        // Refuse an occupied port instead of silently controlling an unrelated browser.
        var probe = new TcpListener(IPAddress.Loopback, settings.DebugPort);
        try { probe.Start(); }
        finally { probe.Stop(); }
        Directory.CreateDirectory(settings.UserDataDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = settings.ExecutablePath,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add($"--remote-debugging-port={settings.DebugPort}");
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        startInfo.ArgumentList.Add($"--user-data-dir={settings.UserDataDirectory}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("about:blank");

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Unable to start Chromium browser.");
    }

    public async Task<Uri> WaitForBrowserWebSocketAsync(
        int debugPort,
        TimeSpan timeout,
        CancellationToken cancellationToken = default, Process? browser = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var endpoint = $"http://127.0.0.1:{debugPort}/json/version";

        while (!deadline.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (browser?.HasExited == true) throw new InvalidOperationException("Browser exited during startup. Close any browser using this profile.");

            try
            {
                var version = await httpClient.GetFromJsonAsync<BrowserVersion>(endpoint, deadline.Token);
                if (!string.IsNullOrWhiteSpace(version?.WebSocketDebuggerUrl))
                {
                    var uri = new Uri(version.WebSocketDebuggerUrl);
                    if (uri.Scheme != "ws" || !uri.IsLoopback || uri.Port != debugPort)
                        throw new InvalidOperationException("Unexpected debugging endpoint.");
                    return uri;
                }
            }
            catch (HttpRequestException)
            {
                // Browser may still be starting.
            }

            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
            try { await Task.Delay(250, deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
        }

        throw new TimeoutException("Chromium DevTools endpoint did not become available.");
    }

    private sealed class BrowserVersion
    {
        public string WebSocketDebuggerUrl { get; set; } = string.Empty;
    }
}
