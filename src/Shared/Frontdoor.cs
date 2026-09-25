namespace Salesforce.Prmfa.Shared;

public static class Frontdoor
{
    public static Uri HttpsOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "")
            throw new ArgumentException("Expected an HTTPS origin without path, credentials, query or fragment.");
        return uri;
    }

    public static string BuildUrl(SalesforceTokenResponse token, string returnUrl)
    {
        var origin = HttpsOrigin(token.InstanceUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(token.AccessToken);
        if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') ||
            returnUrl.StartsWith("//") || returnUrl.Contains('\\') || returnUrl.Any(char.IsControl))
            throw new ArgumentException("returnUrl must be a local absolute path.");
        return $"{origin.GetLeftPart(UriPartial.Authority)}/secur/frontdoor.jsp?sid={Uri.EscapeDataString(token.AccessToken)}&retURL={Uri.EscapeDataString(returnUrl)}";
    }
}
