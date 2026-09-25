using System.Text.Json;
using Salesforce.Prmfa.Option2;
using Salesforce.Prmfa.Shared;

static string? GetOption(string[] args, string name)
{
    var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("SalesforceAuthHelper");
    Console.WriteLine();
    Console.WriteLine("Authenticate:");
    Console.WriteLine("  SalesforceAuthHelper authenticate --config <path> [--return-url /lightning/page/home]");
    Console.WriteLine();
    Console.WriteLine("Bootstrap passkey:");
    Console.WriteLine("  SalesforceAuthHelper bootstrap --config <path> [--return-url /lightning/page/home]");
    return;
}

var command = args[0].ToLowerInvariant();
var configPath = GetOption(args, "--config")
                 ?? throw new ArgumentException("--config is required.");
var returnUrl = GetOption(args, "--return-url") ?? "/lightning/page/home";

var configJson = await File.ReadAllTextAsync(configPath);
var settings = JsonSerializer.Deserialize<AppSettings>(
    configJson,
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("Unable to parse configuration file.");

using var httpClient = new HttpClient();
var launcher = new ChromeLauncher(httpClient);
var helper = new SalesforceAuthHelper(httpClient, launcher);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

switch (command)
{
    case "authenticate":
        await helper.AuthenticateAsync(settings, returnUrl, cts.Token);
        break;

    case "bootstrap":
        await helper.BootstrapAsync(settings, returnUrl, cts.Token);
        break;

    default:
        throw new ArgumentException($"Unknown command '{command}'. Use --help.");
}
