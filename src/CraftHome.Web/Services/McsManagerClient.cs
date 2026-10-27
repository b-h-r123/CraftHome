using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace CraftHome.Web.Services;

public sealed class EngineException(string message) : Exception(message);

public sealed record InstanceSnapshot(int Status, long? MemoryBytes, bool GameResponding)
{
    public string StatusText => Status switch
    {
        -1 => "忙碌中", 0 => "已停止", 1 => "正在停止", 2 => "正在启动", 3 => "进程运行中", _ => "未知状态"
    };
}

public sealed class McsManagerClient(HttpClient http)
{
    private static readonly Regex TerminalEscapes = new(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B[=<>]|\x08", RegexOptions.NonBacktracking);
    public async Task<InstanceSnapshot> GetSnapshotAsync(ConnectionSettings settings, CancellationToken ct = default)
    {
        using var response = await SendAsync(settings, "api/instance", HttpMethod.Get, null, ct);
        var data = response.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code))
            throw new EngineException("管理引擎返回的实例状态格式不正确，请核对兼容版本。");
        long? memory = null;
        if (data.TryGetProperty("processInfo", out var process) && process.ValueKind == JsonValueKind.Object
            && process.TryGetProperty("memory", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var bytes))
            memory = bytes;
        var responding = data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("mcPingOnline", out var ping) && ping.ValueKind == JsonValueKind.True;
        return new(code, memory, responding);
    }

    public async Task<string> GetLogsAsync(ConnectionSettings settings, CancellationToken ct = default)
    {
        using var response = await SendAsync(settings, "api/protected_instance/outputlog", HttpMethod.Get,
            new Dictionary<string, string?> { ["size"] = "16KB" }, ct);
        var data = response.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.String)
            throw new EngineException("管理引擎返回的日志格式不正确。");
        var logs = data.GetString() ?? "";
        if (logs.Length > 32768) logs = logs[^32768..];
        return TerminalEscapes.Replace(logs, "").Replace("\r", "");
    }

    public async Task ControlAsync(ConnectionSettings settings, string action, CancellationToken ct = default)
    {
        var route = action switch
        {
            "start" => "open", "stop" => "stop", "restart" => "restart",
            _ => throw new ArgumentException("不支持的操作。", nameof(action))
        };
        using var response = await SendAsync(settings, "api/protected_instance/" + route, HttpMethod.Post, null, ct);
    }

    private async Task<JsonDocument> SendAsync(ConnectionSettings settings, string route, HttpMethod method,
        Dictionary<string, string?>? extra, CancellationToken ct)
    {
        var query = extra ?? new Dictionary<string, string?>();
        query["daemonId"] = settings.DaemonId;
        query["uuid"] = settings.InstanceId;
        var address = QueryHelpers.AddQueryString(settings.BaseUrl.TrimEnd('/') + "/" + route, query);
        using var request = new HttpRequestMessage(method, address);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("x-request-api-key", settings.ApiKey);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new EngineException("管理引擎拒绝访问，请检查 API Key 和实例权限。");
            if (!response.IsSuccessStatusCode)
                throw new EngineException($"管理引擎返回 HTTP {(int)response.StatusCode}，请检查连接地址和引擎状态。");
            var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code))
            {
                document.Dispose();
                throw new EngineException("返回内容不是受支持的 MCSManager API 响应。");
            }
            if (code != 200)
            {
                document.Dispose();
                throw new EngineException(code == 403
                    ? "管理引擎拒绝访问，请检查 API Key 和实例权限。"
                    : $"管理引擎操作失败（业务状态 {code}），请检查节点、实例 ID 和引擎日志。");
            }
            if (!document.RootElement.TryGetProperty("data", out _))
            {
                document.Dispose();
                throw new EngineException("管理引擎响应缺少 data 字段。");
            }
            return document;
        }
        catch (HttpRequestException) { throw new EngineException("无法连接管理引擎，请检查面板地址、SSH 隧道或服务状态。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new EngineException("管理引擎请求超时，请稍后刷新状态。"); }
        catch (JsonException) { throw new EngineException("管理引擎返回了无效 JSON，请检查地址是否指向面板 API。"); }
    }
}
