using System.Text.Json;
using Salesforce.Prmfa.Shared;

namespace Salesforce.Prmfa.Cdp;

public static class BrowserSession
{
    public static async Task NavigateAsync(CdpClient cdp, string sessionId, string url, CancellationToken cancellationToken = default)
    {
        await cdp.SendAsync("Page.enable", new { }, sessionId, cancellationToken);
        var result = await cdp.SendAsync("Page.navigate", new { url }, sessionId, cancellationToken);
        if (result.TryGetProperty("errorText", out _) ||
            (result.TryGetProperty("isDownload", out var download) && download.GetBoolean()))
            throw new IOException("Browser navigation failed or started a download.");
    }

    public static bool IsAuthenticatedLocation(string? url, string origin)
    {
        var expected = Frontdoor.HttpsOrigin(origin);
        return Uri.TryCreate(url, UriKind.Absolute, out var actual) && actual.Scheme == "https" &&
            actual.GetLeftPart(UriPartial.Authority) == expected.GetLeftPart(UriPartial.Authority) &&
            (actual.AbsolutePath.StartsWith("/lightning/", StringComparison.Ordinal) ||
             actual.AbsolutePath.StartsWith("/setup/", StringComparison.Ordinal));
    }

    public static async Task WaitForAuthenticatedPageAsync(CdpClient cdp, string sessionId,
        string origin, string cssSelector, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _ = Frontdoor.HttpsOrigin(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(cssSelector);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var selector = JsonSerializer.Serialize(cssSelector);
        try
        {
            while (true)
            {
                try
                {
                    var response = await cdp.SendAsync("Runtime.evaluate", new
                    {
                        expression = $"({{url:location.href,ready:document.readyState === 'complete' && !!document.querySelector({selector})}})",
                        returnByValue = true
                    }, sessionId, deadline.Token);
                    if (response.TryGetProperty("exceptionDetails", out _))
                        throw new InvalidOperationException("Readiness script failed; check the configured CSS selector.");
                    if (response.GetProperty("result").TryGetProperty("value", out var value) &&
                        value.GetProperty("ready").GetBoolean() &&
                        IsAuthenticatedLocation(value.GetProperty("url").GetString(), origin)) return;
                }
                catch (CdpCommandException ex) when (ex.Code == -32000)
                {
                    // Navigation may destroy the execution context; retry within the overall deadline.
                }
                await Task.Delay(250, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Authenticated Salesforce page marker was not observed before timeout.");
        }
    }
}
