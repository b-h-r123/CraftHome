using System.Security.Cryptography;

namespace CraftHome.Web.Services;

public enum AuthMeState { NotInstalled, Installed, Archived, External }

public sealed class AuthMeInstaller(PlayerAccessService access, McsManagerClient engine, InstanceOperations operations, RuntimePaths paths)
{
    private const string FileName = "AuthMe-6.0.1-Paper.jar";
    private const string PluginPath = "/plugins/" + FileName;
    private const string ArchiveName = ".crafthome-disabled-" + FileName;
    private const string ArchivePath = "/" + ArchiveName;
    private static readonly string[] Downloads =
    [
        "https://cdn.modrinth.com/data/9js4IEHC/versions/fPuH4uYs/" + FileName,
        "https://github.com/AuthMe/AuthMeReloaded/releases/download/6.0.1/" + FileName
    ];
    private const string Sha512 = "832EB1570F1887493DC09BD183360C6C890AAFE24FBC635D36F7C3162C339F064E3A0E082519DEFE7E67569C1D78072F582CB49C14A24D125D4ECE463B765271";
    private const string Settings = "settings:\n  sessions:\n    enabled: false\n  restrictions:\n    maxRegPerIp: 0\n  registration:\n    enabled: true\n    force: true\n    type: PASSWORD\n";

    public async Task InstallAsync(string selection, CancellationToken ct = default)
    {
        if (!await operations.Gate.WaitAsync(0, ct)) throw new EngineException("另一个服务器操作正在进行，请稍后重试。");
        try
        {
            var target = access.GetTarget(selection);
            var state = await access.ReadAsync(target, ct);
            if (state.Snapshot.Status != 0 || state.Snapshot.FileTasks > 0)
                throw new EngineException("请先正常停服，再安装游戏内登录插件。");
            if (!state.Offline || !state.WhitelistEnabled || state.Players.Length == 0)
                throw new EngineException("安装 AuthMe 前，请启用离线模式和白名单，并至少添加一位玩家。");
            var versions = await engine.ListFilesAsync(target, "/", 0, "versions", ct);
            if (!versions.Items.Any(file => file.Directory && file.Name == "versions") ||
                !(await engine.ListFilesAsync(target, "/versions", 0, "1.21.11", ct)).Items.Any(file => file.Directory && file.Name == "1.21.11") ||
                !(await engine.ListFilesAsync(target, "/versions/1.21.11", 0, "paper-1.21.11.jar", ct)).Items.Any(file => !file.Directory && file.Name == "paper-1.21.11.jar"))
                throw new EngineException("此安装器适用于已经启动过的 Paper 1.21.11。请先核对服务端版本。");
            if (await InspectAsync(engine, target, state.HasPlugins, ct) != AuthMeState.NotInstalled)
                throw new EngineException("实例已有 AuthMe 或其配置，请刷新页面并检查现有安装。");

            var filePath = await GetVerifiedJarAsync(ct);

            var snapshot = await engine.GetSnapshotAsync(target, ct);
            if (snapshot.Status != 0 || snapshot.FileTasks > 0) throw new EngineException("服务器状态已变化，请停服后再安装。");
            if (!state.HasPlugins) await engine.MakeDirectoryAsync(target, "/plugins", ct);
            var stage = "/.crafthome-authme-" + Guid.NewGuid().ToString("N");
            await engine.MakeDirectoryAsync(target, stage, ct);
            await engine.MakeDirectoryAsync(target, stage + "/AuthMe", ct);
            await engine.WriteFileAsync(target, stage + "/AuthMe/config.yml", Settings, ct);
            await engine.UploadFileAsync(target, filePath, stage, FileName, ct);
            await engine.MoveFileAsync(target, stage + "/AuthMe", "/plugins/AuthMe", ct);
            try { await engine.MoveFileAsync(target, stage + "/" + FileName, PluginPath, ct); }
            catch (EngineException)
            {
                await engine.MoveFileAsync(target, "/plugins/AuthMe", stage + "/AuthMe", CancellationToken.None);
                throw;
            }
            try { await engine.DeleteFileAsync(target, stage, ct); }
            catch (EngineException) { /* The installed JAR has already been published. */ }
        }
        finally { operations.Gate.Release(); }
    }

