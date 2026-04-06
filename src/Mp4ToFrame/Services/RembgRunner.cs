using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Mp4ToFrame.Services;

public static class RembgRunner
{
    public sealed class Options
    {
        public string InputFolder = "";
        public string OutputFolder = "";
        public string? RembgExecutable;
        public string Model = "isnet-general-use";
        public bool AlphaMatting;
        public int AlphaMattingErodeSize = 4;
        public bool PostProcessMask;
    }

    public static async Task RunFolderAsync(Options opt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(opt.InputFolder) || !Directory.Exists(opt.InputFolder))
            throw new DirectoryNotFoundException(opt.InputFolder);
        if (string.IsNullOrEmpty(opt.OutputFolder))
            throw new ArgumentException("请指定抠图输出目录");

        Directory.CreateDirectory(opt.OutputFolder);

        var (fileName, arguments) = ResolveCommand(opt);

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

        proc.Start();
        var errTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        var outTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);
        var err = await errTask;
        var std = await outTask;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"rembg 退出码 {proc.ExitCode}。\n{err}\n{std}\n\n安装: pip install \"rembg[cli,cpu]\"\n" +
                "或在界面填写 rembg.exe / python.exe 完整路径。");
    }

    public static string BuildRembgCliArguments(Options opt)
    {
        var sb = new StringBuilder();
        sb.Append("p \"");
        sb.Append(EscapeForProcessArgument(opt.InputFolder));
        sb.Append("\" \"");
        sb.Append(EscapeForProcessArgument(opt.OutputFolder));
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

    static (string fileName, string arguments) ResolveCommand(Options opt)
    {
        var argBody = BuildRembgCliArguments(opt);

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
