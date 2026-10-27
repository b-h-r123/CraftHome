using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CraftHome.Tests;

public sealed class AuthorizationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "crafthome-tests-" + Guid.NewGuid());
    private readonly WebApplicationFactory<Program> factory;

    public AuthorizationTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("CraftHome:DataDirectory", directory).UseEnvironment("Testing"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/settings")]
    public async Task AnonymousVisitorCannotAccessControls(string path)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task PublicRegistrationIsDisabled()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Account/Register")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/Account/Register", new StringContent(""))).StatusCode);
    }

    [Fact]
    public async Task UnsupportedAccountFlowsAreUnavailable()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Account/Manage/Email")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Account/Manage/TwoFactorAuthentication")).StatusCode);
    }

    [Fact]
    public async Task ConnectionSecretIsProtectedAndCanBeReloaded()
    {
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<CraftHome.Web.Services.ConnectionStore>();
        var secret = "test-only-sensitive-key";
        await store.SaveAsync(new(false, "http://127.0.0.1:23333", secret, "daemon", "instance"));
        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(Path.Combine(directory, "connection.json")));
        var reloaded = new CraftHome.Web.Services.ConnectionStore(
            factory.Services.GetRequiredService<CraftHome.Web.Services.RuntimePaths>(),
            factory.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());
        Assert.Equal(secret, reloaded.Read().ApiKey);
    }

    [Fact]
    public async Task LoginPostWithoutAntiforgeryTokenIsRejected()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = "owner@crafthome.local", ["Input.Password"] = "invalid"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HealthCheckDoesNotExposeCredentials()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("password", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
