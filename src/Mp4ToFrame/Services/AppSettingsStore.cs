using System.Text.Json;

namespace Mp4ToFrame.Services;

public sealed class AppSettings
{
    public string WorkspaceRoot { get; set; } = "";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double TargetFps { get; set; } = 30;
    public bool MirrorHorizontally { get; set; }
    public string? RembgPath { get; set; }
    public string? FfmpegPath { get; set; }
    public int RembgModelIndex { get; set; }
    public string RembgCustomModelId { get; set; } = "";
    public bool RembgAlphaMatting { get; set; }
    public int RembgAlphaErode { get; set; } = 4;
    public bool RembgPostProcessMask { get; set; }
    public int SelectedVideoIndex { get; set; }
}

public static class AppSettingsStore
{
    static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mp4ToFrame",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings s)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // ignore
        }
    }
}
