using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CraftHome.Web.Services;

public sealed record WhitelistPlayer([property: JsonPropertyName("uuid")] string Uuid, [property: JsonPropertyName("name")] string Name);
public sealed record PlayerAccessReport(string Selection, string ServerName, int Status, bool Offline, bool WhitelistEnabled,
    WhitelistPlayer[] Players, AuthMeState AuthMe, int FileTasks);

public sealed class PlayerAccessService(ConnectionStore connections, McsManagerClient engine, InstanceOperations operations)
{
    private const string Whitelist = "/whitelist.json";
    private static readonly Regex NamePattern = new(@"\A[A-Za-z0-9_]{3,16}\z", RegexOptions.NonBacktracking);

    public static string[] ParseNames(string text)
    {
        var names = Regex.Split(text.Trim(), @"[\s,，、;；]+").Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length is < 1 or > 100 || names.Any(name => !NamePattern.IsMatch(name)))
            throw new ArgumentException("请填写 1–100 个玩家名，每个使用 3–16 位英文字母、数字或下划线。可用逗号、空格或换行分隔。");
        return names;
    }

    public static WhitelistPlayer OfflinePlayer(string name) => new(ServerProvisioner.OfflineUuid(name), name);

    public async Task<PlayerAccessReport?> InspectAsync(CancellationToken ct = default)
    {
        var target = connections.Read();
        if (target.DemoMode || target.InstanceId.Length == 0) return null;
        var state = await ReadAsync(target, ct);
        var authMe = await AuthMeInstaller.InspectAsync(engine, target, state.HasPlugins, ct);
        return new(Selection(target), target.Name, state.Snapshot.Status, state.Offline, state.WhitelistEnabled, state.Players, authMe, state.Snapshot.FileTasks);
    }

    public Task<string> AddAsync(string selection, string names, CancellationToken ct = default)
    {
        var additions = ParseNames(names);
        return EditAsync(selection, current => current.Concat(additions.Where(name => !current.Any(player =>
            player.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).Select(OfflinePlayer)).ToArray(), ct);
    }

    public Task<string> RemoveAsync(string selection, string uuid, CancellationToken ct = default) =>
        EditAsync(selection, current => current.Where(player => player.Uuid != uuid).ToArray(), ct);

    private async Task<string> EditAsync(string selection, Func<WhitelistPlayer[], WhitelistPlayer[]> update, CancellationToken ct)
    {
        if (!await operations.Gate.WaitAsync(0, ct)) throw new EngineException("另一个服务器操作正在进行，请稍后重试。");
        try
        {
            var target = GetTarget(selection);
            var state = await ReadAsync(target, ct);
            if (!state.Offline || !state.WhitelistEnabled) throw new EngineException("当前服务器需要启用离线模式和白名单，才能在这里编辑离线玩家。");
            if (state.Snapshot.Status is not (0 or 3) || state.Snapshot.FileTasks > 0)
                throw new EngineException("服务器正在切换状态或处理文件，请稍后重试。");
            var next = update(state.Players);
            if (next.SequenceEqual(state.Players)) return "白名单没有变化。";
            var json = JsonSerializer.Serialize(next);
            if (state.HasWhitelist) await engine.EditFileAsync(target, Whitelist, json, ct);
            else await engine.WriteFileAsync(target, Whitelist, json, ct);
            if (state.Snapshot.Status == 0) return "白名单已保存，下次启动时读取。";
            try { await engine.ReloadWhitelistAsync(target, ct); }
            catch (EngineException) { return "白名单已保存。游戏未确认接收重载命令，请在面板重新加载或正常重启。"; }
            return "白名单已保存，已向运行中的游戏发送重载命令。";
        }
        finally { operations.Gate.Release(); }
    }

    internal ConnectionSettings GetTarget(string selection)
    {
        var target = connections.Read();
        if (target.DemoMode || target.InstanceId.Length == 0 || Selection(target) != selection)
            throw new EngineException("当前管理的服务器已改变，请刷新页面。");
        return target;
    }

    private static string Selection(ConnectionSettings target) => target.DaemonId + "/" + target.InstanceId;

    internal async Task<AccessState> ReadAsync(ConnectionSettings target, CancellationToken ct)
    {
        var snapshot = await engine.GetSnapshotAsync(target, ct);
        var properties = await engine.ReadFileAsync(target, "/server.properties", ct);
        var whitelist = (await engine.ListFilesAsync(target, "/", 0, "whitelist.json", ct)).Items.Any(file => !file.Directory && file.Name == "whitelist.json");
        var plugins = (await engine.ListFilesAsync(target, "/", 0, "plugins", ct)).Items.Any(file => file.Directory && file.Name == "plugins");
        WhitelistPlayer[] players = [];
        if (whitelist)
        {
            try
            {
                players = JsonSerializer.Deserialize<WhitelistPlayer[]>(await engine.ReadFileAsync(target, Whitelist, ct))
                    ?? throw new JsonException();
                if (players.Any(player => player is null || string.IsNullOrWhiteSpace(player.Name) || !Guid.TryParse(player.Uuid, out _))) throw new JsonException();
            }
            catch (JsonException) { throw new EngineException("白名单格式异常，请先在 MCSManager 检查 whitelist.json。"); }
        }
        return new(snapshot, !ReadFlag(properties, "online-mode", true), ReadFlag(properties, "white-list", false), players, whitelist, plugins);
    }

    private static bool ReadFlag(string properties, string name, bool defaultValue)
    {
        var matches = Regex.Matches(properties, $@"(?m)^[ \t]*{Regex.Escape(name)}[ \t]*[=:][ \t]*(?<value>[^\r\n]*)");
        if (matches.Count == 0) return defaultValue;
        return bool.TryParse(matches[^1].Groups["value"].Value.Trim(), out var result) ? result
            : throw new EngineException("服务器的账号验证配置格式异常，请检查 server.properties。");
    }

    internal sealed record AccessState(InstanceSnapshot Snapshot, bool Offline, bool WhitelistEnabled, WhitelistPlayer[] Players, bool HasWhitelist, bool HasPlugins);
}
