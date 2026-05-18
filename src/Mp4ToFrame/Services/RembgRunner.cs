using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Mp4ToFrame.Services;

public static class RembgRunner
{
    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    public sealed class Options
    {
        public string InputFolder = "";
        public string OutputFolder = "";
        public string? RembgExecutable;
        public string Model = "isnet-general-use";
        public bool AlphaMatting;
        /// <summary>rembg -ae；与官方 CLI 默认 10 不同，界面滑块常用更小值以保边。</summary>
        public int AlphaMattingErodeSize = 4;
        /// <summary>rembg -af，trimap 前景阈值（越大越“苛刻”，蒙版略软时易把实心主体判成未知区而抠穿）。默认 240 与 rembg 一致。</summary>
        public int AlphaMattingForegroundThreshold = 240;
        /// <summary>rembg -ab，trimap 背景阈值；默认 10 与 rembg 一致。</summary>
        public int AlphaMattingBackgroundThreshold = 10;
        public bool PostProcessMask;
        /// <summary>并行启动的 rembg 进程数（1=与官方 CLI 相同，顺序处理）。大于 1 时将图片分到多个子文件夹并行跑 rembg p，可明显缩短总墙钟时间（内存与 CPU 占用上升）。</summary>
        public int ParallelJobs { get; set; } = 1;
        /// <summary>抠图完成后将 stem.png 重命名为 stem+后缀+.png；空或空白则保持 stem.png。</summary>
        public string? OutputStemSuffix { get; set; }
    }

