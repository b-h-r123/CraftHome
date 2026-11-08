using System.Net;
using System.Text.Json;
using CraftHome.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace CraftHome.Tests;

public sealed class PlayerAccessTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "crafthome-player-tests-" + Guid.NewGuid());

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task BatchAddAndRemovePreserveExistingPlayersAndReloadOnlyWhenRunning(int state)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("CraftHome:DataDirectory", directory).UseEnvironment("Testing"));
        var store = factory.Services.GetRequiredService<ConnectionStore>();
        await store.SaveAsync(new(false, "http://127.0.0.1:23333", "test-key", "node", "instance"));
        var handler = new WhitelistEngine(state);
        using var engine = new McsManagerClient(new HttpClient(handler));
        var service = new PlayerAccessService(store, engine, new InstanceOperations());
        await service.AddAsync("node/instance", "PlayerTwo，PlayerThree\nplayertwo");
        Assert.Equal(new[] { "JeRyan", "PlayerTwo", "PlayerThree" }, handler.Players.Select(player => player.Name));
        Assert.Equal("64d8811c-c74a-3a01-b78e-df0cda7fff83", handler.Players[0].Uuid);
        await service.RemoveAsync("node/instance", handler.Players[1].Uuid);
        Assert.Equal(new[] { "JeRyan", "PlayerThree" }, handler.Players.Select(player => player.Name));
        Assert.Equal(state == 3 ? 2 : 0, handler.Reloads);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    public async Task AuthMeCanBeArchivedAndRestoredOnlyForTheStoppedSupportedInstallation(int state, bool otherVersion)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("CraftHome:DataDirectory", directory).UseEnvironment("Testing"));
        var store = factory.Services.GetRequiredService<ConnectionStore>();
        await store.SaveAsync(new(false, "http://127.0.0.1:23333", "test-key", "node", "instance"));
        var handler = new WhitelistEngine(state);
        const string plugin = "/plugins/AuthMe-6.0.1-Paper.jar";
        const string archive = "/.crafthome-disabled-AuthMe-6.0.1-Paper.jar";
        handler.Files.UnionWith(["/plugins/AuthMe/config.yml", "/plugins/AuthMe/authme.db", otherVersion ? "/plugins/AuthMe-Other.jar" : plugin]);
        using var engine = new McsManagerClient(new HttpClient(handler));
        var operations = new InstanceOperations();
        var access = new PlayerAccessService(store, engine, operations);
        var auth = new AuthMeInstaller(access, engine, operations, factory.Services.GetRequiredService<RuntimePaths>());

        if (state != 0 || otherVersion)
        {
            await Assert.ThrowsAsync<EngineException>(() => auth.SetInstalledAsync("node/instance", installed: false));
            Assert.DoesNotContain(archive, handler.Files);
            Assert.Equal(0, handler.Moves);
        }
        else
        {
            Assert.Equal(AuthMeState.Installed, (await access.InspectAsync())!.AuthMe);
            await auth.SetInstalledAsync("node/instance", installed: false);
            Assert.Equal(AuthMeState.Archived, (await access.InspectAsync())!.AuthMe);
            Assert.DoesNotContain(plugin, handler.Files);
            Assert.Contains(archive, handler.Files);
            await auth.SetInstalledAsync("node/instance", installed: false);
            Assert.Equal(1, handler.Moves);
            await auth.SetInstalledAsync("node/instance", installed: true);
            Assert.Contains(plugin, handler.Files);
            Assert.DoesNotContain(archive, handler.Files);
            Assert.Equal(AuthMeState.Installed, (await access.InspectAsync())!.AuthMe);
        }
        Assert.Contains("/plugins/AuthMe/config.yml", handler.Files);
        Assert.Contains("/plugins/AuthMe/authme.db", handler.Files);
        Assert.Single(handler.Players);
    }

    private sealed class WhitelistEngine(int status) : HttpMessageHandler
    {
        public WhitelistPlayer[] Players { get; private set; } = [PlayerAccessService.OfflinePlayer("JeRyan")];
        public HashSet<string> Files { get; } = ["/whitelist.json"];
        public int Reloads { get; private set; }
        public int Moves { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var query = QueryHelpers.ParseQuery(uri.Query);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = json.RootElement;
            switch (uri.AbsolutePath)
            {
                case "/api/instance": return Respond(new { status });
                case "/api/files/list":
                    var prefix = query["target"].ToString().TrimEnd('/') + "/";
                    var names = Files.Where(file => file.StartsWith(prefix, StringComparison.Ordinal))
                        .Select(file => file[prefix.Length..].Split('/')[0]).Distinct()
                        .Where(name => name.Contains(query["file_name"].ToString(), StringComparison.OrdinalIgnoreCase)).ToArray();
                    return Respond(new { absolutePath = "/tmp/game", total = names.Length, items = names.Select(name =>
                        new { name, type = Files.Contains(prefix + name) ? 1 : 0, size = 100 }) });
                case "/api/files/move":
                    var pair = body.GetProperty("targets")[0];
                    Assert.True(Files.Remove(pair[0].GetString()!));
                    Assert.True(Files.Add(pair[1].GetString()!));
                    Moves++;
                    return Respond(true);
                case "/api/files/":
                    if (body.GetProperty("target").GetString() == "/server.properties") return Respond("online-mode=false\nwhite-list=true\n");
                    if (body.TryGetProperty("text", out var text))
                    { Players = JsonSerializer.Deserialize<WhitelistPlayer[]>(text.GetString()!)!; return Respond(true); }
                    return Respond(JsonSerializer.Serialize(Players));
                case "/api/protected_instance/command":
                    Assert.Equal("whitelist reload", query["command"].ToString());
                    Reloads++;
                    return Respond(true);
                default: throw new InvalidOperationException("Unexpected engine route " + uri.AbsolutePath);
            }
        }

        private static HttpResponseMessage Respond(object data) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { status = 200, data })) };
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "crafthome-player-tests-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected test directory");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
