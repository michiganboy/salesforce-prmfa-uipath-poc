using System.Security.Cryptography;

namespace Salesforce.Prmfa.Shared;

public static class CredentialValidation
{
    public static void Validate(SavedWebAuthnCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.RpId) || credential.RpId.Contains('/') || credential.RpId.Contains(':'))
            throw new ArgumentException("Credential rpId must be a hostname, not a URL.");
        if (credential.SignCount < -1) throw new ArgumentException("signCount must be at least -1.");
        if (Convert.FromBase64String(credential.CredentialId).Length == 0)
            throw new ArgumentException("credentialId must contain base64 bytes.");
        var userHandle = Convert.FromBase64String(credential.UserHandle);
        if (userHandle.Length > 64 || (credential.IsResidentCredential && userHandle.Length == 0))
            throw new ArgumentException("Resident credentials require a 1–64 byte userHandle.");
        var key = Convert.FromBase64String(credential.PrivateKey);
        // Chromium's documented format is base64 PKCS#8 EC private key, not PEM/JWK/public key.
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(key, out var read);
        if (ec.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new ArgumentException("CDP requires an ECDSA P-256 key.");
        if (read != key.Length) throw new ArgumentException("Trailing bytes in PKCS#8 key.");
        if (credential.BackupState == true && credential.BackupEligibility == false)
            throw new ArgumentException("A backed-up credential must be backup eligible.");
    }
}
