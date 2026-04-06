namespace Mp4ToFrame.Services;

/// <summary>工作区根目录下的标准子目录：Video、Frame、Matted；可修正历史拼写 Vedio → Video。</summary>
public static class WorkspaceLayout
{
    public const string VideoFolderName = "Video";
    public const string FrameFolderName = "Frame";
    public const string MattedFolderName = "Matted";
    const string LegacyVideoMisspelling = "Vedio";

    public static string VideoDir(string workspaceRoot) =>
        Path.GetFullPath(Path.Combine(workspaceRoot, VideoFolderName));

    public static string FrameDir(string workspaceRoot) =>
        Path.GetFullPath(Path.Combine(workspaceRoot, FrameFolderName));

    public static string MattedDir(string workspaceRoot) =>
        Path.GetFullPath(Path.Combine(workspaceRoot, MattedFolderName));

    public static void FixLegacyNamesAndEnsureFolders(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return;

        workspaceRoot = Path.GetFullPath(workspaceRoot.Trim());
        if (!Directory.Exists(workspaceRoot))
            return;

        var legacy = Path.Combine(workspaceRoot, LegacyVideoMisspelling);
        var video = Path.Combine(workspaceRoot, VideoFolderName);
        if (Directory.Exists(legacy) && !Directory.Exists(video))
            Directory.Move(legacy, video);

        Directory.CreateDirectory(VideoDir(workspaceRoot));
        Directory.CreateDirectory(FrameDir(workspaceRoot));
        Directory.CreateDirectory(MattedDir(workspaceRoot));
    }

    public static bool HasFrameImages(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return false;
        var frame = FrameDir(workspaceRoot);
        if (!Directory.Exists(frame))
            return false;
        return Directory.GetFiles(frame, "*.png", SearchOption.TopDirectoryOnly).Length > 0
               || Directory.GetFiles(frame, "*.jpg", SearchOption.TopDirectoryOnly).Length > 0
               || Directory.GetFiles(frame, "*.jpeg", SearchOption.TopDirectoryOnly).Length > 0;
    }

    public static string[] ListMp4InVideo(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return Array.Empty<string>();

        FixLegacyNamesAndEnsureFolders(workspaceRoot);
        var videoDir = VideoDir(workspaceRoot);
        if (!Directory.Exists(videoDir))
            return Array.Empty<string>();

        var files = Directory.GetFiles(videoDir, "*.mp4", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }
}