    public static async Task RunFolderAsync(Options opt, CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        if (string.IsNullOrEmpty(opt.InputFolder) || !Directory.Exists(opt.InputFolder))
            throw new DirectoryNotFoundException(opt.InputFolder);
        if (string.IsNullOrEmpty(opt.OutputFolder))
            throw new ArgumentException("请指定抠图输出目录");

        Directory.CreateDirectory(opt.OutputFolder);

        var allFiles = EnumerateImageFiles(opt.InputFolder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        if (allFiles.Count == 0)
            return;

        ThrowIfBasenameCollisions(allFiles);

        var stemSuffix = NormalizeOutputStemSuffix(opt.OutputStemSuffix);
        ValidateOutputStemSuffix(stemSuffix);

        var jobs = Math.Clamp(opt.ParallelJobs, 1, 8);
        if (jobs == 1 || allFiles.Count == 1)
        {
            progress?.Report("正在抠图（单进程）…");
            DeleteExpectedOutputPngs(allFiles, opt.OutputFolder, stemSuffix);
            await RunSingleFolderAsync(opt, opt.InputFolder, opt.OutputFolder, ompThreads: null, cancellationToken)
                .ConfigureAwait(false);
            VerifyRembgIntermediatePngsExist(allFiles, opt.OutputFolder);
            ApplyMattedOutputStemSuffix(allFiles, opt.OutputFolder, stemSuffix);
            VerifyFinalMattedPngsExist(allFiles, opt.OutputFolder, stemSuffix);
            return;
        }

        jobs = Math.Min(jobs, allFiles.Count);
        var tempRoot = Path.Combine(Path.GetTempPath(), "Mp4ToFrame-rembg-" + Guid.NewGuid().ToString("N"));
        var perProcessThreads = Math.Max(1, Environment.ProcessorCount / jobs);

        try
        {
            var batches = SplitIntoBatches(allFiles, jobs);
            var tasks = new List<Task>();

            for (var i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];
                var inDir = Path.Combine(tempRoot, "in", i.ToString(CultureInfo.InvariantCulture));
                var outDir = Path.Combine(tempRoot, "out", i.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(inDir);
                Directory.CreateDirectory(outDir);

                foreach (var src in batch)
                {
                    var name = Path.GetFileName(src);
                    var dst = Path.Combine(inDir, name);
                    LinkOrCopyFile(src, dst);
                }

                var idx = i;
                tasks.Add(Task.Run(async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report($"正在抠图（并行 {idx + 1}/{batches.Count}）…");
                    await RunSingleFolderAsync(opt, inDir, outDir, perProcessThreads, cancellationToken)
                        .ConfigureAwait(false);
                }, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            DeleteExpectedOutputPngs(allFiles, opt.OutputFolder, stemSuffix);
            foreach (var batchDir in Directory.GetDirectories(Path.Combine(tempRoot, "out")))
            {
                foreach (var png in Directory.GetFiles(batchDir, "*.png", SearchOption.TopDirectoryOnly))
                {
                    var dest = Path.Combine(opt.OutputFolder, Path.GetFileName(png));
                    File.Copy(png, dest, overwrite: true);
                }
            }

            VerifyRembgIntermediatePngsExist(allFiles, opt.OutputFolder);
            ApplyMattedOutputStemSuffix(allFiles, opt.OutputFolder, stemSuffix);
            VerifyFinalMattedPngsExist(allFiles, opt.OutputFolder, stemSuffix);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    static IEnumerable<string> EnumerateImageFiles(string folder)
    {
        foreach (var ext in ImageExtensions)
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*" + ext, SearchOption.AllDirectories))
                yield return f;
        }
    }

    static string? NormalizeOutputStemSuffix(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static void ValidateOutputStemSuffix(string? stemSuffix)
    {
        if (string.IsNullOrEmpty(stemSuffix))
            return;
        if (stemSuffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("文件名后缀含有非法字符，不能与 \\ / : * ? \" < > | 等用于路径的符号。");
    }

    static string RembgIntermediatePngPath(string inputPath, string outputFolder)
    {
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(outputFolder, stem + ".png");
    }

    static string FinalMattedPngPath(string inputPath, string outputFolder, string? stemSuffix)
    {
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var name = string.IsNullOrEmpty(stemSuffix) ? stem + ".png" : stem + stemSuffix + ".png";
        return Path.Combine(outputFolder, name);
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
            "输入目录中存在同名图片（文件名相同、路径不同），rembg 只会按文件名写一张 PNG，其余会被覆盖或丢失。\n" +
            $"示例（{dupes[0].Key}）：\n{sample}");
    }

    static void DeleteExpectedOutputPngs(IReadOnlyList<string> inputFiles, string outputFolder, string? stemSuffix)
    {
        foreach (var input in inputFiles)
        {
            try
            {
                var inter = RembgIntermediatePngPath(input, outputFolder);
                if (File.Exists(inter))
                    File.Delete(inter);
                if (!string.IsNullOrEmpty(stemSuffix))
                {
                    var final = FinalMattedPngPath(input, outputFolder, stemSuffix);
                    if (File.Exists(final))
                        File.Delete(final);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    static void VerifyRembgIntermediatePngsExist(IReadOnlyList<string> inputFiles, string outputFolder)
    {
        var missing = new List<string>();
        foreach (var input in inputFiles)
        {
            if (!File.Exists(RembgIntermediatePngPath(input, outputFolder)))
                missing.Add(Path.GetFileName(input) ?? input);
        }

        if (missing.Count == 0)
            return;
        var sample = string.Join(", ", missing.Take(12));
        var more = missing.Count > 12 ? $" …共 {missing.Count} 张" : "";
        throw new InvalidOperationException(
            $"抠图输出少于输入：缺 {missing.Count} 张。rembg 对单张失败只打印错误并跳过，进程仍可能返回成功。\n" +
            $"缺失（节选）：{sample}{more}\n\n" +
            "可尝试：并行进程数改为 1；关闭 Alpha matting；若 matting 把实心区域抠淡，将「前景阈值 -af」略调低（如 200–220）或减小腐蚀 -ae。");
    }

    static void ApplyMattedOutputStemSuffix(IReadOnlyList<string> inputFiles, string outputFolder, string? stemSuffix)
    {
        if (string.IsNullOrEmpty(stemSuffix))
            return;

        foreach (var input in inputFiles)
        {
            var src = RembgIntermediatePngPath(input, outputFolder);
            var dst = FinalMattedPngPath(input, outputFolder, stemSuffix);
            if (!File.Exists(src))
                throw new InvalidOperationException($"抠图后未找到中间文件，无法加后缀重命名：{Path.GetFileName(src)}");
            if (File.Exists(dst))
                File.Delete(dst);
            File.Move(src, dst);
        }
    }

    static void VerifyFinalMattedPngsExist(IReadOnlyList<string> inputFiles, string outputFolder, string? stemSuffix)
    {
        var missing = new List<string>();
        foreach (var input in inputFiles)
        {
            if (!File.Exists(FinalMattedPngPath(input, outputFolder, stemSuffix)))
                missing.Add(Path.GetFileName(input) ?? input);
        }

        if (missing.Count == 0)
            return;
        var sample = string.Join(", ", missing.Take(12));
        var more = missing.Count > 12 ? $" …共 {missing.Count} 张" : "";
        throw new InvalidOperationException(
            $"抠图结果校验失败（重命名后缀后）：缺 {missing.Count} 张。\n缺失（节选）：{sample}{more}");
    }

    static List<List<string>> SplitIntoBatches(List<string> files, int batchCount)
    {
        var batches = new List<List<string>>();
        for (var i = 0; i < batchCount; i++)
            batches.Add(new List<string>());

        for (var i = 0; i < files.Count; i++)
            batches[i % batchCount].Add(files[i]);

        return batches.Where(b => b.Count > 0).ToList();
    }

    static void LinkOrCopyFile(string sourcePath, string destPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            string.Equals(Path.GetPathRoot(Path.GetFullPath(sourcePath)),
                Path.GetPathRoot(Path.GetFullPath(destPath)), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(destPath))
                    File.Delete(destPath);
                if (CreateHardLinkWin(destPath, sourcePath, IntPtr.Zero))
                    return;
            }
            catch
            {
                // fall through
            }
        }

        File.Copy(sourcePath, destPath, overwrite: true);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW")]
    static extern bool CreateHardLinkWin(string newFileName, string existingFileName, IntPtr securityAttributes);

    static async Task RunSingleFolderAsync(Options template, string inputFolder, string outputFolder,
        int? ompThreads, CancellationToken cancellationToken)
    {
        var (fileName, arguments) = ResolveCommand(template, inputFolder, outputFolder);

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            }
        };

        if (ompThreads is int t)
        {
            proc.StartInfo.Environment["OMP_NUM_THREADS"] = t.ToString(CultureInfo.InvariantCulture);
            proc.StartInfo.Environment["MKL_NUM_THREADS"] = t.ToString(CultureInfo.InvariantCulture);
            proc.StartInfo.Environment["OPENBLAS_NUM_THREADS"] = t.ToString(CultureInfo.InvariantCulture);
        }

        proc.Start();
        var errTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        var outTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);

        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var err = await errTask.ConfigureAwait(false);
        var std = await outTask.ConfigureAwait(false);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"rembg 退出码 {proc.ExitCode}。\n{err}\n{std}\n\n安装: pip install \"rembg[cli,cpu]\"\n" +
                "或在界面填写 rembg.exe / python.exe 完整路径。");
    }

    public static string BuildRembgCliArguments(Options opt, string inputFolder, string outputFolder)
    {
        var sb = new StringBuilder();
        sb.Append("p \"");
        sb.Append(EscapeForProcessArgument(inputFolder));
        sb.Append("\" \"");
        sb.Append(EscapeForProcessArgument(outputFolder));
        sb.Append('"');
        if (!string.IsNullOrWhiteSpace(opt.Model))
        {
            sb.Append(" -m ");
            sb.Append(opt.Model.Trim());
        }

        if (opt.AlphaMatting)
        {
            sb.Append(" -a");
            sb.Append(" -af ").Append(Math.Clamp(opt.AlphaMattingForegroundThreshold, 0, 255));
            sb.Append(" -ab ").Append(Math.Clamp(opt.AlphaMattingBackgroundThreshold, 0, 255));
            sb.Append(" -ae ").Append(Math.Clamp(opt.AlphaMattingErodeSize, 0, 31));
        }

        if (opt.PostProcessMask)
            sb.Append(" -ppm");

        return sb.ToString();
    }

    static (string fileName, string arguments) ResolveCommand(Options opt, string inputFolder, string outputFolder)
    {
        var argBody = BuildRembgCliArguments(opt, inputFolder, outputFolder);

        if (!string.IsNullOrWhiteSpace(opt.RembgExecutable))
        {
            var exe = opt.RembgExecutable.Trim();
            var file = Path.GetFileName(exe).ToLowerInvariant();
            if (file is "python.exe" or "python3.exe" or "py.exe")
                return (exe, "-m rembg " + argBody);
            return (exe, argBody);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var rembgOnPath = FindExecutableInPath("rembg.exe");
            if (!string.IsNullOrEmpty(rembgOnPath))
                return (rembgOnPath, argBody);

            var scriptsRembg = FindRembgInPythonInstallations();
            if (!string.IsNullOrEmpty(scriptsRembg))
                return (scriptsRembg, argBody);

            var pythonExe = FindPythonExeForModule("rembg");
            if (!string.IsNullOrEmpty(pythonExe))
                return (pythonExe, "-m rembg " + argBody);
        }
        else
        {
            var rembgUnix = FindExecutableInPath("rembg");
            if (!string.IsNullOrEmpty(rembgUnix))
                return (rembgUnix, argBody);
        }

        throw new InvalidOperationException(
            "找不到 rembg。请填写 rembg.exe 或 python.exe 完整路径，或安装: pip install \"rembg[cli,cpu]\"");
    }

    static string? FindExecutableInPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            try
            {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    static string? FindRembgInPythonInstallations()
    {
        foreach (var root in GetWindowsPythonRoots())
        {
            if (!Directory.Exists(root))
                continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var candidate = Path.Combine(dir, "Scripts", "rembg.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    static string? FindPythonExeForModule(string module)
    {
        foreach (var root in GetWindowsPythonRoots())
        {
            if (!Directory.Exists(root))
                continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var python = Path.Combine(dir, "python.exe");
                    if (!File.Exists(python))
                        continue;
                    if (PythonModuleHelpOk(python, module))
                        return python;
                }
            }
            catch
            {
                // ignore
            }
        }

        foreach (var name in new[] { "py.exe", "python.exe", "python3.exe" })
        {
            var fromPath = FindExecutableInPath(name);
            if (string.IsNullOrEmpty(fromPath))
                continue;
            if (PythonModuleHelpOk(fromPath, module))
                return fromPath;
        }

        return null;
    }

    static IEnumerable<string> GetWindowsPythonRoots()
    {
        var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(la, "Programs", "Python");

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return pf;

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.Equals(pf, pf86, StringComparison.OrdinalIgnoreCase))
            yield return pf86;
    }

    static bool PythonModuleHelpOk(string pythonExe, string module)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = "-m " + module + " --help",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (p == null)
                return false;
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    static string EscapeForProcessArgument(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        return path.Replace("\"", "\\\"");
    }
}
