using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mp4ToFrame.Services;

/// <summary>使用 FFmpeg 从 MP4 导出序列帧（目标 FPS + 按目标分辨率缩放并黑边填充 + 可选水平镜像 + 可选 Border 内切/外扩）。</summary>
public static class FfmpegFrameExtractor
{
    /// <param name="borderLRTB">左、右、上、下：正数为从该边向内裁切像素；负数为向该边外扩（绝对值）像素并以 <paramref name="borderFillArgb"/> 填充。</param>
    /// <param name="borderFillArgb">外扩填充色，ARGB（与 WPF 一致）。A=0 为全透明（默认）。</param>
    public static async Task<int> ExtractAsync(
        string ffmpegPath,
        string videoPath,
        string outputDir,
        int width,
        int height,
        double targetFps,
        bool mirrorHorizontally,
        int borderL,
        int borderR,
        int borderT,
        int borderB,
        uint borderFillArgb,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            throw new FileNotFoundException("未找到 ffmpeg.exe。请将 ffmpeg.exe 放在程序同目录或加入 PATH。", ffmpegPath);
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("视频文件不存在", videoPath);

        Directory.CreateDirectory(outputDir);
        var pattern = Path.Combine(outputDir, "frame_%05d.png");
        var vf = BuildVideoFilter(width, height, targetFps, mirrorHorizontally, borderL, borderR, borderT, borderB,
            borderFillArgb);

        var args = $"-hide_banner -loglevel error -stats -y -i \"{videoPath}\" -vf \"{vf}\" \"{pattern}\"";

        progress?.Report("正在运行 FFmpeg…");

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = outputDir
            }
        };

        proc.Start();
        var errTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        var outTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);

        await proc.WaitForExitAsync(cancellationToken);
        _ = await errTask;
        _ = await outTask;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg 退出码 {proc.ExitCode}。请确认视频编码为 H.264 等常见格式。");

        var pngs = Directory.GetFiles(outputDir, "frame_*.png", SearchOption.TopDirectoryOnly);
        return pngs.Length;
    }

    /// <summary>校验 Border 在目标宽高下是否可行（内切后宽、高须 ≥ 1）。</summary>
    public static void ValidateBorder(int width, int height, int borderL, int borderR, int borderT, int borderB)
    {
        var cropL = Math.Max(0, borderL);
        var cropR = Math.Max(0, borderR);
        var cropT = Math.Max(0, borderT);
        var cropB = Math.Max(0, borderB);
        var iw = width - cropL - cropR;
        var ih = height - cropT - cropB;
        if (iw < 1 || ih < 1)
            throw new InvalidOperationException(
                $"Border 内切后尺寸无效（{iw}×{ih}）。请减小正值 Border 或增大导出宽高。");
    }

    static string BuildVideoFilter(int w, int h, double fps, bool mirror, int bL, int bR, int bT, int bB, uint fillArgb)
    {
        var inv = CultureInfo.InvariantCulture;
        var fpsStr = Math.Abs(fps % 1) < double.Epsilon
            ? ((int)Math.Round(fps)).ToString(inv)
            : fps.ToString("0.###", inv);

        var parts = new List<string>
        {
            $"fps={fpsStr}",
            $"scale={w}:{h}:force_original_aspect_ratio=decrease",
            $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black"
        };
        if (mirror)
            parts.Add("hflip");

        AppendBorderFilterParts(parts, w, h, bL, bR, bT, bB, fillArgb);
        return string.Join(",", parts);
    }

    /// <summary>在已是 <paramref name="canvasW"/>×<paramref name="canvasH"/> 的画布上追加 Border（与视频抽帧一致：正裁切、负外扩）。</summary>
    public static void AppendBorderFilterParts(IList<string> parts, int canvasW, int canvasH, int bL, int bR, int bT,
        int bB, uint fillArgb)
    {
        var inv = CultureInfo.InvariantCulture;
        var cropL = Math.Max(0, bL);
        var cropR = Math.Max(0, bR);
        var cropT = Math.Max(0, bT);
        var cropB = Math.Max(0, bB);
        var padL = Math.Max(0, -bL);
        var padR = Math.Max(0, -bR);
        var padT = Math.Max(0, -bT);
        var padB = Math.Max(0, -bB);

        var iw = canvasW - cropL - cropR;
        var ih = canvasH - cropT - cropB;

        if (cropL + cropR + cropT + cropB > 0)
            parts.Add($"crop={iw}:{ih}:{cropL}:{cropT}");

        if (padL + padR + padT + padB > 0)
        {
            var ow = iw + padL + padR;
            var oh = ih + padT + padB;
            var a = (fillArgb >> 24) & 0xFF;
            if (a < 255)
                parts.Add("format=rgba");
            var colorStr = FormatPadColor(fillArgb, inv);
            parts.Add($"pad={ow}:{oh}:{padL}:{padT}:color={colorStr}");
        }
    }

    /// <summary>FFmpeg pad color：0xRRGGBB@alpha（alpha 为 0–1）。</summary>
    static string FormatPadColor(uint argb, CultureInfo inv)
    {
        var a = (argb >> 24) & 0xFF;
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        var alpha = a / 255.0;
        return $"0x{r:X2}{g:X2}{b:X2}@{alpha.ToString("0.##########", inv)}";
    }
}
