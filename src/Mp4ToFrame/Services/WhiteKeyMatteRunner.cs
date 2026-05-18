using System.Diagnostics;
using System.Globalization;

namespace Mp4ToFrame.Services;

/// <summary>对纯色背景序列帧使用 FFmpeg <c>colorkey</c> 去底，输出带 Alpha 的 PNG（键控色与背景一致即可，如白/绿/蓝幕）。</summary>
public static class WhiteKeyMatteRunner
{
    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    public sealed class Options
    {
        public string InputFolder = "";
        public string OutputFolder = "";
        public int KeyR { get; set; } = 255;
        public int KeyG { get; set; } = 255;
        public int KeyB { get; set; } = 255;
        /// <summary>与 FFmpeg colorkey 的 similarity 一致，建议约 0.04～0.15。</summary>
        public double Similarity { get; set; } = 0.08;
        /// <summary>与 FFmpeg colorkey 的 blend 一致，边缘羽化，建议约 0～0.1。</summary>
        public double Blend { get; set; } = 0.04;
        /// <summary>0=自动、1=绿幕 despill、2=蓝幕 despill、3=关闭。</summary>
        public int DespillMode { get; set; }
        public string? OutputStemSuffix { get; set; }
    }

    public static void ValidateOptions(Options opt)
    {
        if (string.IsNullOrEmpty(opt.InputFolder) || !Directory.Exists(opt.InputFolder))
            throw new DirectoryNotFoundException(opt.InputFolder);
        if (string.IsNullOrEmpty(opt.OutputFolder))
            throw new ArgumentException("请指定输出目录");
        if (opt.KeyR is < 0 or > 255 || opt.KeyG is < 0 or > 255 || opt.KeyB is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(opt), "键控颜色 RGB 须在 0～255。");
        if (opt.Similarity is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(opt), "colorkey similarity 须在 (0, 1] 内，例如 0.08。");
        if (opt.Blend is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(opt), "colorkey blend 须在 [0, 1] 内。");
        if (opt.DespillMode is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(opt), "去溢色模式无效。");
        RembgRunner.ValidateOutputStemSuffix(opt.OutputStemSuffix);
    }

    public static async Task RunFolderAsync(string ffmpegPath, Options opt,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(opt);
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            throw new FileNotFoundException("未找到 ffmpeg.exe。", ffmpegPath);

        Directory.CreateDirectory(opt.OutputFolder);

        var allFiles = EnumerateImageFiles(opt.InputFolder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        if (allFiles.Count == 0)
            return;

        ThrowIfBasenameCollisions(allFiles);

        var inv = CultureInfo.InvariantCulture;
        var vf = BuildFilterGraph(opt, inv);

        foreach (var input in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dst = FinalOutputPngPath(input, opt.OutputFolder, opt.OutputStemSuffix);
            try
            {
                if (File.Exists(dst))
                    File.Delete(dst);
            }
            catch
            {
                // ignore
            }
        }

        for (var i = 0; i < allFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var src = allFiles[i];
            progress?.Report(string.Format(inv, "色键抠图 {0}/{1}…", i + 1, allFiles.Count));
            var dst = FinalOutputPngPath(src, opt.OutputFolder, opt.OutputStemSuffix);
            var args =
                $"-hide_banner -loglevel error -y -i \"{EscapePath(src)}\" -vf \"{vf}\" \"{EscapePath(dst)}\"";
            await RunFfmpegOnceAsync(ffmpegPath, args, src, cancellationToken).ConfigureAwait(false);
        }

        VerifyOutputsExist(allFiles, opt.OutputFolder, opt.OutputStemSuffix);
    }

    static string BuildFilterGraph(Options opt, CultureInfo inv)
    {
        var rgb = ((opt.KeyR & 255) << 16) | ((opt.KeyG & 255) << 8) | (opt.KeyB & 255);
        var colorHex = rgb.ToString("x6", inv);
        var sim = opt.Similarity.ToString("0.####", inv);
        var blend = opt.Blend.ToString("0.####", inv);
        var parts = new List<string> { "format=rgba", $"colorkey=0x{colorHex}:{sim}:{blend}" };
        var despill = ResolveDespill(opt);
        if (!string.IsNullOrEmpty(despill))
            parts.Add(despill);
        return string.Join(",", parts);
    }

    static string? ResolveDespill(Options opt)
    {
        return opt.DespillMode switch
        {
            3 => null,
            1 => "despill=green=-1",
            2 => "despill=blue=-1",
            _ => AutoDespill(opt.KeyR, opt.KeyG, opt.KeyB)
        };
    }

    /// <summary>白/灰键不叠 despill；绿/蓝占优时叠对应 despill。</summary>
    static string? AutoDespill(int r, int g, int b)
    {
        if (r == g && g == b)
            return null;
        var max = Math.Max(Math.Max(r, g), b);
        if (max < 16)
            return null;
        if (g >= max && g - Math.Max(r, b) >= 24)
            return "despill=green=-1";
        if (b >= max && b - Math.Max(r, g) >= 24)
            return "despill=blue=-1";
        return null;
    }

    static string FinalOutputPngPath(string inputPath, string outputFolder, string? stemSuffix)
    {
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var name = string.IsNullOrEmpty(stemSuffix) ? stem + ".png" : stem + stemSuffix + ".png";
        return Path.Combine(outputFolder, name);
    }

    static void VerifyOutputsExist(IReadOnlyList<string> inputFiles, string outputFolder, string? stemSuffix)
    {
        var missing = new List<string>();
        foreach (var input in inputFiles)
        {
            if (!File.Exists(FinalOutputPngPath(input, outputFolder, stemSuffix)))
                missing.Add(Path.GetFileName(input) ?? input);
        }

        if (missing.Count == 0)
            return;
        var sample = string.Join(", ", missing.Take(12));
        var more = missing.Count > 12 ? $" …共 {missing.Count} 张" : "";
        throw new InvalidOperationException($"色键输出不完整。缺失（节选）：{sample}{more}");
    }

    static async Task RunFfmpegOnceAsync(string ffmpegPath, string arguments, string inputPathForError,
        CancellationToken cancellationToken)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            }
        };
        proc.Start();
        var errTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        _ = await proc.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var err = await errTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"FFmpeg 色键失败: {Path.GetFileName(inputPathForError)}\n{err}\n\n请确认 ffmpeg 支持 colorkey/despill 滤镜（建议较新版本）；或尝试关闭「去溢色」、调整 similarity / blend。");
    }

    static IEnumerable<string> EnumerateImageFiles(string folder)
    {
        foreach (var ext in ImageExtensions)
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*" + ext, SearchOption.AllDirectories))
                yield return f;
        }
    }

    static void ThrowIfBasenameCollisions(IReadOnlyList<string> inputFiles)
    {
        var dupes = inputFiles
            .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Where(gr => gr.Count() > 1)
            .ToList();
        if (dupes.Count == 0)
            return;
        var sample = string.Join("\n", dupes[0].Take(4));
        throw new InvalidOperationException(
            "输入目录中存在同名图片（文件名相同、路径不同），输出会互相覆盖。\n" +
            $"示例（{dupes[0].Key}）：\n{sample}");
    }

    static string EscapePath(string path) => path.Replace("\"", "\\\"");
}
