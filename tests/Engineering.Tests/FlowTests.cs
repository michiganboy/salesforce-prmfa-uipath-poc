using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Salesforce.Prmfa.Cdp;
using Salesforce.Prmfa.Option1;
using Salesforce.Prmfa.Shared;
using Xunit;

namespace Engineering.Tests;

public class FlowTests
{
    internal static SavedWebAuthnCredential Credential(long counter = 7)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new() { CredentialId = "AQID", RpId = "example.com", PrivateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            UserHandle = "BAUG", IsResidentCredential = true, SignCount = counter, BackupEligibility = true, BackupState = true };
    }

    [Fact]
    public async Task Option1AttachesAndKeepsAuthenticatorAliveUntilCapture()
    {
        var credential = Credential();
        await using var server = await SocketServer.Start(async socket =>
        {
            var attach = await SocketServer.Read(socket);
            Assert.Equal("Target.attachToTarget", attach.GetProperty("method").GetString());
            Assert.Equal("exact-page", attach.GetProperty("params").GetProperty("targetId").GetString());
            Assert.True(attach.GetProperty("params").GetProperty("flatten").GetBoolean());
            await SocketServer.Reply(socket, attach, new { sessionId = "ours" });
            foreach (var method in new[] { "WebAuthn.enable", "WebAuthn.addVirtualAuthenticator", "WebAuthn.addCredential", "WebAuthn.getCredentials" })
            {
                var command = await SocketServer.Read(socket);
                Assert.Equal(method, command.GetProperty("method").GetString());
                Assert.Equal("ours", command.GetProperty("sessionId").GetString());
                if (method == "WebAuthn.addVirtualAuthenticator")
                {
                    var options = command.GetProperty("params").GetProperty("options");
                    Assert.Equal("ctap2", options.GetProperty("protocol").GetString());
                    Assert.True(options.GetProperty("isUserVerified").GetBoolean());
                    await SocketServer.Reply(socket, command, new { authenticatorId = "auth" });
                }
                else if (method == "WebAuthn.getCredentials")
                    await SocketServer.Reply(socket, command, new { credentials = new[] { JsonSerializer.SerializeToElement(credential, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) } });
                else
                {
                    if (method == "WebAuthn.addCredential")
                    {
                        var saved = command.GetProperty("params").GetProperty("credential");
                        Assert.True(saved.GetProperty("isResidentCredential").GetBoolean());
                        Assert.Equal(7, saved.GetProperty("signCount").GetInt64());
                        Assert.True(saved.GetProperty("backupState").GetBoolean());
                        Assert.False(saved.TryGetProperty("publicKey", out _));
                    }
                    await SocketServer.Reply(socket, command, new { });
                }
            }
            await Task.Delay(100);
        });
        var configure = new ConfigureSalesforceWebAuthn(new ConfiguredCdpSessionProvider(new(server.Endpoint, TargetId: "exact-page")));
        await using var session = await configure.BeginAsync(credential);
        await Task.Delay(50); // Simulate UiPath retaining this object while it navigates.
        Assert.Equal(credential.PrivateKey, (await session.CaptureCredentialAsync()).PrivateKey);
        await server.Completion;
    }

    [Fact]
    public async Task NavigationErrorIsNotTreatedAsSuccess()
    {
        await using var server = await SocketServer.Start(async socket =>
        {
            var enable = await SocketServer.Read(socket);
            Assert.Equal("Page.enable", enable.GetProperty("method").GetString());
            await SocketServer.Reply(socket, enable, new { });
            var navigate = await SocketServer.Read(socket);
            Assert.Equal("Page.navigate", navigate.GetProperty("method").GetString());
            await SocketServer.Reply(socket, navigate, new { errorText = "net::ERR_FAILED" });
            await Task.Delay(100);
        });
        await using var client = new CdpClient();
        await client.ConnectAsync(server.Endpoint);
        await Assert.ThrowsAsync<IOException>(() => BrowserSession.NavigateAsync(client, "page", "https://example.com"));
        await server.Completion;
    }

    [Theory]
    [InlineData("https://org.example/lightning/page/home", true)]
    [InlineData("https://org.example/setup/home", true)]
    [InlineData("https://org.example/secur/frontdoor.jsp?retURL=/lightning/page/home", false)]
    [InlineData("https://evil.example/lightning/page/home", false)]
    [InlineData("http://org.example/lightning/page/home", false)]
    [InlineData("https://org.example/login?next=/setup/home", false)]
    public void ReadinessUsesOriginAndPath(string url, bool expected) =>
        Assert.Equal(expected, BrowserSession.IsAuthenticatedLocation(url, "https://org.example"));

    [Fact]
    public void FrontdoorEncodesTokenAndReturnPathSeparately()
    {
        var url = new Uri(Frontdoor.BuildUrl(new() { InstanceUrl = "https://org.example/", AccessToken = "a+b&c=!#" }, "/lightning/page/home?x=1&y=2"));
        Assert.Equal("?sid=a%2Bb%26c%3D%21%23&retURL=%2Flightning%2Fpage%2Fhome%3Fx%3D1%26y%3D2", url.Query);
        Assert.Throws<ArgumentException>(() => Frontdoor.BuildUrl(new() { InstanceUrl = "https://org.example", AccessToken = "x" }, "//evil.example"));
    }

    [Fact]
    public async Task CredentialsRoundTripAndAtomicReplacement()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var credential = Credential(uint.MaxValue);
            await CredentialStore.SaveAsync(path, credential);
            var loaded = await CredentialStore.LoadAsync(path);
            Assert.Equal(credential.PrivateKey, loaded.PrivateKey);
            Assert.Equal(uint.MaxValue, loaded.SignCount);
            Assert.True(loaded.BackupEligibility);
            await CredentialStore.SaveAsync(path, Credential(8));
            Assert.Equal(8, (await CredentialStore.LoadAsync(path)).SignCount);
        }
        finally { File.Delete(path); }
        CredentialValidation.Validate(Credential(-1));
        Assert.Throws<ArgumentException>(() => CredentialValidation.Validate(Credential(-2)));
    }

    [Fact]
    public void JwtHasCorrectClaimsAndVerifiableRs256Signature()
    {
        using var rsa = RSA.Create(2048);
        var jwt = JwtBearerClient.CreateJwtAssertion("client", "user", "https://test.salesforce.com/", rsa.ExportPkcs8PrivateKeyPem());
        var parts = jwt.Split('.');
        static byte[] Decode(string input) => Convert.FromBase64String(input.Replace('-', '+').Replace('_', '/').PadRight((input.Length + 3) / 4 * 4, '='));
        using var claims = JsonDocument.Parse(Decode(parts[1]));
        Assert.Equal("https://test.salesforce.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("client", claims.RootElement.GetProperty("iss").GetString());
        Assert.Equal("user", claims.RootElement.GetProperty("sub").GetString());
        Assert.InRange(claims.RootElement.GetProperty("exp").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 175, 180);
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task TokenExchangeUsesFormPost_AndDoesNotLeakErrorBody()
    {
        using var rsa = RSA.Create(2048);
        var keyPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            using var handler = new RecordingHandler();
            using var http = new HttpClient(handler);
            var settings = new SalesforceSettings { LoginUrl = "https://test.salesforce.com", ClientId = "client", Username = "user", PrivateKeyPath = keyPath };
            var client = new JwtBearerClient(http);
            Assert.Equal("token", (await client.AuthenticateAsync(settings)).AccessToken);
            Assert.Equal("https://test.salesforce.com/services/oauth2/token", handler.Url);
            Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer&assertion=", handler.Body);
            handler.Fail = true;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AuthenticateAsync(settings));
            Assert.DoesNotContain("SECRET", error.Message);
        }
        finally { File.Delete(keyPath); }
    }


    [Fact]
    public async Task Option1RejectsSessionFromAnotherConnection()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketServer.Start(async socket => await release.Task);
        var configure = new ConfigureSalesforceWebAuthn(new ConfiguredCdpSessionProvider(new(server.Endpoint, SessionId: "foreign")));
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => configure.BeginAsync(Credential()));
            Assert.Contains("originating WebSocket", error.Message);
        }
        finally { release.TrySetResult(); }
        await server.Completion;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ExportRejectsMissingOrAmbiguousCredentials(int count)
    {
        await using var server = await SocketServer.Start(async socket =>
        {
            var command = await SocketServer.Read(socket);
            await SocketServer.Reply(socket, command, new { credentials = Enumerable.Range(0, count).Select(_ => new { }).ToArray() });
            await Task.Delay(100);
        });
        await using var client = new CdpClient();
        await client.ConnectAsync(server.Endpoint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WebAuthnClient(client, "page").GetFirstCredentialAsync("auth"));
        await server.Completion;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool Fail;
        public string? Url;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            Url = request.RequestUri!.ToString();
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return new(Fail ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            { Content = new StringContent(Fail ? "SECRET" : "{\"access_token\":\"token\",\"instance_url\":\"https://org.example\"}") };
        }
    }
}
