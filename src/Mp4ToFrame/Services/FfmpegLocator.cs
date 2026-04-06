namespace Mp4ToFrame.Services;

public static class FfmpegLocator
{
    /// <summary>优先 exe 同目录下的 ffmpeg.exe，其次 PATH。</summary>
    public static string? FindFfmpegExecutable()
    {
        var baseDir = AppContext.BaseDirectory;
        var local = Path.Combine(baseDir, "ffmpeg.exe");
        if (File.Exists(local))
            return local;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            try
            {
                var full = Path.Combine(dir.Trim(), "ffmpeg.exe");
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
