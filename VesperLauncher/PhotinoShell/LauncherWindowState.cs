using System.Text.Json;
using System.IO;

namespace VesperLauncher.PhotinoShell;

internal readonly record struct LauncherWindowBounds(int Width, int Height);

internal static class LauncherWindowState
{
    public const int DefaultWidth = 1400;
    public const int DefaultHeight = 880;
    public const int MinWidth = 960;
    public const int MinHeight = 640;
    private const int MaxWidth = 2600;
    private const int MaxHeight = 1800;

    public static LauncherWindowBounds Load()
    {
        try
        {
            var path = GetStatePath();
            if (!File.Exists(path))
            {
                return new LauncherWindowBounds(DefaultWidth, DefaultHeight);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var width = root.TryGetProperty("width", out var widthElement) && widthElement.TryGetInt32(out var savedWidth)
                ? savedWidth
                : DefaultWidth;
            var height = root.TryGetProperty("height", out var heightElement) && heightElement.TryGetInt32(out var savedHeight)
                ? savedHeight
                : DefaultHeight;

            return Normalize(width, height);
        }
        catch
        {
            return new LauncherWindowBounds(DefaultWidth, DefaultHeight);
        }
    }

    public static void Save(int width, int height)
    {
        try
        {
            var normalized = Normalize(width, height);
            var path = GetStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                width = normalized.Width,
                height = normalized.Height
            }));
        }
        catch
        {
        }
    }

    private static LauncherWindowBounds Normalize(int width, int height)
    {
        return new LauncherWindowBounds(
            Math.Clamp(width, MinWidth, MaxWidth),
            Math.Clamp(height, MinHeight, MaxHeight));
    }

    private static string GetStatePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = AppContext.BaseDirectory;
        }

        return Path.Combine(appData, "VesperLauncher", "window-state.json");
    }
}
