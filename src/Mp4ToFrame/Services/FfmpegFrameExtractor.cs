using System.Diagnostics;
using System.Globalization;

namespace Mp4ToFrame.Services;

/// <summary>使用 FFmpeg 从 MP4 导出序列帧（目标 FPS + 按目标分辨率缩放并黑边填充 + 可选水平镜像）。</summary>
public static class FfmpegFrameExtractor
{
    public static async Task<int> ExtractAsync(
        string ffmpegPath,
        string videoPath,
        string outputDir,
        int width,
        int height,
        double targetFps,
        bool mirrorHorizontally,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            throw new FileNotFoundException("未找到 ffmpeg.exe。请将 ffmpeg.exe 放在程序同目录或加入 PATH。", ffmpegPath);
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("视频文件不存在", videoPath);

        Directory.CreateDirectory(outputDir);
        var pattern = Path.Combine(outputDir, "frame_%05d.png");
        var vf = BuildVideoFilter(width, height, targetFps, mirrorHorizontally);

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

    static string BuildVideoFilter(int w, int h, double fps, bool mirror)
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

        return string.Join(",", parts);
    }
}
