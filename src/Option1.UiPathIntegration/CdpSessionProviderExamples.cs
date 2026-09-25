namespace Salesforce.Prmfa.Option1;

/// <summary>
/// Useful when a UiPath activity or supported bridge has already obtained the
/// browser WebSocket endpoint plus TargetId and simply needs to pass
/// those values into the reusable Option 1 implementation.
/// </summary>
public sealed class ConfiguredCdpSessionProvider(CdpSessionHandle handle)
    : ICdpSessionProvider
{
    public Task<CdpSessionHandle> GetActiveChromiumSessionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(handle);
    }
}

/// <summary>
/// Adapter helper for environments where the UiPath-specific lookup is easiest
/// to express as a delegate. The delegate is the only place that needs to call
/// the UiPath-supported API/extension point.
/// </summary>
public sealed class DelegateCdpSessionProvider(
    Func<CancellationToken, Task<CdpSessionHandle>> resolver)
    : ICdpSessionProvider
{
    public Task<CdpSessionHandle> GetActiveChromiumSessionAsync(
        CancellationToken cancellationToken = default)
        => resolver(cancellationToken);
}
