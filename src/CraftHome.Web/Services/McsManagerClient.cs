using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace CraftHome.Web.Services;

public sealed class EngineException(string message, bool isTransient = false) : Exception(message)
{
    public bool IsTransient { get; } = isTransient;
}

public sealed record InstanceSnapshot(int Status, long? MemoryBytes, bool GameResponding, int FileTasks = 0, string? StartCommand = null)
{
    public string StatusText => Status switch
    {
        -1 => "忙碌中", 0 => "已停止", 1 => "正在停止", 2 => "正在启动", 3 => "进程运行中", _ => "未知状态"
    };
}

public sealed record EngineNode(string Id, string Host, bool Available);
public sealed record EngineInstance(string Id, string Name, int Status, int Port);
public sealed record EngineFile(string Name, bool Directory, long Bytes);
public sealed record EngineFiles(string AbsolutePath, int Total, EngineFile[] Items);

public sealed class McsManagerClient(HttpClient http, ILogger<McsManagerClient>? logger = null) : IDisposable
{
    private static readonly Regex TerminalEscapes = new(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B[=<>]|\x08", RegexOptions.NonBacktracking);
    // MCSManager 10.22 permits eight low-level requests per session per second.
    private readonly TokenBucketRateLimiter requestPace = new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 1, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromMilliseconds(150),
        AutoReplenishment = true, QueueLimit = 16, QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    });

    public void Dispose() => requestPace.Dispose();

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
        var fileTasks = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("fileLock", out var fileLock)
            && fileLock.ValueKind == JsonValueKind.Number && fileLock.TryGetInt32(out var tasks) ? tasks : 0;
        return new(code, memory, responding, fileTasks, data.TryGetProperty("config", out var config) ? Text(config, "startCommand") : null);
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

    public async Task<EngineNode[]> GetNodesAsync(ConnectionSettings settings, CancellationToken ct = default)
    {
        using var response = await SendAsync(settings, "api/service/remote_services_list", HttpMethod.Get, null, ct);
        var data = response.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array) throw new EngineException("管理引擎返回的节点列表格式不正确。");
        return data.EnumerateArray().Select(item => new EngineNode(Text(item, "uuid"), Text(item, "ip"),
            item.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.True)).ToArray();
    }

    public async Task<EngineInstance[]> GetInstancesAsync(ConnectionSettings settings, CancellationToken ct = default)
    {
        using var response = await SendAsync(settings, "api/service/remote_service_instances", HttpMethod.Get,
            new() { ["page"] = "1", ["page_size"] = "50", ["instance_name"] = "", ["status"] = "", ["tag"] = "[]" }, ct);
        var data = response.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new EngineException("管理引擎返回的实例列表格式不正确。");
        return items.EnumerateArray().Select(item =>
        {
            var config = item.GetProperty("config");
            var port = config.TryGetProperty("pingConfig", out var ping) && ping.TryGetProperty("port", out var value) ? value.GetInt32() : 0;
            return new EngineInstance(Text(item, "instanceUuid"), Text(config, "nickname"), item.GetProperty("status").GetInt32(), port);
        }).ToArray();
    }

    public async Task<string> CreateInstanceAsync(ConnectionSettings settings, object config, CancellationToken ct = default)
    {
        using var response = await SendAsync(settings, "api/instance", HttpMethod.Post, null, ct, config);
        var id = Text(response.RootElement.GetProperty("data"), "instanceUuid");
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new EngineException("创建响应没有有效的实例 ID，请在 MCSManager 核对，暂勿重复创建。");
        return id;
    }

    public async Task WriteFileAsync(ConnectionSettings settings, string target, string text, CancellationToken ct = default)
    {
        using var created = await SendAsync(settings, "api/files/touch", HttpMethod.Post, null, ct, new { target });
        await EditFileAsync(settings, target, text, ct);
    }

    public async Task EditFileAsync(ConnectionSettings settings, string target, string text, CancellationToken ct = default)
    {
        using var response = await SendAsync(settings, "api/files/", HttpMethod.Put, null, ct, new { target, text });
    }

    public async Task<string> ReadFileAsync(ConnectionSettings settings, string target, CancellationToken ct = default)
    {
        using var response = await SendAsync(settings, "api/files/", HttpMethod.Put, null, ct, new { target });
        var data = response.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.String) throw new EngineException("管理引擎未返回有效的文件内容。");
        return data.GetString()!;
    }

    public async Task<EngineFiles> ListFilesAsync(ConnectionSettings settings, string target, int page, string filter = "", CancellationToken ct = default)
    {
        using var response = await SendAsync(settings, "api/files/list", HttpMethod.Get,
            new() { ["target"] = target, ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture), ["page_size"] = "100", ["file_name"] = filter }, ct);
        var data = response.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
            || !data.TryGetProperty("total", out var total) || total.ValueKind != JsonValueKind.Number || !total.TryGetInt32(out var count))
            throw new EngineException("管理引擎返回的文件列表格式不正确。");
        return new(Text(data, "absolutePath"), count, items.EnumerateArray().Select(item => new EngineFile(Text(item, "name"),
            item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var code) && code == 0,
            item.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes) ? bytes : 0)).ToArray());
    }

    public async Task MakeDirectoryAsync(ConnectionSettings settings, string target, CancellationToken ct)
    {
        using var response = await SendAsync(settings, "api/files/mkdir", HttpMethod.Post, null, ct, new { target });
    }

    public async Task CompressAsync(ConnectionSettings settings, string archive, string[] paths, CancellationToken ct)
    {
        using var response = await SendAsync(settings, "api/files/compress", HttpMethod.Post, null, ct,
            new { source = archive, targets = paths, type = 1, code = "utf-8" }, TimeSpan.FromMinutes(10));
        if (response.RootElement.GetProperty("data").ValueKind != JsonValueKind.True) throw new EngineException("管理引擎未确认压缩完成。");
    }

    public async Task MoveFileAsync(ConnectionSettings settings, string source, string destination, CancellationToken ct)
    {
        using var response = await SendAsync(settings, "api/files/move", HttpMethod.Put, null, ct, new { targets = new[] { new[] { source, destination } } });
        if (response.RootElement.GetProperty("data").ValueKind == JsonValueKind.False)
            throw new EngineException("管理引擎未确认文件移动。");
    }

    public async Task ExtractZipAsync(ConnectionSettings settings, string archive, string destination, CancellationToken ct)
    {
        using var response = await SendAsync(settings, "api/files/compress", HttpMethod.Post, null, ct,
            new { source = archive, targets = destination, type = 2, code = "utf-8" }, TimeSpan.FromMinutes(10));
        if (response.RootElement.GetProperty("data").ValueKind != JsonValueKind.True) throw new EngineException("管理引擎未确认解压完成。");
    }

    public async Task DeleteFileAsync(ConnectionSettings settings, string path, CancellationToken ct)
    {
        using var response = await SendAsync(settings, "api/files", HttpMethod.Delete, null, ct, new { targets = new[] { path } }, TimeSpan.FromMinutes(3));
    }

    public async Task ReloadWhitelistAsync(ConnectionSettings settings, CancellationToken ct)
    {
        using var response = await SendAsync(settings, "api/protected_instance/command", HttpMethod.Post, new() { ["command"] = "whitelist reload" }, ct);
        if (response.RootElement.GetProperty("data").ValueKind == JsonValueKind.False)
            throw new EngineException("游戏进程未接收白名单重载命令。");
    }

    public Task UploadJarAsync(ConnectionSettings settings, string file, CancellationToken ct = default) =>
        UploadFileAsync(settings, file, ".", "server.jar", ct);

    public async Task UploadFileAsync(ConnectionSettings settings, string file, string directory, string name, CancellationToken ct)
    {
        using var passport = await SendAsync(settings, "api/files/upload", HttpMethod.Get, new() { ["upload_dir"] = directory }, ct);
        var data = passport.RootElement.GetProperty("data");
        var address = Text(data, "addr");
        if (!address.StartsWith("http://") && !address.StartsWith("https://")) address = "http://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new EngineException("文件上传需要同机管理节点，上传地址必须为回环地址。");
        var token = Text(data, "password");
        if (string.IsNullOrWhiteSpace(token)) throw new EngineException("管理引擎未提供文件上传凭据。");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "upload/" + Uri.EscapeDataString(token)));
        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(file);
        var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", name);
        request.Content = form;
        request.Headers.ExpectContinue = false;
        request.Headers.UserAgent.ParseAdd("CraftHome/0.2.0");
        try
        {
            using var transfer = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
                { Timeout = TimeSpan.FromMinutes(5), MaxResponseContentBufferSize = 1024 * 1024 };
            using var response = await transfer.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode || (await response.Content.ReadAsStringAsync(ct)).Trim() != "OK")
                throw new EngineException("管理引擎未确认文件上传成功。");
        }
        catch (HttpRequestException ex)
        {
            var detail = (ex.Message + " / " + ex.GetBaseException().Message).Replace(token, "[redacted]").Replace(Uri.EscapeDataString(token), "[redacted]").Replace(settings.ApiKey, "[redacted]");
            logger?.LogWarning("File upload transport failed ({Reason}): {Detail}", ex.HttpRequestError, detail);
            throw new EngineException("无法上传到本机管理节点。");
        }
    }

    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";

    private async Task<JsonDocument> SendAsync(ConnectionSettings settings, string route, HttpMethod method,
        Dictionary<string, string?>? extra, CancellationToken ct, object? body = null, TimeSpan? requestTimeout = null)
    {
        using var slot = await requestPace.AcquireAsync(1, ct);
        if (!slot.IsAcquired) throw new EngineException("管理引擎请求过多，请稍后重试。", isTransient: true);
        var query = extra ?? new Dictionary<string, string?>();
        query["daemonId"] = settings.DaemonId;
        query["uuid"] = settings.InstanceId;
        var address = QueryHelpers.AddQueryString(settings.BaseUrl.TrimEnd('/') + "/" + route, query);
        using var request = new HttpRequestMessage(method, address);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("x-request-api-key", settings.ApiKey);
        request.Content = new StringContent(body is null ? "{}" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(15));
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new EngineException("管理引擎拒绝访问，请检查 API Key 和实例权限。");
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("Management engine returned HTTP {Status} for {Method} {Route}", (int)response.StatusCode, method, route);
                throw new EngineException($"管理引擎返回 HTTP {(int)response.StatusCode}，请检查连接地址和引擎状态。");
            }
            var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
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
        catch (HttpRequestException) { throw new EngineException("无法连接管理引擎，请检查面板地址、SSH 隧道或服务状态。", isTransient: true); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new EngineException("管理引擎请求超时，请稍后刷新状态。", isTransient: true); }
        catch (JsonException) { throw new EngineException("管理引擎返回了无效 JSON，请检查地址是否指向面板 API。"); }
    }
}
