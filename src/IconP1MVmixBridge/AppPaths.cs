namespace IconP1MVmixBridge;

internal static class AppPaths
{
    public static string AppDataDirectory
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IconP1MVmixBridge");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string LogDirectory
    {
        get
        {
            var path = Path.Combine(AppDataDirectory, "logs");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string ConfigFile => Path.Combine(AppDataDirectory, "profile.json");
}
