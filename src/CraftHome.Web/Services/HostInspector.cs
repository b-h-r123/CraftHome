using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CraftHome.Web.Services;

public sealed record HostReport(string Platform, int JavaMajor, long AvailableMemoryMiB, long FreeDiskGiB, int SuggestedPort,
    string[] Blockers, string[] Warnings);

public interface IHostInspector
{
    Task<HostReport> InspectAsync(int port, int memoryMiB, CancellationToken ct = default);
}

public sealed class HostInspector(RuntimePaths paths) : IHostInspector
{
    private const string UbuntuJava21 = "/usr/lib/jvm/java-21-openjdk-amd64/bin/java";
    internal static string JavaExecutable => File.Exists(UbuntuJava21) ? UbuntuJava21 : "/usr/bin/java";

    public async Task<HostReport> InspectAsync(int port, int memoryMiB, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) return new(RuntimeInformation.OSDescription, 0, 0, 0, port,
            ["新建向导目前支持 Linux 同机部署。Windows 可以使用现有实例控制功能。"], []);
        var blockers = new List<string>();
        var warnings = new List<string>();
        var meminfo = await File.ReadAllLinesAsync("/proc/meminfo", ct);
        long ReadMemory(string name) => long.Parse(meminfo.Single(line => line.StartsWith(name + ":")).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) / 1024;
        var available = ReadMemory("MemAvailable");
        var total = ReadMemory("MemTotal");
        var disk = new DriveInfo(Path.GetPathRoot(paths.DataDirectory)!).AvailableFreeSpace / (1024L * 1024 * 1024);
        var occupied = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet();
        var suggested = 25565;
        while (occupied.Contains(suggested) && suggested < 65535) suggested++;
        if (occupied.Contains(port)) blockers.Add($"TCP {port} 已被程序占用，请选择其他端口。");
        if (available < memoryMiB + 256L) blockers.Add($"可用内存 {available} MiB 不足，至少需要所选堆内存加 256 MiB 余量。先停止不用的游戏实例或降低内存配置。");
        if (disk < 2) blockers.Add("可用磁盘不足 2 GiB，暂不创建服务器。");
        if (total < 2048 || memoryMiB < 1024) warnings.Add("当前属于低内存测试配置，不能据此保证多人或长期游玩的稳定性。");
        int java = 0;
        try
        {
            using var process = new Process { StartInfo = new(JavaExecutable, "-version") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false } };
            process.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            string text;
            try { text = await process.StandardError.ReadToEndAsync(timeout.Token); await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); throw; }
            var match = Regex.Match(text, "version \\\"(\\d+)");
            if (match.Success) java = int.Parse(match.Groups[1].Value);
        }
        catch (System.ComponentModel.Win32Exception) { }
        if (java < 21) blockers.Add("未检测到可用的 Java 21 或更高版本。请运行 Linux 环境准备脚本后重试。");
        return new(RuntimeInformation.OSDescription, java, available, disk, suggested, blockers.ToArray(), warnings.ToArray());
    }
}
