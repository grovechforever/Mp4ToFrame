using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;

namespace Mp4ToFrame.Services;

/// <summary>
/// 便携版：从 BtbN FFmpeg-Builds 下载 Windows x64 zip，解压后将 ffmpeg.exe 复制到程序目录。
/// 另可选通过本机 winget 安装 Gyan.FFmpeg（安装后需重启程序或依赖 PATH）。
/// </summary>
public static class FfmpegInstaller
{
    /// <summary>BtbN 自动构建（GPL）。GitHub 会重定向到当前最新包。</summary>
    public const string PortableZipUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";

    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(45),
        DefaultRequestHeaders = { { "User-Agent", "Mp4ToFrame/1.0" } }
    };

    /// <summary>将便携版 ffmpeg.exe 写入 <paramref name="targetDirectory"/>（通常为程序目录）。</summary>
    public static async Task InstallPortableAsync(
        string targetDirectory,
        IProgress<(long bytesRead, long? totalLength)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDirectory);
        Directory.CreateDirectory(targetDirectory);
        var targetExe = Path.Combine(targetDirectory, "ffmpeg.exe");

        var tempZip = Path.Combine(Path.GetTempPath(), "Mp4ToFrame-ffmpeg-" + Guid.NewGuid().ToString("N") + ".zip");
        var tempExtract = Path.Combine(Path.GetTempPath(), "Mp4ToFrame-ffmpeg-extract-" + Guid.NewGuid().ToString("N"));

        try
        {
            await DownloadToFileAsync(PortableZipUrl, tempZip, progress, cancellationToken).ConfigureAwait(false);

            ZipFile.ExtractToDirectory(tempZip, tempExtract);

            var found = Directory.GetFiles(tempExtract, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (found == null)
                throw new FileNotFoundException("下载的压缩包中未找到 ffmpeg.exe，可能发布页结构已变更。");

            File.Copy(found, targetExe, overwrite: true);
        }
        finally
        {
            TryDelete(tempZip);
            TryDeleteDir(tempExtract);
        }
    }

    static async Task DownloadToFileAsync(
        string url,
        string destPath,
        IProgress<(long bytesRead, long? totalLength)>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) >
               0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            read += n;
            progress?.Report((read, total));
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>若 PATH 中存在 winget.exe，则启动交互式安装（可能弹出 UAC/商店界面）。</summary>
    public static bool TryStartWingetInstallGyanFfmpeg()
    {
        var winget = FindInPath("winget.exe");
        if (string.IsNullOrEmpty(winget))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = winget,
                Arguments = "install --id Gyan.FFmpeg -e --accept-package-agreements --accept-source-agreements",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? FindWingetPath() => FindInPath("winget.exe");

    static string? FindInPath(string fileName)
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
}
