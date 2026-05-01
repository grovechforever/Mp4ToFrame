using System.Text.Json;

namespace Mp4ToFrame.Services;

public sealed class AppSettings
{
    public string WorkspaceRoot { get; set; } = "";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double TargetFps { get; set; } = 30;
    public bool MirrorHorizontally { get; set; }
    /// <summary>左/右/上/下：正=向内裁切像素，负=向外扩展像素（填充见 BorderFillArgb）。</summary>
    public int BorderLeft { get; set; }
    public int BorderRight { get; set; }
    public int BorderTop { get; set; }
    public int BorderBottom { get; set; }
    /// <summary>外扩区域填充色 ARGB；A=0 为透明（默认 0）。</summary>
    public uint BorderFillArgb { get; set; }
    public string? RembgPath { get; set; }
    public string? FfmpegPath { get; set; }
    public int RembgModelIndex { get; set; }
    public string RembgCustomModelId { get; set; } = "";
    public bool RembgAlphaMatting { get; set; }
    public int RembgAlphaErode { get; set; } = 4;
    public bool RembgPostProcessMask { get; set; }
    /// <summary>rembg 并行进程数（1–8），大于 1 时多进程分批抠图。</summary>
    public int RembgParallelJobs { get; set; } = 1;
    /// <summary>Matted → 最终导出的目标宽高（FFmpeg 等比缩放 + 透明边）。</summary>
    public int FinalExportWidth { get; set; } = 1920;
    public int FinalExportHeight { get; set; } = 1080;
    /// <summary>最终导出目录；空则使用工作路径下的 Final 文件夹。</summary>
    public string FinalExportOutputFolder { get; set; } = "";
    /// <summary>最终导出 Border（与视频抽帧 Border 语义相同）。</summary>
    public int FinalBorderLeft { get; set; }
    public int FinalBorderRight { get; set; }
    public int FinalBorderTop { get; set; }
    public int FinalBorderBottom { get; set; }
    public uint FinalBorderFillArgb { get; set; }
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
