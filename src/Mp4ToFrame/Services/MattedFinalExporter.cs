using System.Diagnostics;
using System.Globalization;

namespace Mp4ToFrame.Services;

/// <summary>将 Matted 目录中的图片用 FFmpeg 缩放到目标分辨率（等比缩放 + 透明边 + 可选 Border），与视频抽帧语义一致。</summary>
public static class MattedFinalExporter
{
    static string BuildImageFilter(int w, int h, int bL, int bR, int bT, int bB, uint borderFillArgb)
    {
        var parts = new List<string>
        {
            "format=rgba",
            $"scale={w}:{h}:force_original_aspect_ratio=decrease",
            $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=0x00000000@0"
        };
        FfmpegFrameExtractor.AppendBorderFilterParts(parts, w, h, bL, bR, bT, bB, borderFillArgb);
        return string.Join(",", parts);
    }

    public static List<string> ListMattedImages(string mattedDir)
    {
        if (!Directory.Exists(mattedDir))
            return [];

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in new[] { "*.png", "*.jpg", "*.jpeg" })
        {
            foreach (var f in Directory.GetFiles(mattedDir, ext, SearchOption.TopDirectoryOnly))
                set.Add(f);
        }

        return set.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<int> ExportFolderAsync(
        string ffmpegPath,
        string mattedDir,
        string outputDir,
        int width,
        int height,
        int borderL,
        int borderR,
        int borderT,
        int borderB,
        uint borderFillArgb,
        string? outputStemSuffix,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            throw new FileNotFoundException("未找到 ffmpeg.exe。", ffmpegPath);
        if (!Directory.Exists(mattedDir))
            throw new DirectoryNotFoundException(mattedDir);

        var files = ListMattedImages(mattedDir);
        if (files.Count == 0)
            return 0;

        Directory.CreateDirectory(outputDir);
        var vf = BuildImageFilter(width, height, borderL, borderR, borderT, borderB, borderFillArgb);
        var inv = CultureInfo.InvariantCulture;

        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var src = files[i];
            progress?.Report(string.Format(inv, "最终导出 {0}/{1}…", i + 1, files.Count));

            var baseName = Path.GetFileNameWithoutExtension(src);
            var destName = string.IsNullOrEmpty(outputStemSuffix)
                ? baseName + ".png"
                : baseName + outputStemSuffix + ".png";
            var dest = Path.Combine(outputDir, destName);

            var args = $"-hide_banner -loglevel error -y -i \"{EscapePath(src)}\" -vf \"{vf}\" \"{EscapePath(dest)}\"";

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            var err = await proc.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            _ = await proc.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"FFmpeg 处理失败: {Path.GetFileName(src)}\n{err}");
        }

        return files.Count;
    }

    static string EscapePath(string path) => path.Replace("\"", "\\\"");
}
