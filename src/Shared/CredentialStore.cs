using System.Text.Json;

namespace Salesforce.Prmfa.Shared;

public static class CredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<SavedWebAuthnCredential> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var credential = await JsonSerializer.DeserializeAsync<SavedWebAuthnCredential>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? throw new InvalidOperationException("Credential file was empty or invalid.");
        CredentialValidation.Validate(credential);
        return credential;
    }

    public static async Task SaveAsync(
        string path,
        SavedWebAuthnCredential credential,
        CancellationToken cancellationToken = default)
    {
        CredentialValidation.Validate(credential);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
                await JsonSerializer.SerializeAsync(stream, credential, JsonOptions, cancellationToken);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