    public async Task SetInstalledAsync(string selection, bool installed, CancellationToken ct = default)
    {
        if (!await operations.Gate.WaitAsync(0, ct)) throw new EngineException("另一个服务器操作正在进行，请稍后重试。");
        try
        {
            var target = access.GetTarget(selection);
            var state = await access.ReadAsync(target, ct);
            if (state.Snapshot.Status != 0 || state.Snapshot.FileTasks > 0)
                throw new EngineException("请先正常停服，并等待文件任务结束。");
            if (installed && (!state.Offline || !state.WhitelistEnabled || state.Players.Length == 0))
                throw new EngineException("恢复 AuthMe 前，请启用离线模式和白名单，并至少添加一位玩家。");
            var before = await InspectAsync(engine, target, state.HasPlugins, ct);
            var after = installed ? AuthMeState.Installed : AuthMeState.Archived;
            if (before == after) return;
            if (before != (installed ? AuthMeState.Archived : AuthMeState.Installed))
                throw new EngineException("AuthMe 文件状态已变化。请刷新，若仍无法管理，请在 MCSManager 检查。");
            if (installed && !state.HasPlugins) await engine.MakeDirectoryAsync(target, "/plugins", ct);
            var snapshot = await engine.GetSnapshotAsync(target, ct);
            if (snapshot.Status != 0 || snapshot.FileTasks > 0)
                throw new EngineException("服务器状态已变化，请停服后再操作 AuthMe。");
            await engine.MoveFileAsync(target, installed ? ArchivePath : PluginPath, installed ? PluginPath : ArchivePath, ct);
            if (await InspectAsync(engine, target, true, ct) != after)
                throw new EngineException("管理引擎未确认 AuthMe 的文件状态，请刷新并核对实例文件。");
        }
        finally { operations.Gate.Release(); }
    }

    internal static async Task<AuthMeState> InspectAsync(McsManagerClient engine, ConnectionSettings target, bool hasPlugins, CancellationToken ct)
    {
        var plugins = hasPlugins ? (await engine.ListFilesAsync(target, "/plugins", 0, "authme", ct)).Items : [];
        var jars = plugins.Where(file => !file.Directory && file.Name.StartsWith("AuthMe", StringComparison.OrdinalIgnoreCase)
            && file.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).ToArray();
        var archived = (await engine.ListFilesAsync(target, "/", 0, ArchiveName, ct)).Items.FirstOrDefault(file =>
            file.Name.Equals(ArchiveName, StringComparison.OrdinalIgnoreCase));
        if (plugins.Any(file => file.Directory && file.Name.Equals(FileName, StringComparison.OrdinalIgnoreCase)) ||
            archived is not null && (archived.Directory || archived.Name != ArchiveName || jars.Length > 0))
            return AuthMeState.External;
        if (archived is not null) return AuthMeState.Archived;
        if (jars.Length == 1 && jars[0].Name == FileName) return AuthMeState.Installed;
        if (jars.Length > 0 || plugins.Any(file => file.Name.Equals("AuthMe", StringComparison.OrdinalIgnoreCase)))
            return AuthMeState.External;
        return AuthMeState.NotInstalled;
    }

    private async Task<string> GetVerifiedJarAsync(CancellationToken ct)
    {
        var directory = Path.Combine(paths.DataDirectory, "downloads");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, FileName);
        if (File.Exists(file) && new FileInfo(file).Length == 1153596 && Verify(await File.ReadAllBytesAsync(file, ct))) return file;
        using var http = new HttpClient(new SocketsHttpHandler { MaxAutomaticRedirections = 3 })
        { Timeout = TimeSpan.FromSeconds(45), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CraftHome/0.4 (+https://github.com/b-h-r123/CraftHome)");
        foreach (var url in Downloads)
        {
            try
            {
                using var response = await http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (!Verify(bytes)) throw new EngineException("AuthMe 发行文件校验失败，安装已停止。");
                var temporary = file + ".tmp";
                try { await File.WriteAllBytesAsync(temporary, bytes, ct); File.Move(temporary, file, overwrite: true); }
                finally { File.Delete(temporary); }
                return file;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        throw new EngineException("无法下载 AuthMe，请检查服务器到 Modrinth 或 GitHub 的网络后重试。");
    }

    private static bool Verify(byte[] bytes) => bytes.Length == 1153596 && Convert.ToHexString(SHA512.HashData(bytes)) == Sha512;
}
