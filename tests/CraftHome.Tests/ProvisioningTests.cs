using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CraftHome.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CraftHome.Tests;

public sealed class ProvisioningTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "crafthome-provision-tests-" + Guid.NewGuid());
    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.UseSetting("CraftHome:DataDirectory", directory).UseEnvironment("Testing"));
    private static CreateServerOptions Valid => new("测试服", "1.21.11", 25566, 512, "example.com", true, "JeRyan", true);

    [Fact]
    public async Task EulaMustBeExplicitlyAcceptedBeforeCreation()
    {
        using var factory = Factory();
        var service = factory.Services.GetRequiredService<ServerProvisioner>();
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(Valid with { AcceptEula = false }, _ => Task.CompletedTask));
    }

    [Theory]
    [InlineData("端口被占用")]
    [InlineData("可用内存不足")]
    public async Task FailedPreflightDoesNotCreateAnInstance(string reason)
    {
        using var factory = Factory();
        var store = factory.Services.GetRequiredService<ConnectionStore>();
        await store.SaveAsync(new(false, "http://127.0.0.1:23333", "test-key", "daemon", "existing"));
        var mutations = 0;
        var engine = new McsManagerClient(new HttpClient(new Handler(request =>
        {
            if (request.Method != HttpMethod.Get) mutations++;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":200,\"data\":[{\"uuid\":\"daemon\",\"ip\":\"127.0.0.1\",\"available\":true}]}") };
        })));
        var service = new ServerProvisioner(store, engine, factory.Services.GetRequiredService<PaperCatalog>(), new BlockedHost(reason));
        var error = await Assert.ThrowsAsync<EngineException>(() => service.CreateAsync(Valid, _ => Task.CompletedTask));
        Assert.Contains(reason, error.Message);
        Assert.Equal(0, mutations);
        Assert.Equal("existing", store.Read().InstanceId);
    }

    [Theory]
    [InlineData("1.21.11; touch /tmp/bad")]
    [InlineData("../../etc/passwd")]
    [InlineData("26.3")]
    public void UnsupportedVersionsCannotBecomeDownloadPaths(string version) => Assert.Throws<ArgumentException>(() => ServerProvisioner.Validate(Valid with { Version = version }));

    [Theory]
    [InlineData("JeRyan\nonline-mode=false")]
    [InlineData("../../ops")]
    [InlineData("")]
    public void OfflinePlayerMustBeAValidSingleName(string name) => Assert.Throws<ArgumentException>(() => ServerProvisioner.Validate(Valid with { PlayerName = name }));

    [Fact]
    public void OfflineWhitelistUsesJavaCompatibleUuid()
        => Assert.Equal("64d8811c-c74a-3a01-b78e-df0cda7fff83", ServerProvisioner.OfflineUuid("JeRyan"));

    [Fact]
    public void OfflineModeKeepsWhitelistEnabled()
    {
        var text = ServerProvisioner.Properties(Valid);
        Assert.Contains("online-mode=false\n", text);
        Assert.Contains("white-list=true\n", text);
        Assert.Contains("server-port=25566\n", text);
    }

    [Fact]
    public async Task CatalogDoesNotUseExperimentalBuild()
    {
        var sha = new string('a', 64);
        var body = JsonSerializer.Serialize(new[]
        {
            new { id = 200, channel = "EXPERIMENTAL", downloads = new Dictionary<string, object>() },
            new { id = 132, channel = "STABLE", downloads = new Dictionary<string, object> { ["server:default"] = new { url = "https://fill-data.papermc.io/v1/objects/" + sha + "/paper.jar", checksums = new { sha256 = sha }, size = 8 } } }
        });
        using var factory = Factory();
        using var catalog = new PaperCatalog(new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) })), factory.Services.GetRequiredService<RuntimePaths>());
        Assert.Equal(132, (await catalog.GetBuildAsync("1.21.11")).Number);
    }

    [Fact]
    public async Task HashMismatchDoesNotLeaveAUsableJar()
    {
        using var factory = Factory();
        using var catalog = new PaperCatalog(new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("bad") })), factory.Services.GetRequiredService<RuntimePaths>());
        var build = new PaperBuild("1.21.11", 132, "https://fill-data.papermc.io/test.jar", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("good"))), 3);
        await Assert.ThrowsAsync<EngineException>(() => catalog.DownloadAsync(build));
        var target = Path.Combine(factory.Services.GetRequiredService<RuntimePaths>().DataDirectory, "downloads", build.Sha256.ToLowerInvariant() + ".jar");
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task NewConfigurationFileIsCreatedBeforeEditing()
    {
        var calls = new List<string>();
        var engine = new McsManagerClient(new HttpClient(new Handler(request =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":200,\"data\":true}") };
        })));
        await engine.WriteFileAsync(new(false, "http://127.0.0.1:23333", "test-key", "daemon", "instance"), "/eula.txt", "eula=true\n");
        Assert.Equal(new[] { "POST /api/files/touch", "PUT /api/files/" }, calls);
    }

    [Fact]
    public async Task JarUploadIsSentAsABinaryFilePart()
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "upload.jar");
        var bytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(file, bytes);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var receiver = builder.Build();
        var received = false;
        receiver.MapPost("/upload/test-passport", async context =>
        {
            var form = await context.Request.ReadFormAsync();
            var upload = Assert.Single(form.Files);
            Assert.Equal("file", upload.Name);
            Assert.Equal("server.jar", upload.FileName);
            Assert.Equal("application/octet-stream", upload.ContentType);
            using var output = new MemoryStream();
            await upload.CopyToAsync(output);
            Assert.Equal(bytes, output.ToArray());
            received = true;
            await context.Response.WriteAsync("OK");
        });
        await receiver.StartAsync();
        var passport = JsonSerializer.Serialize(new { status = 200, data = new { password = "test-passport", addr = receiver.Urls.Single() } });
        var engine = new McsManagerClient(new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(passport) })));
        await engine.UploadJarAsync(new(false, "http://127.0.0.1:23333", "test-key", "daemon", "instance"), file);
        Assert.True(received);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class BlockedHost(string reason) : IHostInspector
    {
        public Task<HostReport> InspectAsync(int port, int memoryMiB, CancellationToken ct = default)
            => Task.FromResult(new HostReport("Linux", 21, 100, 10, port, [reason], []));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "crafthome-provision-tests-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected test directory");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
