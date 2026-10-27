using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace CraftHome.Web.Services;

public sealed record ConnectionSettings(
    bool DemoMode = true,
    string BaseUrl = "http://127.0.0.1:23333",
    string ApiKey = "",
    string DaemonId = "",
    string InstanceId = "",
    string Name = "我的生存服务器",
    string Address = "localhost:25565");

public sealed class ConnectionStore
{
    private readonly string path;
    private readonly IDataProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ConnectionSettings current;

    public ConnectionStore(RuntimePaths paths, IDataProtectionProvider protection)
    {
        path = Path.Combine(paths.DataDirectory, "connection.json");
        protector = protection.CreateProtector("CraftHome.Connection.v1");
        if (!File.Exists(path))
        {
            current = new();
            return;
        }
        var saved = JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("连接配置为空。请恢复 connection.json 和 keys 目录。");
        current = saved with { ApiKey = saved.ApiKey.Length == 0 ? "" : protector.Unprotect(saved.ApiKey) };
    }

    public ConnectionSettings Read() => Volatile.Read(ref current);

    public async Task SaveAsync(ConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Name) || settings.Name.Length > 80)
            throw new ArgumentException("请填写不超过 80 字的服务器名称。");
        if (!settings.DemoMode)
        {
            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var url)
                || (url.Scheme != "http" && url.Scheme != "https")
                || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
                throw new ArgumentException("面板地址必须是 http/https 地址，不包含密码、查询参数或 #。");
            if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.DaemonId)
                || string.IsNullOrWhiteSpace(settings.InstanceId))
                throw new ArgumentException("真实连接需要 API Key、节点 ID 和实例 ID。");
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            var saved = settings with { ApiKey = settings.ApiKey.Length == 0 ? "" : protector.Protect(settings.ApiKey) };
            var temporaryPath = path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
            Volatile.Write(ref current, settings);
        }
        finally { gate.Release(); }
    }
}
