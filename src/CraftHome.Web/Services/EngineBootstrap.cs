using System.Text.Json;

namespace CraftHome.Web.Services;

public static class EngineBootstrap
{
    public static async Task ImportAsync(IServiceProvider services)
    {
        var paths = services.GetRequiredService<RuntimePaths>();
        var file = Path.Combine(paths.DataDirectory, "engine-bootstrap.json");
        if (!File.Exists(file)) return;
        var store = services.GetRequiredService<ConnectionStore>();
        if (store.Read().ApiKey.Length == 0)
        {
            var input = JsonSerializer.Deserialize<BootstrapInput>(await File.ReadAllTextAsync(file))
                ?? throw new InvalidDataException("引擎初始化配置为空。");
            if (!Uri.TryCreate(input.BaseUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme is not ("http" or "https")
                || string.IsNullOrWhiteSpace(input.ApiKey) || string.IsNullOrWhiteSpace(input.DaemonId))
                throw new InvalidDataException("引擎初始化配置无效。");
            await store.SaveAsync(store.Read() with { BaseUrl = input.BaseUrl, ApiKey = input.ApiKey, DaemonId = input.DaemonId });
        }
        File.Delete(file);
    }
    private sealed record BootstrapInput(string BaseUrl, string ApiKey, string DaemonId);
}
