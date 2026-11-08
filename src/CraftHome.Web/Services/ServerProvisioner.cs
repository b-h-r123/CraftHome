using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CraftHome.Web.Services;

public sealed record CreateServerOptions(string Name, string Version, int Port, int MemoryMiB, string PublicHost,
    bool OfflineMode, string PlayerName, bool AcceptEula);
public sealed record CreatedServer(string InstanceId, string Name, string Address, int Port, int MemoryMiB, string Version, int Build, bool OfflineMode = false);

public sealed class ServerProvisioner(ConnectionStore connections, McsManagerClient engine, PaperCatalog catalog,
    IHostInspector inspector, InstanceOperations operations, RuntimePaths paths)
{
    private readonly SemaphoreSlim gate = operations.Gate;
    private readonly string checkpointPath = Path.Combine(paths.DataDirectory, "first-start.json");
    private sealed record FirstStartCheckpoint(string BaseUrl, string DaemonId, CreatedServer Server);

    private async Task<FirstStartCheckpoint?> ReadCheckpointAsync(CancellationToken ct)
    {
        try
        {
            await using var input = new FileStream(checkpointPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<FirstStartCheckpoint>(input, cancellationToken: ct);
        }
        catch (FileNotFoundException) { return null; }
        catch (JsonException) { throw new EngineException("无法读取上次开服进度，请在服务器页面检查已有实例。"); }
    }

    public async Task<CreatedServer?> GetPendingAsync(CancellationToken ct = default)
        => (await ReadCheckpointAsync(ct))?.Server;

    public async Task DismissPendingAsync(string instanceId, CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct)) throw new EngineException("向导正在处理另一个操作，请稍后重试。");
        try
        {
            if ((await ReadCheckpointAsync(ct))?.Server.InstanceId == instanceId) File.Delete(checkpointPath);
        }
        finally { gate.Release(); }
    }

    public async Task<ConnectionSettings> GetEngineAsync(CancellationToken ct = default)
    {
        var current = connections.Read();
        if (string.IsNullOrWhiteSpace(current.ApiKey)) throw new EngineException("尚未配置管理引擎。请运行 Linux 一键安装／环境准备脚本，或在连接设置中连接已有引擎。");
        if (!Uri.TryCreate(current.BaseUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme is not ("http" or "https"))
            throw new EngineException("新建向导目前支持 Linux 同机 MCSManager，面板地址应为回环地址。");
        var nodes = await engine.GetNodesAsync(current, ct);
        var node = nodes.FirstOrDefault(item => item.Id == current.DaemonId && item.Available)
            ?? nodes.FirstOrDefault(item => item.Available && IsLocalHost(item.Host));
        if (node is null || !IsLocalHost(node.Host)) throw new EngineException("未找到已连接的本机节点，请检查 MCSManager Daemon 服务。");
        return current with { DaemonId = node.Id, DemoMode = false };
    }

    public async Task<CreatedServer> CreateAsync(CreateServerOptions options, Func<string, Task> progress, CancellationToken ct = default)
    {
        Validate(options);
        if (!await gate.WaitAsync(0, ct)) throw new EngineException("已有一个创建任务正在进行，请等待完成。");
        string? instanceId = null;
        try
        {
            if (await ReadCheckpointAsync(ct) is not null) throw new EngineException("已有待首次启动的实例，请刷新向导并继续该进度。");
            await progress("检查本机引擎、内存、磁盘与端口…");
            var connection = await GetEngineAsync(ct);
            await EnsureResourcesAsync(options.Port, options.MemoryMiB, ct);
            var instances = await engine.GetInstancesAsync(connection, ct);
            if (instances.Any(item => item.Port == options.Port))
                throw new EngineException($"已有实例使用 {options.Port} 作为游戏端口，请选择其他端口，以免启动时冲突。");
            await progress("获取所选版本的稳定构建…");
            var build = await catalog.GetBuildAsync(options.Version, ct);
            await progress($"下载 Paper {build.Version} / 构建 {build.Number}，并校验 SHA-256…");
            var file = await catalog.DownloadAsync(build, ct);
            await EnsureResourcesAsync(options.Port, options.MemoryMiB, ct);
            await progress("由 MCSManager 创建独立目录与实例…");
            instanceId = await engine.CreateInstanceAsync(connection, new
            {
                nickname = options.Name.Trim(), cwd = "", type = "minecraft/java", processType = "general",
                startCommand = $"{HostInspector.JavaExecutable} -Xms128M -Xmx{options.MemoryMiB}M -jar server.jar --nogui",
                stopCommand = "stop", stopTimeout = 90, ie = "utf-8", oe = "utf-8",
                terminalOption = new { haveColor = false, pty = false, ptyWindowCol = 120, ptyWindowRow = 30 },
                eventTask = new { autoStart = false, autoRestart = true, autoRestartMaxTimes = 3, ignore = false },
                pingConfig = new { ip = "127.0.0.1", port = options.Port, type = 1 }
            }, ct);
            var target = connection with { InstanceId = instanceId };
            await progress("上传已校验的服务端文件…");
            await engine.UploadJarAsync(target, file, ct);
            await progress("写入端口、玩家模式和已确认的 EULA…");
            await engine.WriteFileAsync(target, "/server.properties", Properties(options), ct);
            var whitelist = options.OfflineMode ? PlayerAccessService.ParseNames(options.PlayerName).Select(PlayerAccessService.OfflinePlayer).ToArray() : [];
            await engine.WriteFileAsync(target, "/whitelist.json", JsonSerializer.Serialize(whitelist), ct);
            await engine.WriteFileAsync(target, "/eula.txt", "# Explicitly accepted in CraftHome creation wizard\neula=true\n", ct);
            var result = new CreatedServer(instanceId, options.Name.Trim(), Address(options.PublicHost, options.Port), options.Port, options.MemoryMiB, build.Version, build.Number, options.OfflineMode);
            var stagedCheckpoint = checkpointPath + ".tmp";
            try
            {
                await File.WriteAllTextAsync(stagedCheckpoint, JsonSerializer.Serialize(new FirstStartCheckpoint(connection.BaseUrl, connection.DaemonId, result)), CancellationToken.None);
                File.Move(stagedCheckpoint, checkpointPath, overwrite: true);
            }
            catch (IOException) { throw new EngineException("实例已创建，但面板无法保存首次启动进度。"); }
            await progress("创建完成。点击“首次启动并管理”后才会启动游戏；原实例已保留。");
            return result;
        }
        catch (EngineException ex) when (instanceId is not null)
        {
            throw new EngineException($"{ex.Message} 新实例 {instanceId} 已保留，未删除文件；可在已有实例列表检查。原实例没有切换。");
        }
        finally { gate.Release(); }
    }

    public async Task StartAsync(CreatedServer server, Func<string, Task> progress, CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct)) throw new EngineException("已有创建或首次启动任务正在进行。");
        try
        {
            var connection = await GetEngineAsync(ct);
            var checkpoint = await ReadCheckpointAsync(ct);
            if (checkpoint is null || checkpoint.Server != server)
                throw new EngineException("首次启动进度已变化，请刷新向导。");
            if (checkpoint.BaseUrl != connection.BaseUrl || checkpoint.DaemonId != connection.DaemonId)
                throw new EngineException("这个实例属于原管理节点。请切回原节点，或结束向导并保留实例。");
            var target = connection with { InstanceId = server.InstanceId, Name = server.Name, Address = server.Address };
            var snapshot = await engine.GetSnapshotAsync(target, ct);
            if (snapshot.Status == 0)
            {
                if (snapshot.FileTasks > 0) throw new EngineException("服务器还有文件任务正在运行，请等待完成再启动。");
                await EnsureResourcesAsync(server.Port, server.MemoryMiB, ct);
                await progress("正在启动 Java。首次启动可能下载依赖并生成地图…");
                await engine.ControlAsync(target, "start", ct);
            }
            if (connections.Read() != target) await connections.SaveAsync(target, CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            try
            {
                while (true)
                {
                    if (snapshot.Status == 3 && snapshot.GameResponding)
                    {
                        File.Delete(checkpointPath);
                        await progress("游戏已响应。接下来请从另一网络验证玩家连接地址。");
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                    try { snapshot = await engine.GetSnapshotAsync(target, timeout.Token); }
                    catch (EngineException ex) when (ex.IsTransient)
                    {
                        await progress("管理引擎暂时未响应，正在等待游戏就绪…");
                        continue;
                    }
                    if (snapshot.Status == 0) throw new EngineException("游戏在准备完成前退出。已切换到新实例，请到服务器页面查看日志。");
                    if (snapshot.Status == 3 && !snapshot.GameResponding) await progress("游戏进程已启动，正在等待游戏响应…");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new EngineException("首次启动尚未确认就绪，实例可能仍在准备。请到服务器页面刷新并查看日志，暂勿重复创建。");
            }
        }
        finally { gate.Release(); }
    }

    private async Task EnsureResourcesAsync(int port, int memory, CancellationToken ct)
    {
        var host = await inspector.InspectAsync(port, memory, ct);
        if (host.Blockers.Length > 0) throw new EngineException(string.Join(" ", host.Blockers));
    }

    public static void Validate(CreateServerOptions options)
    {
        if (!options.AcceptEula) throw new ArgumentException("请先阅读并确认 Minecraft EULA。");
        if (string.IsNullOrWhiteSpace(options.Name) || options.Name.Length > 80 || options.Name.Any(char.IsControl)) throw new ArgumentException("服务器名称需为 1–80 个字符，不能包含换行或控制字符。");
        if (!PaperCatalog.IsSupportedVersion(options.Version)) throw new ArgumentException("请选择支持的 Paper 1.21.x 版本。");
        if (options.Port is < 1024 or > 65535) throw new ArgumentException("游戏端口需在 1024–65535 之间。");
        if (options.MemoryMiB is < 512 or > 16384 || options.MemoryMiB % 128 != 0) throw new ArgumentException("内存需为 512–16384 MiB，且是 128 的倍数。");
        if (options.PublicHost.Length > 253 || Uri.CheckHostName(options.PublicHost) == UriHostNameType.Unknown || options.PublicHost.Any(char.IsWhiteSpace)) throw new ArgumentException("请填写不含端口的服务器公网 IP 或域名。");
        if (options.OfflineMode) PlayerAccessService.ParseNames(options.PlayerName);
    }

    public static string Address(string host, int port) => host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
    private static bool IsLocalHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);

    public static string OfflineUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        var value = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{value[..8]}-{value[8..12]}-{value[12..16]}-{value[16..20]}-{value[20..]}";
    }

    public static string Properties(CreateServerOptions options) =>
        $"server-ip=0.0.0.0\nserver-port={options.Port}\nonline-mode={(!options.OfflineMode).ToString().ToLowerInvariant()}\nenforce-secure-profile={(!options.OfflineMode).ToString().ToLowerInvariant()}\nwhite-list={options.OfflineMode.ToString().ToLowerInvariant()}\nenforce-whitelist={options.OfflineMode.ToString().ToLowerInvariant()}\nmax-players={(options.MemoryMiB < 1024 ? 2 : 8)}\nview-distance=3\nsimulation-distance=3\nenable-status=true\nlevel-name=world\nmotd=CraftHome Paper Server\nspawn-protection=16\npause-when-empty-seconds=-1\nenable-rcon=false\n";
}
