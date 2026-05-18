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
    /// <summary>rembg -af；旧版 settings 未写入时反序列化为 0，加载时规范为 240。</summary>
    public int RembgAlphaFg { get; set; } = 240;
    /// <summary>rembg -ab；未写入时可能为 0，加载时规范为 10。</summary>
    public int RembgAlphaBg { get; set; } = 10;
    public bool RembgPostProcessMask { get; set; }
    /// <summary>rembg 并行进程数（1–8），大于 1 时多进程分批抠图。</summary>
    public int RembgParallelJobs { get; set; } = 1;
    /// <summary>0=rembg；1=纯色底色键（FFmpeg colorkey）。</summary>
    public int MatteMode { get; set; }
    /// <summary>色键键控色 RGB（仅低 24 位），默认 0xFFFFFF；绿幕可改为 0x00FF00 等。</summary>
    public uint WhiteKeyRgb { get; set; } = 0xFFFFFF;
    /// <summary>FFmpeg colorkey similarity，默认 0.08。</summary>
    public double WhiteKeySimilarity { get; set; } = 0.08;
    /// <summary>FFmpeg colorkey blend，默认 0.04。</summary>
    public double WhiteKeyBlend { get; set; } = 0.04;
    /// <summary>色键后 despill：0=自动（绿/蓝键控时去边）、1=强制绿幕、2=强制蓝幕、3=关闭。</summary>
    public int WhiteKeyDespillMode { get; set; }
    /// <summary>抠图输出到 Matted 时，在文件名（不含扩展名）末尾拼接的字符串；空则仍为 stem.png。</summary>
    public string MattedOutputStemSuffix { get; set; } = "";
    /// <summary>最终导出时，在输出 PNG 文件名（不含扩展名）末尾拼接的字符串；空则与 Matted 源 stem 一致。</summary>
    public string FinalOutputStemSuffix { get; set; } = "";
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
            var s = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            NormalizeRembgAlphaThresholds(s);
            NormalizeWhiteKey(s);
            return s;
        }
        catch
        {
            return new AppSettings();
        }
    }

    static void NormalizeRembgAlphaThresholds(AppSettings s)
    {
        if (s.RembgAlphaFg is < 1 or > 255)
            s.RembgAlphaFg = 240;
        if (s.RembgAlphaBg is < 0 or > 255)
            s.RembgAlphaBg = 10;
    }

    static void NormalizeWhiteKey(AppSettings s)
    {
        if (s.MatteMode is not (0 or 1))
            s.MatteMode = 0;
        s.WhiteKeyRgb &= 0xFFFFFF;
        if (s.WhiteKeySimilarity <= 0 || s.WhiteKeySimilarity > 1)
            s.WhiteKeySimilarity = 0.08;
        if (s.WhiteKeyBlend < 0 || s.WhiteKeyBlend > 1)
            s.WhiteKeyBlend = 0.04;
        if (s.WhiteKeyDespillMode is < 0 or > 3)
            s.WhiteKeyDespillMode = 0;
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
