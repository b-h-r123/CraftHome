using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CraftHome.Web.Services;

public sealed record CreateServerOptions(string Name, string Version, int Port, int MemoryMiB, string PublicHost,
    bool OfflineMode, string PlayerName, bool AcceptEula);
public sealed record CreatedServer(string InstanceId, string Name, string Address, int Port, int MemoryMiB, string Version, int Build);

public sealed class ServerProvisioner(ConnectionStore connections, McsManagerClient engine, PaperCatalog catalog,
    IHostInspector inspector)
{
    private readonly SemaphoreSlim gate = new(1, 1);

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
                startCommand = $"/usr/bin/java -Xms128M -Xmx{options.MemoryMiB}M -jar server.jar --nogui",
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
            var whitelist = options.OfflineMode ? new[] { new { uuid = OfflineUuid(options.PlayerName), name = options.PlayerName } } : [];
            await engine.WriteFileAsync(target, "/whitelist.json", JsonSerializer.Serialize(whitelist), ct);
            await engine.WriteFileAsync(target, "/eula.txt", "# Explicitly accepted in CraftHome creation wizard\neula=true\n", ct);
            var result = new CreatedServer(instanceId, options.Name.Trim(), Address(options.PublicHost, options.Port), options.Port, options.MemoryMiB, build.Version, build.Number);
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
            var target = connection with { InstanceId = server.InstanceId, Name = server.Name, Address = server.Address };
            var snapshot = await engine.GetSnapshotAsync(target, ct);
            if (snapshot.Status == 0)
            {
                await EnsureResourcesAsync(server.Port, server.MemoryMiB, ct);
                await progress("启动 Java，首次启动可能下载依赖并生成世界，请保持页面打开…");
                await engine.ControlAsync(target, "start", ct);
            }
            await connections.SaveAsync(target, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                    try { snapshot = await engine.GetSnapshotAsync(target, timeout.Token); }
                    catch (EngineException ex) when (ex.IsTransient)
                    {
                        await progress("管理引擎暂时未响应，正在等待游戏就绪…");
                        continue;
                    }
                    if (snapshot.Status == 3 && snapshot.GameResponding) { await progress("游戏查询已响应，可以进入管理页面。"); return; }
                    if (snapshot.Status == 0) throw new EngineException("游戏在准备完成前退出。已切换到新实例，请到服务器页面查看日志。");
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
        if (options.OfflineMode && !Regex.IsMatch(options.PlayerName, "^[A-Za-z0-9_]{3,16}$")) throw new ArgumentException("离线模式需填写 3–16 位英文、数字或下划线玩家名，并加入白名单。");
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
