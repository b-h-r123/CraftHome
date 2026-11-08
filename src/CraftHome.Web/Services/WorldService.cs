using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CraftHome.Web.Services;

public sealed record SavedWorld(string Name, string[] Dimensions);
public sealed record TrashedWorld(string Id, string Name);
public sealed record WorldBackup(string Name, long Bytes, DateTimeOffset CreatedAt);
public sealed record WorldDisk(long TotalBytes, long UsedBytes, long AvailableBytes)
{
    public int PercentUsed => TotalBytes > 0 ? (int)Math.Clamp(100d * UsedBytes / TotalBytes, 0, 100) : 0;
}
public sealed record WorldReport(string Selection, string ServerName, string? ActiveWorld, SavedWorld[] Worlds, WorldBackup[] Backups,
    WorldDisk? Disk, string? ChangeBlocker, TrashedWorld[] Trash);

public sealed class WorldService(ConnectionStore connections, McsManagerClient engine, InstanceOperations operations)
{
    private const string PropertiesFile = "/server.properties";
    private const string BackupDirectory = "crafthome-backups";
    private const string TrashDirectory = ".crafthome-world-trash";
    public const long MaxImportBytes = WorldArchive.MaxUpload;
    private const long ReserveBytes = 256L * 1024 * 1024;
    private static readonly Regex WorldName = new(@"\A[\p{L}\p{N}_-][\p{L}\p{N}._ -]{0,79}\z", RegexOptions.NonBacktracking);
    private static readonly Regex LevelName = new(@"(?m)^[ \t]*level-name(?:[ \t]*[=:][ \t]*|[ \t]+)(?<name>[^\r\n]*)");
    private static readonly Regex EscapedCharacter = new(@"\\u([0-9a-fA-F]{4})|\\(.)");
    private static readonly Regex BackupName = new(@"-(\d{8}-\d{6}Z)-[0-9a-f]{8}\.zip\z", RegexOptions.NonBacktracking);
    private static readonly Regex CustomWorldArgument = new(@"(?:^|[\s\""'])(?:--world|--level-name|--universe|--server-settings|--config|-w|-W|-c)(?=[\s=\""']|$)");

    public async Task<WorldReport?> InspectAsync(CancellationToken ct = default)
    {
        var connection = connections.Read();
        if (connection.DemoMode || connection.InstanceId.Length == 0) return null;
        var state = await ReadStateAsync(connection, ct);
        var backups = state.Root.Any(file => file.Directory && file.Name == BackupDirectory)
            ? (await ListAsync(connection, "/" + BackupDirectory, ct)).Select(ToBackup).OfType<WorldBackup>().OrderByDescending(item => item.CreatedAt).ToArray()
            : [];
        var disk = await GetDiskAsync(connection, state.AbsolutePath, ct);
        var trash = state.Root.Any(file => file.Directory && file.Name == TrashDirectory)
            ? (await ListAsync(connection, "/" + TrashDirectory, ct)).Where(file => file.Directory)
                .Select(file => ParseTrash(file.Name)).OfType<TrashedWorld>().OrderBy(item => item.Name, StringComparer.Ordinal).ToArray()
            : [];
        return new(Selection(connection), connection.Name, state.ActiveWorld, state.Worlds, backups, disk, state.Blocker, trash);
    }

    public async Task SelectAsync(string selection, string name, CancellationToken ct = default)
    {
        if (!IsWorldName(name)) throw new EngineException("地图名称无效。");
        await InStoppedInstanceAsync(selection, async (connection, state) =>
        {
            if (!state.Worlds.Any(world => world.Name == name)) throw new EngineException("找不到这张地图，请刷新列表。");
            if (name == state.ActiveWorld) return;
            await RequireStoppedAsync(connection, ct);
            await engine.EditFileAsync(connection, PropertiesFile, ReplaceLevelName(state.Properties!, name), ct);
            var saved = await engine.ReadFileAsync(connection, PropertiesFile, ct);
            if (ReadLevelName(saved) != name) throw new EngineException("管理引擎未确认地图切换，请检查服务器配置。");
        }, ct);
    }

    public async Task<string> BackupAsync(string selection, CancellationToken ct = default)
    {
        string? completed = null;
        await InStoppedInstanceAsync(selection, async (connection, state) =>
        {
            var world = state.Worlds.FirstOrDefault(item => item.Name == state.ActiveWorld)
                ?? throw new EngineException("当前地图还没有存档。首次启动并正常停服后再备份。");
            var disk = await GetDiskAsync(connection, state.AbsolutePath, ct)
                ?? throw new EngineException("无法确认实例所在磁盘的可用空间。本机备份需要同机管理节点。");
            var targets = new[] { "/" + world.Name }.Concat(world.Dimensions.Select(name => "/" + name)).ToArray();
            await CheckBackupSpaceAsync(connection, targets, disk.AvailableBytes, ct);
            if (!state.Root.Any(file => file.Directory && file.Name == BackupDirectory))
                await engine.MakeDirectoryAsync(connection, "/" + BackupDirectory, ct);
            await RequireStoppedAsync(connection, ct);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss'Z'", CultureInfo.InvariantCulture);
            var name = $"{world.Name}-{stamp}-{Guid.NewGuid().ToString("N")[..8]}.zip";
            // MCSManager resolves compression inputs from the archive's parent directory.
            var temporary = $"/.partial-{name}";
            await engine.CompressAsync(connection, temporary, targets, ct);
            await RequireStoppedAsync(connection, ct);
            await engine.MoveFileAsync(connection, temporary, $"/{BackupDirectory}/{name}", ct);
            var files = await engine.ListFilesAsync(connection, "/" + BackupDirectory, 0, name, ct);
            if (!files.Items.Any(item => item.Name == name && !item.Directory && item.Bytes > 0))
                throw new EngineException("未找到已完成的备份文件，请到管理引擎检查。");
            completed = name;
        }, ct);
        return completed!;
    }

    public Task ImportAsync(string selection, string name, Stream archive, CancellationToken ct = default)
    {
        if (!IsWorldName(name) || name == BackupDirectory) throw new EngineException("地图名可使用字母、数字、空格、点、下划线和连字符，最多 80 个字符。");
        return InStoppedInstanceAsync(selection, async (connection, state) =>
        {
            RequireAvailableWorldName(state, name);
            var disk = await GetDiskAsync(connection, state.AbsolutePath, ct)
                ?? throw new EngineException("无法确认实例所在磁盘的可用空间。地图导入需要同机管理节点。");
            var temp = Directory.CreateTempSubdirectory("crafthome-world-import-");
            var original = Path.Combine(temp.FullName, "upload.zip");
            var normalized = Path.Combine(temp.FullName, "world.zip");
            try
            {
                var (roots, bytes) = await WorldArchive.PrepareAsync(archive, original, normalized, name, ct);
                if (disk.AvailableBytes < ReserveBytes + bytes + new FileInfo(normalized).Length)
                    throw new EngineException("磁盘可用空间不足，暂不能导入地图。");
                await RequireStoppedAsync(connection, ct);
                var stage = "/.crafthome-import-" + Guid.NewGuid().ToString("N");
                var extract = stage + "/extract";
                await engine.MakeDirectoryAsync(connection, stage, ct);
                await engine.MakeDirectoryAsync(connection, extract, ct);
                await engine.UploadFileAsync(connection, normalized, stage, "world.zip", ct);
                await engine.ExtractZipAsync(connection, stage + "/world.zip", extract, ct);
                foreach (var root in roots)
                    if (!(await engine.ListFilesAsync(connection, extract + "/" + root, 0, "level.dat", ct)).Items.Any(file => !file.Directory && file.Name == "level.dat"))
                        throw new EngineException("解压后缺少地图存档，未发布到地图列表。");
                await RequireStoppedAsync(connection, ct);
                RequireAvailableWorldName(state with { Root = await ListAsync(connection, "/", ct) }, name);
                await MoveGroupAsync(connection, roots.Where(root => root != name).Append(name), extract, "", ct);
                try { await engine.DeleteFileAsync(connection, stage, ct); }
                catch (EngineException) { /* The imported world is already published; temporary files are isolated. */ }
            }
            finally
            {
                File.Delete(original);
                File.Delete(normalized);
                temp.Delete();
            }
        }, ct);
    }

    public Task TrashAsync(string selection, string name, CancellationToken ct = default, bool resetActive = false) =>
        InStoppedInstanceAsync(selection, async (connection, state) =>
        {
            var world = state.Worlds.SingleOrDefault(item => item.Name == name)
                ?? throw new EngineException("找不到这张地图，请刷新列表。");
            if (name == state.ActiveWorld && !resetActive) throw new EngineException("请先切换到另一张地图，或明确确认重置当前地图。");
            if (resetActive && name != state.ActiveWorld) throw new EngineException("当前地图已改变，请刷新后再重置。");
            if (resetActive && WorldRoots(name).Skip(1).Any(dimension => !world.Dimensions.Contains(dimension) &&
                state.Root.Any(file => file.Name.Equals(dimension, StringComparison.OrdinalIgnoreCase))))
                throw new EngineException("发现无法确认归属的同名维度，请先在 MCSManager 检查地图目录。");
            var root = "/" + TrashDirectory;
            if (!state.Root.Any(file => file.Directory && file.Name == TrashDirectory)) await engine.MakeDirectoryAsync(connection, root, ct);
            var destination = root + "/" + Guid.NewGuid().ToString("N") + "--" + name;
            await engine.MakeDirectoryAsync(connection, destination, ct);
            try
            {
                await RequireStoppedAsync(connection, ct);
                await MoveGroupAsync(connection, world.Dimensions.Append(name), "", destination, ct);
            }
            catch (EngineException)
            {
                try
                {
                    if ((await engine.ListFilesAsync(connection, destination, 0, ct: CancellationToken.None)).Total == 0)
                        await engine.DeleteFileAsync(connection, destination, CancellationToken.None);
                }
                catch (EngineException) { /* Keep any files that may still need recovery. */ }
                throw;
            }
        }, ct);

    public Task RestoreAsync(string selection, string id, CancellationToken ct = default) =>
        InStoppedInstanceAsync(selection, async (connection, state) =>
        {
            var (world, path) = await GetTrashAsync(connection, id, ct);
            RequireAvailableWorldName(state, world.Name);
            var files = await ListAsync(connection, path, ct);
            var roots = files.Where(file => file.Directory && WorldRoots(world.Name).Contains(file.Name, StringComparer.Ordinal)).Select(file => file.Name).ToArray();
            if (!roots.Contains(world.Name, StringComparer.Ordinal)) throw new EngineException("回收站中的地图不完整，请在 MCSManager 检查。");
            await RequireStoppedAsync(connection, ct);
            await MoveGroupAsync(connection, roots.Where(root => root != world.Name).Append(world.Name), path, "", ct);
            try { await engine.DeleteFileAsync(connection, path, ct); }
            catch (EngineException) { /* The map was already restored. */ }
        }, ct);

    public Task PurgeAsync(string selection, string id, string confirmation, CancellationToken ct = default) =>
        InStoppedInstanceAsync(selection, async (connection, _) =>
        {
            var (world, path) = await GetTrashAsync(connection, id, ct);
            if (world.Name != confirmation) throw new EngineException("请准确输入地图名以确认永久删除。");
            await RequireStoppedAsync(connection, ct);
            await engine.DeleteFileAsync(connection, path, ct);
            if ((await engine.ListFilesAsync(connection, "/" + TrashDirectory, 0, id, ct)).Items.Any(file => file.Name == id))
                throw new EngineException("管理引擎未确认删除，请刷新回收站。");
        }, ct);

    private async Task<(TrashedWorld World, string Path)> GetTrashAsync(ConnectionSettings connection, string id, CancellationToken ct)
    {
        var world = ParseTrash(id) ?? throw new EngineException("回收站条目无效。");
        var root = "/" + TrashDirectory;
        if (!(await engine.ListFilesAsync(connection, root, 0, id, ct)).Items.Any(file => file.Directory && file.Name == id))
            throw new EngineException("回收站条目已不存在，请刷新页面。");
        return (world, root + "/" + id);
    }

    private static TrashedWorld? ParseTrash(string id) => id.Length > 34 && id.AsSpan(0, 32).ToString().All(Uri.IsHexDigit)
        && id.AsSpan(32, 2).SequenceEqual("--") && IsWorldName(id[34..]) ? new(id, id[34..]) : null;

    private static string[] WorldRoots(string name) => [name, name + "_nether", name + "_the_end"];

    private static void RequireAvailableWorldName(WorldState state, string name)
    {
        if (WorldRoots(name).Any(root => state.Root.Any(file => file.Name.Equals(root, StringComparison.OrdinalIgnoreCase))))
            throw new EngineException("同名地图或维度已存在，请使用其他名称或先处理现有地图。");
    }

    private async Task MoveGroupAsync(ConnectionSettings connection, IEnumerable<string> names, string source, string target, CancellationToken ct)
    {
        var moved = new Stack<string>();
        try
        {
            foreach (var name in names)
            {
                await engine.MoveFileAsync(connection, source + "/" + name, target + "/" + name, ct);
                moved.Push(name);
            }
        }
        catch (EngineException)
        {
            while (moved.TryPop(out var name))
            {
                try { await engine.MoveFileAsync(connection, target + "/" + name, source + "/" + name, CancellationToken.None); }
                catch (EngineException) { throw new EngineException("移动地图时未能完整恢复，请在 MCSManager 核对地图和暂存目录后再开服。"); }
            }
            throw;
        }
    }

    private async Task InStoppedInstanceAsync(string selection, Func<ConnectionSettings, WorldState, Task> action, CancellationToken ct)
    {
        if (!await operations.Gate.WaitAsync(0, ct)) throw new EngineException("另一个服务器操作正在进行，请稍后重试。");
        try
        {
            var connection = connections.Read();
            if (connection.DemoMode || connection.InstanceId.Length == 0 || Selection(connection) != selection)
                throw new EngineException("当前管理的服务器已改变，请刷新页面。");
            await RequireStoppedAsync(connection, ct);
            var state = await ReadStateAsync(connection, ct);
            if (state.Blocker is not null) throw new EngineException(state.Blocker);
            await action(connection, state);
        }
        finally { operations.Gate.Release(); }
    }

    private async Task RequireStoppedAsync(ConnectionSettings connection, CancellationToken ct)
    {
        var snapshot = await engine.GetSnapshotAsync(connection, ct);
        if (snapshot.Status != 0) throw new EngineException("请先正常停止服务器，等状态显示已停止后再操作地图。");
        if (snapshot.FileTasks > 0) throw new EngineException("管理引擎正在处理文件，请等待当前任务完成。");
    }

    private async Task<WorldState> ReadStateAsync(ConnectionSettings connection, CancellationToken ct)
    {
        var snapshot = await engine.GetSnapshotAsync(connection, ct);
        var first = await engine.ListFilesAsync(connection, "/", 0, ct: ct);
        var root = await ListAsync(connection, "/", ct, first);
        var properties = root.Any(file => !file.Directory && file.Name == "server.properties")
            ? await engine.ReadFileAsync(connection, PropertiesFile, ct) : null;
        var active = properties is null ? null : ReadLevelName(properties);
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in root.Where(file => file.Directory && file.Name != BackupDirectory && IsWorldName(file.Name)))
        {
            var marker = await engine.ListFilesAsync(connection, "/" + directory.Name, 0, "level.dat", ct);
            if (marker.Items.Any(file => !file.Directory && file.Name == "level.dat")) discovered.Add(directory.Name);
        }

        var dimensions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var grouped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in discovered)
        {
            if (name == active) continue;
            var (suffix, folder) = name.EndsWith("_nether", StringComparison.Ordinal) ? ("_nether", "DIM-1")
                : name.EndsWith("_the_end", StringComparison.Ordinal) ? ("_the_end", "DIM1") : ("", "");
            if (suffix.Length == 0) continue;
            var parent = name[..^suffix.Length];
            if (!discovered.Contains(parent)) continue;
            var children = await engine.ListFilesAsync(connection, "/" + name, 0, folder, ct);
            if (!children.Items.Any(file => file.Directory && file.Name == folder)) continue;
            if (!dimensions.TryGetValue(parent, out var list)) dimensions[parent] = list = [];
            list.Add(name);
            grouped.Add(name);
        }
        var worlds = discovered.Except(grouped).OrderByDescending(name => name == active).ThenBy(name => name, StringComparer.Ordinal)
            .Select(name => new SavedWorld(name, dimensions.TryGetValue(name, out var list) ? list.Order(StringComparer.Ordinal).ToArray() : [])).ToArray();
        var blocker = snapshot.Status != 0 ? "请先正常停服，再导入、切换、备份或删除地图。"
            : snapshot.FileTasks > 0 ? "管理引擎正在处理文件，请等待当前任务完成。"
            : properties is null ? "找不到 server.properties。首次开服后再管理地图。"
            : properties.Contains('\ufffd') || active is null || !IsWorldName(active) || properties.Contains("\\\n") || properties.Contains("\\\r\n")
                ? "当前地图路径或配置格式不受支持。请在 MCSManager 检查 server.properties。"
            : CustomWorldArgument.IsMatch(snapshot.StartCommand ?? "") ? "启动命令指定了自定义地图或配置路径，请在 MCSManager 管理这些地图。"
            : null;
        return new(first.AbsolutePath, root, properties, active, worlds, blocker);
    }

    private async Task<EngineFile[]> ListAsync(ConnectionSettings connection, string path, CancellationToken ct, EngineFiles? first = null)
    {
        first ??= await engine.ListFilesAsync(connection, path, 0, ct: ct);
        if (first.Total > 10_000) throw new EngineException("目录文件过多，请在 MCSManager 管理该目录。");
        var items = first.Items.ToList();
        for (var page = 1; page * 100 < first.Total; page++)
            items.AddRange((await engine.ListFilesAsync(connection, path, page, ct: ct)).Items);
        return items.ToArray();
    }

    private async Task<WorldDisk?> GetDiskAsync(ConnectionSettings connection, string path, CancellationToken ct)
    {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var panel) || !panel.IsLoopback || !Path.IsPathFullyQualified(path)) return null;
        EngineNode[] nodes;
        try { nodes = await engine.GetNodesAsync(connection, ct); }
        catch (EngineException) { return null; }
        if (!nodes.Any(node => node.Id == connection.DaemonId && node.Available &&
            (node.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || System.Net.IPAddress.TryParse(node.Host, out var address) && System.Net.IPAddress.IsLoopback(address)))) return null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullPath = Path.GetFullPath(path);
        try
        {
            var drive = DriveInfo.GetDrives().Where(drive => fullPath.Equals(Path.TrimEndingDirectorySeparator(drive.Name), comparison)
                || fullPath.StartsWith(Path.EndsInDirectorySeparator(drive.Name) ? drive.Name : drive.Name + Path.DirectorySeparatorChar, comparison))
                .MaxBy(drive => drive.Name.Length);
            return drive is { IsReady: true } ? new(drive.TotalSize, drive.TotalSize - drive.TotalFreeSpace, drive.AvailableFreeSpace) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task CheckBackupSpaceAsync(ConnectionSettings connection, string[] targets, long available, CancellationToken ct)
    {
        var remaining = available - ReserveBytes;
        var directories = new Queue<string>(targets);
        var inspected = 0;
        while (directories.TryDequeue(out var directory))
        {
            if (++inspected > 160) throw new EngineException("地图目录过多，无法核对备份所需空间。请在 MCSManager 手动备份。");
            foreach (var file in await ListAsync(connection, directory, ct))
            {
                if (file.Name.Length == 0 || file.Name is "." or ".." || file.Name.Contains('/') || file.Name.Contains('\\'))
                    throw new EngineException("地图包含无法备份的路径，请在 MCSManager 检查。");
                if (file.Directory) directories.Enqueue(directory + "/" + file.Name);
                var bytes = Math.Max(file.Bytes, 0);
                remaining -= bytes + bytes / 50 + 1024;
                if (remaining < 0) throw new EngineException("磁盘可用空间不足。请为地图文件和压缩预留空间后再备份。");
            }
        }
    }

    private static WorldBackup? ToBackup(EngineFile item)
    {
        if (item.Directory || item.Name.StartsWith(".partial-", StringComparison.Ordinal) || item.Bytes <= 0) return null;
        var match = BackupName.Match(item.Name);
        return match.Success && DateTimeOffset.TryParseExact(match.Groups[1].Value, "yyyyMMdd-HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var time) ? new(item.Name, item.Bytes, time) : null;
    }

    private static string Selection(ConnectionSettings connection) => connection.DaemonId + "/" + connection.InstanceId;
    private static bool IsWorldName(string name) => WorldName.IsMatch(name) && !name.EndsWith(' ') && !name.EndsWith('.');

    internal static string ReadLevelName(string properties)
    {
        var matches = LevelName.Matches(properties);
        return matches.Count == 0 ? "world" : EscapedCharacter.Replace(matches[^1].Groups["name"].Value, match =>
            match.Groups[1].Success ? ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString()
                : match.Groups[2].Value);
    }

    internal static string ReplaceLevelName(string properties, string name)
    {
        var line = new StringBuilder("level-name=");
        foreach (var character in name)
            if (char.IsAscii(character)) line.Append(character); else line.Append($"\\u{(int)character:x4}");
        var matches = LevelName.Matches(properties);
        if (matches.Count > 0)
        {
            var last = matches[^1];
            return properties[..last.Index] + line + properties[(last.Index + last.Length)..];
        }
        var newline = properties.Contains("\r\n") ? "\r\n" : "\n";
        return properties + (properties.EndsWith('\n') || properties.Length == 0 ? "" : newline) + line + newline;
    }

    private sealed record WorldState(string AbsolutePath, EngineFile[] Root, string? Properties, string? ActiveWorld, SavedWorld[] Worlds, string? Blocker);
}
