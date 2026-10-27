namespace CraftHome.Web.Services;

public sealed class RuntimePaths
{
    public string DataDirectory { get; }

    public RuntimePaths(IConfiguration configuration, IWebHostEnvironment environment)
    {
        DataDirectory = Path.GetFullPath(configuration["CraftHome:DataDirectory"]
            ?? Path.Combine(environment.ContentRootPath, ".local"));
        Directory.CreateDirectory(DataDirectory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
