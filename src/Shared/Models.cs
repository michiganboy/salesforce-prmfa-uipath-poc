namespace Salesforce.Prmfa.Shared;

public sealed class SalesforceTokenResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string InstanceUrl { get; init; } = string.Empty;
}

public sealed class SavedWebAuthnCredential
{
    public string CredentialId { get; init; } = string.Empty;
    public string RpId { get; init; } = string.Empty;
    public string PrivateKey { get; init; } = string.Empty;
    public bool IsResidentCredential { get; init; } = true;
    public bool? BackupEligibility { get; init; }
    public bool? BackupState { get; init; }
    public string UserHandle { get; init; } = string.Empty;
    public long SignCount { get; init; }
    public string? LargeBlob { get; init; }
    public string? UserName { get; init; }
    public string? UserDisplayName { get; init; }
}

public sealed class SalesforceSettings
{
    public string LoginUrl { get; init; } = "https://test.salesforce.com";
    public string? Audience { get; init; }
    public string ClientId { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string PrivateKeyPath { get; init; } = string.Empty;
    public string CredentialPath { get; init; } = string.Empty;
    public string AuthenticatedOrigin { get; init; } = string.Empty;
    public string AuthenticatedCssSelector { get; init; } = string.Empty;
}

public sealed class BrowserSettings
{
    public string ExecutablePath { get; init; } = string.Empty;
    public string UserDataDirectory { get; init; } = string.Empty;
    public int DebugPort { get; init; } = 9223;
}

public sealed class AppSettings
{
    public SalesforceSettings Salesforce { get; init; } = new();
    public BrowserSettings Browser { get; init; } = new();
}
