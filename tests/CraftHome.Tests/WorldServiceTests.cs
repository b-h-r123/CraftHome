using System.Net;
using System.IO.Compression;
using System.Text.Json;
using CraftHome.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace CraftHome.Tests;

public sealed class WorldServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "crafthome-worlds-test-" + Guid.NewGuid());
    private const string Selection = "node/instance";

    [Fact]
    public async Task SwitchingAnExistingWorldPreservesOtherPropertiesAndGroupsPaperDimensions()
    {
        using var factory = Factory();
        var handler = new EngineStub();
        using var engine = new McsManagerClient(new HttpClient(handler));
        var service = await ConnectAsync(factory, engine);
        var initial = (await service.InspectAsync())!;
        Assert.Equal(new[] { "world", "冒险" }, initial.Worlds.Select(world => world.Name));
        Assert.Equal(new[] { "world_nether", "world_the_end" }, initial.Worlds[0].Dimensions);
        await service.SelectAsync(initial.Selection, "冒险");
        Assert.Equal("# Example\r\nlevel-name=\\u5192\\u9669\r\nserver-port=25565\r\n", handler.Properties);
        Assert.Equal("冒险", (await service.InspectAsync())!.ActiveWorld);
        Assert.Equal(1, handler.Mutations);
    }

    [Fact]
    public async Task MapChangesAndBackupsCannotWriteWhileTheServerIsRunning()
    {
        using var factory = Factory();
        var handler = new EngineStub { Running = true };
        using var engine = new McsManagerClient(new HttpClient(handler));
        var service = await ConnectAsync(factory, engine);
        await Assert.ThrowsAsync<EngineException>(() => service.SelectAsync(Selection, "冒险"));
        await Assert.ThrowsAsync<EngineException>(() => service.BackupAsync(Selection));
        await Assert.ThrowsAsync<EngineException>(() => service.TrashAsync(Selection, "冒险"));
        await Assert.ThrowsAsync<EngineException>(() => service.TrashAsync(Selection, "world", resetActive: true));
        await Assert.ThrowsAsync<EngineException>(() => service.ImportAsync(Selection, "imported", Stream.Null));
        Assert.Equal(0, handler.Mutations);
    }

    [Theory]
    [InlineData("../escape.dat", false)]
    [InlineData("world/unsafe-link", true)]
    public async Task UnsafeZipIsRejectedBeforeCreatingAnyRemoteFiles(string path, bool symbolicLink)
    {
        using var factory = Factory();
        var handler = new EngineStub();
        using var engine = new McsManagerClient(new HttpClient(handler));
        var service = await ConnectAsync(factory, engine);
        using var file = new MemoryStream();
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(path);
            if (symbolicLink) entry.ExternalAttributes = unchecked((int)0xA0000000);
        }
        file.Position = 0;
        var error = await Assert.ThrowsAsync<EngineException>(() => service.ImportAsync(Selection, "imported", file));
        Assert.Contains("不安全", error.Message);
        Assert.Equal(0, handler.Mutations);
    }

    [Fact]
    public async Task ActiveMapRequiresAnExplicitReset()
    {
        using var factory = Factory();
        var handler = new EngineStub();
        using var engine = new McsManagerClient(new HttpClient(handler));
        var service = await ConnectAsync(factory, engine);
        var error = await Assert.ThrowsAsync<EngineException>(() => service.TrashAsync(Selection, "world"));
        Assert.Contains("先切换", error.Message);
        Assert.Equal(0, handler.Mutations);
    }

    [Fact]
    public async Task ResettingTheActiveMapArchivesAllDimensionsWithoutChangingTheWorldName()
    {
        using var factory = Factory();
        var handler = new EngineStub();
        using var engine = new McsManagerClient(new HttpClient(handler));
        var service = await ConnectAsync(factory, engine);
        var properties = handler.Properties;
        await service.TrashAsync(Selection, "world", resetActive: true);
        var report = (await service.InspectAsync())!;
        Assert.Equal("world", report.ActiveWorld);
        Assert.Equal(properties, handler.Properties);
        Assert.DoesNotContain(report.Worlds, world => world.Name == "world");
        var trash = Assert.Single(report.Trash);
        Assert.Equal("world", trash.Name);
        await service.RestoreAsync(Selection, trash.Id);
        var restored = (await service.InspectAsync())!;
        Assert.Empty(restored.Trash);
        Assert.Equal(new[] { "world_nether", "world_the_end" }, restored.Worlds[0].Dimensions);
    }

    [Fact]
    public async Task AFailedDimensionMoveRestoresTheWorldAndDoesNotLeaveAnEmptyTrashEntry()
    {
        using var factory = Factory();
        var handler = new EngineStub { RejectMoveOf = "/world_the_end" };
        using var engine = new McsManagerClient(new HttpClient(handler));
        var service = await ConnectAsync(factory, engine);
        await Assert.ThrowsAsync<EngineException>(() => service.TrashAsync(Selection, "world", resetActive: true));
        var report = (await service.InspectAsync())!;
        Assert.Empty(report.Trash);
        Assert.Equal(new[] { "world_nether", "world_the_end" }, report.Worlds[0].Dimensions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyACompletedBackupOfTheSelectedWorldAndItsDimensionsIsListed(bool failCompression)
    {
        using var factory = Factory();
        var handler = new EngineStub { FailCompression = failCompression };
        using var engine = new McsManagerClient(new HttpClient(handler));
        var service = await ConnectAsync(factory, engine);
        if (failCompression)
        {
            await Assert.ThrowsAsync<EngineException>(() => service.BackupAsync(Selection));
            Assert.Empty((await service.InspectAsync())!.Backups);
        }
        else
        {
            var name = await service.BackupAsync(Selection);
            var backup = Assert.Single((await service.InspectAsync())!.Backups);
            Assert.Equal(name, backup.Name);
            Assert.True(backup.Bytes > 0);
        }
        Assert.Equal(new[] { "/world", "/world_nether", "/world_the_end" }, handler.CompressedTargets);
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.UseSetting("CraftHome:DataDirectory", directory).UseEnvironment("Testing"));

    private static async Task<WorldService> ConnectAsync(WebApplicationFactory<Program> factory, McsManagerClient engine)
    {
        var store = factory.Services.GetRequiredService<ConnectionStore>();
        await store.SaveAsync(new(false, "http://127.0.0.1:23333", "test-key", "node", "instance"));
        return new(store, engine, new InstanceOperations());
    }

    private sealed class EngineStub : HttpMessageHandler
    {
        private readonly Dictionary<string, EngineFile[]> tree = new()
        {
            ["/"] = [Dir("world"), Dir("world_nether"), Dir("world_the_end"), Dir("冒险"), Dir("plugins"), File("server.properties")],
            ["/world"] = [File("level.dat")],
            ["/world_nether"] = [File("level.dat"), Dir("DIM-1")],
            ["/world_the_end"] = [File("level.dat"), Dir("DIM1")],
            ["/world_nether/DIM-1"] = [], ["/world_the_end/DIM1"] = [],
            ["/冒险"] = [File("level.dat")], ["/plugins"] = []
        };
        public bool Running { get; init; }
        public bool FailCompression { get; init; }
        public string? RejectMoveOf { get; init; }
        public string Properties { get; private set; } = "# Example\r\nlevel-name=world\r\nserver-port=25565\r\n";
        public string[]? CompressedTargets { get; private set; }
        public int Mutations { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var query = QueryHelpers.ParseQuery(uri.Query);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = document.RootElement;
            switch (uri.AbsolutePath)
            {
                case "/api/instance": return Response(new { status = Running ? 3 : 0, info = new { fileLock = 0 } });
                case "/api/service/remote_services_list": return Response(new[] { new { uuid = "node", ip = "127.0.0.1", available = true } });
                case "/api/files/list":
                    var filter = query["file_name"].ToString();
                    var files = tree[query["target"].ToString()].Where(file => file.Name.Contains(filter, StringComparison.Ordinal)).ToArray();
                    return Response(new { absolutePath = Path.GetTempPath(), total = files.Length, items = files.Select(file => new { name = file.Name, type = file.Directory ? 0 : 1, size = file.Bytes }) });
                case "/api/files/":
                    if (!body.TryGetProperty("text", out var text)) return Response(Properties);
                    Properties = text.GetString()!;
                    break;
                case "/api/files/mkdir":
                    var directory = body.GetProperty("target").GetString()!;
                    var (parent, folder) = Split(directory);
                    tree[parent] = [..tree[parent], Dir(folder)];
                    tree[directory] = [];
                    break;
                case "/api/files/compress":
                    CompressedTargets = body.GetProperty("targets").Deserialize<string[]>()!;
                    var archive = body.GetProperty("source").GetString()!;
                    Assert.Equal("", archive[..archive.LastIndexOf('/')]);
                    tree["/"] = [..tree["/"], File(archive[1..])];
                    if (FailCompression) return new(HttpStatusCode.InternalServerError);
                    break;
                case "/api/files/move":
                    var pair = body.GetProperty("targets")[0];
                    var source = pair[0].GetString()!;
                    if (source == RejectMoveOf) return Response(false);
                    var destination = pair[1].GetString()!;
                    var (oldParent, oldName) = Split(source);
                    var (newParent, newName) = Split(destination);
                    var moved = tree[oldParent].Single(file => file.Name == oldName);
                    tree[oldParent] = tree[oldParent].Where(file => file.Name != oldName).ToArray();
                    tree[newParent] = [..tree[newParent], moved with { Name = newName }];
                    foreach (var path in tree.Keys.Where(path => path == source || path.StartsWith(source + "/", StringComparison.Ordinal)).ToArray())
                    {
                        tree[destination + path[source.Length..]] = tree[path];
                        tree.Remove(path);
                    }
                    break;
                case "/api/files":
                    var deleted = body.GetProperty("targets")[0].GetString()!;
                    var (deletedParent, deletedName) = Split(deleted);
                    tree[deletedParent] = tree[deletedParent].Where(file => file.Name != deletedName).ToArray();
                    tree.Remove(deleted);
                    break;
                default: throw new InvalidOperationException("Unexpected engine route " + uri.AbsolutePath);
            }
            Mutations++;
            return Response(true);
        }

        private static HttpResponseMessage Response(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { status = 200, data })) };
        private static (string Parent, string Name) Split(string path) =>
            (path[..Math.Max(1, path.LastIndexOf('/'))], path[(path.LastIndexOf('/') + 1)..]);
        private static EngineFile Dir(string name) => new(name, true, 0);
        private static EngineFile File(string name) => new(name, false, 128);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "crafthome-worlds-test-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected test directory");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
