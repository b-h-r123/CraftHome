using System.IO.Compression;

namespace CraftHome.Web.Services;

internal static class WorldArchive
{
    public const long MaxUpload = 128L * 1024 * 1024;
    private const long MaxExpanded = 1024L * 1024 * 1024;

    public static async Task<(string[] Roots, long Bytes)> PrepareAsync(Stream input, string original, string output, string name, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[128 * 1024];
            await using (var file = File.Create(original))
                await CopyBoundedAsync(input, file, buffer, MaxUpload, ct);
            await using var source = new ZipArchive(File.OpenRead(original), ZipArchiveMode.Read);
            if (source.Entries.Count is 0 or > 20_000) throw new EngineException("ZIP 必须包含 1–20,000 个条目。");
            var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long declared = 0;
            foreach (var entry in source.Entries)
            {
                var path = entry.FullName.TrimEnd('/');
                var parts = path.Split('/');
                var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
                if (path.Length > 800 || parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.'))
                    || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)
                    || unixType is not (0 or 0x4000 or 0x8000) || (entry.ExternalAttributes & 0x400) != 0)
                    throw new EngineException("ZIP 包含不安全的路径、链接或特殊文件。");
                if (entry.FullName.EndsWith('/')) continue;
                if (entry.Length > MaxExpanded - declared) throw new EngineException("ZIP 解压后超过 1 GiB。");
                declared += entry.Length;
                if (!files.TryAdd(path, entry)) throw new EngineException("ZIP 内有重复路径或仅大小写不同的文件。");
            }

            var markers = files.Keys.Where(path => path == "level.dat" || path.EndsWith("/level.dat", StringComparison.Ordinal))
                .Select(path => path[..^"level.dat".Length].TrimEnd('/')).ToHashSet(StringComparer.Ordinal);
            var primary = markers.Where(path => !IsDimension(path, markers)).ToArray();
            if (primary.Length != 1) throw new EngineException("ZIP 必须包含一张可识别的 Java 地图（level.dat），支持 CraftHome 备份包。");
            var world = primary[0];
            var roots = new Dictionary<string, string>(StringComparer.Ordinal) { [world] = name };
            if (world.Length > 0)
                foreach (var suffix in new[] { "_nether", "_the_end" })
                    if (markers.Contains(world + suffix)) roots.Add(world + suffix, name + suffix);

            var marker = files[world.Length == 0 ? "level.dat" : world + "/level.dat"];
            using (var markerStream = marker.Open())
                if (markerStream.ReadByte() != 0x1f || markerStream.ReadByte() != 0x8b)
                    throw new EngineException("level.dat 不是可识别的 Minecraft Java 存档。");

            await using var normalized = new ZipArchive(File.Create(output), ZipArchiveMode.Create);
            foreach (var (path, entry) in files)
            {
                var root = roots.FirstOrDefault(pair => pair.Key.Length == 0 || path.StartsWith(pair.Key + "/", StringComparison.Ordinal));
                if (root.Value is null) continue;
                var relative = root.Key.Length == 0 ? path : path[(root.Key.Length + 1)..];
                await using var from = entry.Open();
                await using var to = normalized.CreateEntry(root.Value + "/" + relative, CompressionLevel.Fastest).Open();
                if (await CopyBoundedAsync(from, to, buffer, entry.Length, ct) != entry.Length)
                    throw new EngineException("ZIP 文件长度与目录记录不符。");
            }
            return (roots.Values.ToArray(), declared);
        }
        catch (InvalidDataException) { throw new EngineException("ZIP 无法读取，可能已损坏或使用了不支持的压缩方式。"); }
    }

    private static bool IsDimension(string path, HashSet<string> markers) =>
        path.EndsWith("_nether", StringComparison.Ordinal) && markers.Contains(path[..^7])
        || path.EndsWith("_the_end", StringComparison.Ordinal) && markers.Contains(path[..^8]);

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, byte[] buffer, long limit, CancellationToken ct)
    {
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (read > limit - total) throw new EngineException("ZIP 文件超过大小限制或目录记录不符。");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
        }
        return total;
    }
}
