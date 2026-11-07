using System.Security.Cryptography;
using System.Text.Json;

namespace CraftHome.Web.Services;

public sealed record PaperBuild(string Version, int Number, string Url, string Sha256, long Size);

public sealed class PaperCatalog(HttpClient http, RuntimePaths paths) : IDisposable
{
    private const string Api = "https://fill.papermc.io/v3/projects/paper";
    private const long MaxJarBytes = 128 * 1024 * 1024;

    public async Task<string[]> GetVersionsAsync(CancellationToken ct = default)
    {
        using var data = await GetJsonAsync(Api, ct);
        return data.RootElement.GetProperty("versions").EnumerateObject()
            .SelectMany(group => group.Value.EnumerateArray().Select(item => item.GetString() ?? ""))
            .Where(IsSupportedVersion).Distinct().OrderByDescending(value => Version.Parse(value)).ToArray();
    }

    public static bool IsSupportedVersion(string value) => Version.TryParse(value, out var version)
        && version.Major == 1 && version.Minor == 21 && value.All(c => char.IsAsciiDigit(c) || c == '.');

    public async Task<PaperBuild> GetBuildAsync(string version, CancellationToken ct = default)
    {
        if (!IsSupportedVersion(version)) throw new ArgumentException("当前向导支持 Paper 1.21.x（Java 21）。");
        using var data = await GetJsonAsync($"{Api}/versions/{version}/builds", ct);
        foreach (var build in data.RootElement.EnumerateArray().OrderByDescending(item => item.GetProperty("id").GetInt32()))
        {
            if (build.GetProperty("channel").GetString() != "STABLE") continue;
            var download = build.GetProperty("downloads").GetProperty("server:default");
            var result = new PaperBuild(version, build.GetProperty("id").GetInt32(), download.GetProperty("url").GetString() ?? "",
                download.GetProperty("checksums").GetProperty("sha256").GetString() ?? "", download.GetProperty("size").GetInt64());
            if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.Host != "fill-data.papermc.io" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || result.Sha256.Length != 64 || !result.Sha256.All(Uri.IsHexDigit) || result.Size is <= 0 or > MaxJarBytes)
                throw new EngineException("Paper 官方下载元数据格式不受支持。");
            return result;
        }
        throw new EngineException("所选版本没有稳定构建，请选择其他版本。");
    }

    public async Task<string> DownloadAsync(PaperBuild build, CancellationToken ct = default)
    {
        var directory = Path.Combine(paths.DataDirectory, "downloads");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, build.Sha256.ToLowerInvariant() + ".jar");
        if (File.Exists(target) && await HasExpectedHashAsync(target, build.Sha256, ct)) return target;
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var request = Request(build.Url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(temporary))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += read;
                    if (total > build.Size || total > MaxJarBytes) throw new EngineException("Paper 下载超过预期大小，已中止。");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total != build.Size) throw new EngineException("Paper 下载不完整，请重试。");
            }
            if (!await HasExpectedHashAsync(temporary, build.Sha256, ct)) throw new EngineException("Paper SHA-256 校验失败，没有创建或启动服务器。");
            File.Move(temporary, target, true);
            return target;
        }
        catch (HttpRequestException) { throw new EngineException("无法下载 Paper，请检查服务器到 Paper 官方下载源的网络。"); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            using var request = Request(url);
            using var response = await http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (HttpRequestException) { throw new EngineException("无法获取 Paper 官方版本列表，请检查网络后重试。"); }
        catch (JsonException) { throw new EngineException("Paper 官方版本接口返回了无效内容。"); }
    }

    private static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CraftHome/0.2.0 (+https://github.com/b-h-r123/CraftHome)");
        return request;
    }

    private static async Task<bool> HasExpectedHashAsync(string path, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => http.Dispose();
}
