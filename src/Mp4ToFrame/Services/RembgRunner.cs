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
        public int AlphaMattingErodeSize = 4;
        public bool PostProcessMask;
        /// <summary>并行启动的 rembg 进程数（1=与官方 CLI 相同，顺序处理）。大于 1 时将图片分到多个子文件夹并行跑 rembg p，可明显缩短总墙钟时间（内存与 CPU 占用上升）。</summary>
        public int ParallelJobs { get; set; } = 1;
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

        var jobs = Math.Clamp(opt.ParallelJobs, 1, 8);
        if (jobs == 1 || allFiles.Count == 1)
        {
            progress?.Report("正在抠图（单进程）…");
            await RunSingleFolderAsync(opt, opt.InputFolder, opt.OutputFolder, ompThreads: null, cancellationToken)
                .ConfigureAwait(false);
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

            foreach (var batchDir in Directory.GetDirectories(Path.Combine(tempRoot, "out")))
            {
                foreach (var png in Directory.GetFiles(batchDir, "*.png", SearchOption.TopDirectoryOnly))
                {
                    var dest = Path.Combine(opt.OutputFolder, Path.GetFileName(png));
                    File.Copy(png, dest, overwrite: true);
                }
            }
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
