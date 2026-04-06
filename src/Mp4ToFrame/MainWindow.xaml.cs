using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mp4ToFrame.Services;

namespace Mp4ToFrame;

public partial class MainWindow : Window
{
    AppSettings _settings = new();
    bool _uiReady;
    FileSystemWatcher? _videoFolderWatcher;
    string? _videoWatcherWorkspaceRoot;
    readonly DispatcherTimer _videoListDebounce;

    sealed class VideoItem
    {
        public string Display { get; init; } = "";
        public string Path { get; init; } = "";
    }

    public MainWindow()
    {
        InitializeComponent();
        ComboModel.ItemsSource = RembgModelCatalog.AllPopupLabels;
        SliderErode.ValueChanged += (_, _) =>
        {
            TxtErodeValue.Text = ((int)SliderErode.Value).ToString();
        };
        ChkAlphaMatting.Checked += (_, _) => UpdateErodePanel();
        ChkAlphaMatting.Unchecked += (_, _) => UpdateErodePanel();

        _videoListDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _videoListDebounce.Tick += (_, _) =>
        {
            _videoListDebounce.Stop();
            if (!_uiReady) return;
            var root = TxtWorkspace.Text.Trim();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            RefreshWorkspaceUi(ComboVideo.SelectedIndex >= 0 ? ComboVideo.SelectedIndex : null);
        };
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = AppSettingsStore.Load();
        TxtWorkspace.Text = _settings.WorkspaceRoot;
        TxtWidth.Text = _settings.Width.ToString();
        TxtHeight.Text = _settings.Height.ToString();
        TxtFps.Text = _settings.TargetFps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ChkMirror.IsChecked = _settings.MirrorHorizontally;
        TxtRembg.Text = _settings.RembgPath ?? "";
        if (!string.IsNullOrWhiteSpace(_settings.FfmpegPath))
            TxtFfmpeg.Text = _settings.FfmpegPath!;
        else
        {
            var auto = FfmpegLocator.FindFfmpegExecutable();
            TxtFfmpeg.Text = auto ?? "";
        }

        ComboModel.SelectedIndex = Math.Clamp(_settings.RembgModelIndex, 0,
            RembgModelCatalog.AllPopupLabels.Length - 1);
        TxtCustomModel.Text = _settings.RembgCustomModelId;
        ChkAlphaMatting.IsChecked = _settings.RembgAlphaMatting;
        SliderErode.Value = _settings.RembgAlphaErode;
        ChkPostProcess.IsChecked = _settings.RembgPostProcessMask;
        TxtErodeValue.Text = ((int)SliderErode.Value).ToString();
        UpdateCustomModelPanel();
        UpdateErodePanel();

        _uiReady = true;
        RefreshWorkspaceUi(selectVideoIndex: _settings.SelectedVideoIndex);
        UpdateFfmpegInstallPanel();
    }

    void Window_Activated(object sender, EventArgs e)
    {
        if (!_uiReady) return;
        var root = TxtWorkspace.Text.Trim();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        RefreshWorkspaceUi(ComboVideo.SelectedIndex >= 0 ? ComboVideo.SelectedIndex : null);
    }

    void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        DisposeVideoFolderWatcher();
        _videoListDebounce.Stop();
        PushSettingsFromUi();
        AppSettingsStore.Save(_settings);
    }

    void PushSettingsFromUi()
    {
        _settings.WorkspaceRoot = TxtWorkspace.Text.Trim();
        if (int.TryParse(TxtWidth.Text, out var w)) _settings.Width = w;
        if (int.TryParse(TxtHeight.Text, out var h)) _settings.Height = h;
        if (double.TryParse(TxtFps.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var fps))
            _settings.TargetFps = fps;
        _settings.MirrorHorizontally = ChkMirror.IsChecked == true;
        _settings.RembgPath = string.IsNullOrWhiteSpace(TxtRembg.Text) ? null : TxtRembg.Text.Trim();
        _settings.FfmpegPath = string.IsNullOrWhiteSpace(TxtFfmpeg.Text) ? null : TxtFfmpeg.Text.Trim();
        _settings.RembgModelIndex = ComboModel.SelectedIndex >= 0 ? ComboModel.SelectedIndex : 0;
        _settings.RembgCustomModelId = TxtCustomModel.Text ?? "";
        _settings.RembgAlphaMatting = ChkAlphaMatting.IsChecked == true;
        _settings.RembgAlphaErode = (int)SliderErode.Value;
        _settings.RembgPostProcessMask = ChkPostProcess.IsChecked == true;
        if (ComboVideo.SelectedIndex >= 0)
            _settings.SelectedVideoIndex = ComboVideo.SelectedIndex;
    }

    void TxtWorkspace_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady) return;
        RefreshWorkspaceUi(selectVideoIndex: null);
    }

    void BtnBrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        dlg.SelectedPath = string.IsNullOrWhiteSpace(TxtWorkspace.Text)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : TxtWorkspace.Text.Trim();
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxtWorkspace.Text = dlg.SelectedPath;
    }

    void ComboVideo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        if (ComboVideo.SelectedIndex >= 0)
            _settings.SelectedVideoIndex = ComboVideo.SelectedIndex;
    }

    void ComboModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCustomModelPanel();
    }

    void UpdateCustomModelPanel()
    {
        var idx = ComboModel.SelectedIndex;
        PanelCustomModel.Visibility = RembgModelCatalog.IsCustomPopupIndex(idx)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    void UpdateErodePanel()
    {
        PanelErode.IsEnabled = ChkAlphaMatting.IsChecked == true;
    }

    void RefreshWorkspaceUi(int? selectVideoIndex)
    {
        var root = TxtWorkspace.Text.Trim();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            DisposeVideoFolderWatcher();
            TxtPathVideo.Text = "";
            TxtPathFrame.Text = "";
            TxtPathMatted.Text = "";
            ComboVideo.ItemsSource = null;
            TxtVideoHint.Text = "请先设置有效的工作路径。";
            BtnVideoToFrame.IsEnabled = false;
            BtnVideoToFrameMatted.IsEnabled = false;
            BtnFrameToMatted.IsEnabled = false;
            return;
        }

        WorkspaceLayout.FixLegacyNamesAndEnsureFolders(root);
        TxtPathVideo.Text = $"{WorkspaceLayout.VideoFolderName}: {WorkspaceLayout.VideoDir(root)}";
        TxtPathFrame.Text = $"{WorkspaceLayout.FrameFolderName}: {WorkspaceLayout.FrameDir(root)}";
        TxtPathMatted.Text = $"{WorkspaceLayout.MattedFolderName}: {WorkspaceLayout.MattedDir(root)}";

        var paths = WorkspaceLayout.ListMp4InVideo(root);
        var items = new ObservableCollection<VideoItem>(
            paths.Select(p => new VideoItem { Display = Path.GetFileName(p), Path = p }));
        ComboVideo.ItemsSource = items;

        var idx = selectVideoIndex ?? _settings.SelectedVideoIndex;
        idx = Math.Clamp(idx, 0, Math.Max(0, items.Count - 1));
        if (items.Count > 0)
            ComboVideo.SelectedIndex = idx;

        var hasVideo = items.Count > 0;
        var hasFrames = WorkspaceLayout.HasFrameImages(root);
        BtnVideoToFrame.IsEnabled = hasVideo;
        BtnVideoToFrameMatted.IsEnabled = hasVideo;
        BtnFrameToMatted.IsEnabled = hasFrames;

        if (!hasVideo)
            TxtVideoHint.Text = "Video 中暂无 MP4。可将已有图片放入 Frame，使用「Frame → Matted」抠图。";
        else
            TxtVideoHint.Text = $"共 {items.Count} 个视频文件。";

        EnsureVideoFolderWatcher(root);
    }

    void EnsureVideoFolderWatcher(string workspaceRoot)
    {
        workspaceRoot = Path.GetFullPath(workspaceRoot.Trim());
        if (string.Equals(_videoWatcherWorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase) &&
            _videoFolderWatcher != null)
            return;

        DisposeVideoFolderWatcher();
        _videoWatcherWorkspaceRoot = workspaceRoot;

        var videoDir = WorkspaceLayout.VideoDir(workspaceRoot);
        if (!Directory.Exists(videoDir))
            return;

        try
        {
            var w = new FileSystemWatcher(videoDir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size |
                               NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                Filter = "*.*",
                EnableRaisingEvents = true
            };
            w.Created += OnVideoFolderFilesystemChanged;
            w.Deleted += OnVideoFolderFilesystemChanged;
            w.Renamed += OnVideoFolderRenamed;
            w.Changed += OnVideoFolderFilesystemChanged;
            _videoFolderWatcher = w;
        }
        catch
        {
            _videoFolderWatcher = null;
        }
    }

    void DisposeVideoFolderWatcher()
    {
        _videoWatcherWorkspaceRoot = null;
        if (_videoFolderWatcher == null) return;
        try
        {
            _videoFolderWatcher.EnableRaisingEvents = false;
            _videoFolderWatcher.Created -= OnVideoFolderFilesystemChanged;
            _videoFolderWatcher.Deleted -= OnVideoFolderFilesystemChanged;
            _videoFolderWatcher.Renamed -= OnVideoFolderRenamed;
            _videoFolderWatcher.Changed -= OnVideoFolderFilesystemChanged;
            _videoFolderWatcher.Dispose();
        }
        catch
        {
            // ignore
        }

        _videoFolderWatcher = null;
    }

    void OnVideoFolderFilesystemChanged(object sender, FileSystemEventArgs e) =>
        RequestDebouncedVideoListRefresh();

    void OnVideoFolderRenamed(object sender, RenamedEventArgs e) =>
        RequestDebouncedVideoListRefresh();

    void RequestDebouncedVideoListRefresh()
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _videoListDebounce.Stop();
                _videoListDebounce.Start();
            }), DispatcherPriority.Background);
        }
        catch
        {
            // ignore (shutting down)
        }
    }

    string? ResolveFfmpegPath()
    {
        var manual = TxtFfmpeg.Text.Trim();
        if (!string.IsNullOrEmpty(manual) && File.Exists(manual))
            return manual;
        return FfmpegLocator.FindFfmpegExecutable();
    }

    void TxtFfmpeg_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady) return;
        UpdateFfmpegInstallPanel();
    }

    void UpdateFfmpegInstallPanel()
    {
        var ok = ResolveFfmpegPath() != null;
        PanelFfmpegMissing.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        BtnWingetFfmpeg.Visibility = FfmpegInstaller.FindWingetPath() != null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    async void BtnDownloadFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var baseDir = AppContext.BaseDirectory;
        var targetExe = Path.Combine(baseDir, "ffmpeg.exe");
        if (File.Exists(targetExe))
        {
            var r = System.Windows.MessageBox.Show(
                "程序目录下已存在 ffmpeg.exe，是否覆盖为刚下载的版本？",
                "确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes)
                return;
        }

        SetBusy(true);
        TxtStatus.Text = "正在下载 FFmpeg…";
        try
        {
            var progress = new Progress<(long bytesRead, long? totalLength)>(t =>
            {
                if (t.totalLength is long total && total > 0)
                {
                    var pct = (int)Math.Clamp(100.0 * t.bytesRead / total, 0, 100);
                    TxtStatus.Text = $"正在下载 FFmpeg… {pct}%";
                }
                else
                    TxtStatus.Text = $"正在下载 FFmpeg… {t.bytesRead / 1024 / 1024} MB";
            });

            await FfmpegInstaller.InstallPortableAsync(baseDir, progress).ConfigureAwait(true);

            TxtFfmpeg.Text = "";
            PushSettingsFromUi();
            AppSettingsStore.Save(_settings);
            UpdateFfmpegInstallPanel();
            System.Windows.MessageBox.Show(
                "已将 ffmpeg.exe 解压到程序目录。上方路径已清空，将自动使用该文件。",
                "完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "下载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TxtStatus.Text = "";
            SetBusy(false);
            UpdateFfmpegInstallPanel();
        }
    }

    void BtnWingetFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        if (!FfmpegInstaller.TryStartWingetInstallGyanFfmpeg())
        {
            System.Windows.MessageBox.Show(
                "未在 PATH 中找到 winget.exe。请从 Microsoft Store 安装「应用安装程序」，或手动安装 FFmpeg。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        System.Windows.MessageBox.Show(
            "已尝试启动 winget 安装。请在打开的窗口中完成步骤；结束后请重启本程序，或清空 FFmpeg 路径以从 PATH 自动查找。",
            "提示",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    RembgRunner.Options BuildRembgOptions(string frameDir, string mattedDir)
    {
        return new RembgRunner.Options
        {
            InputFolder = frameDir,
            OutputFolder = mattedDir,
            RembgExecutable = string.IsNullOrWhiteSpace(TxtRembg.Text) ? null : TxtRembg.Text.Trim(),
            Model = RembgModelCatalog.ResolveModelId(ComboModel.SelectedIndex, TxtCustomModel.Text),
            AlphaMatting = ChkAlphaMatting.IsChecked == true,
            AlphaMattingErodeSize = (int)SliderErode.Value,
            PostProcessMask = ChkPostProcess.IsChecked == true
        };
    }

    async void BtnVideoToFrame_Click(object sender, RoutedEventArgs e)
    {
        await RunExportAsync(matteAfter: false);
    }

    async void BtnVideoToFrameMatted_Click(object sender, RoutedEventArgs e)
    {
        await RunExportAsync(matteAfter: true);
    }

    async void BtnFrameToMatted_Click(object sender, RoutedEventArgs e)
    {
        await RunMatteOnlyAsync();
    }

    async Task RunExportAsync(bool matteAfter)
    {
        PushSettingsFromUi();
        var root = Path.GetFullPath(TxtWorkspace.Text.Trim());
        if (!Directory.Exists(root))
        {
            System.Windows.MessageBox.Show("工作路径无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ComboVideo.SelectedItem is not VideoItem vi || !File.Exists(vi.Path))
        {
            System.Windows.MessageBox.Show("请先在 Video 文件夹中放入 MP4 并选择要导出的文件。", "提示", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(TxtWidth.Text, out var w) || !int.TryParse(TxtHeight.Text, out var h) ||
            w < 16 || h < 16)
        {
            System.Windows.MessageBox.Show("宽高必须为不小于 16 的整数。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!double.TryParse(TxtFps.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var fps) || fps <= 0)
        {
            System.Windows.MessageBox.Show("帧率必须大于 0。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ffmpeg = ResolveFfmpegPath();
        if (ffmpeg == null)
        {
            System.Windows.MessageBox.Show("未找到 ffmpeg.exe。请放在程序同目录、加入 PATH，或在 FFmpeg 栏填写路径。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        WorkspaceLayout.FixLegacyNamesAndEnsureFolders(root);
        var frameDir = WorkspaceLayout.FrameDir(root);
        var mattedDir = WorkspaceLayout.MattedDir(root);

        SetBusy(true);
        try
        {
            TxtStatus.Text = "正在导出序列帧…";
            var progress = new Progress<string>(s =>
                Dispatcher.BeginInvoke(() => TxtStatus.Text = s));
            var count = await FfmpegFrameExtractor.ExtractAsync(
                ffmpeg, vi.Path, frameDir, w, h, fps, ChkMirror.IsChecked == true, progress);

            if (matteAfter)
            {
                TxtStatus.Text = "正在抠图…";
                await RembgRunner.RunFolderAsync(BuildRembgOptions(frameDir, mattedDir));
                System.Windows.MessageBox.Show($"已导出 {count} 张到 Frame，并完成抠图到 Matted。", "完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show($"已导出 {count} 张 PNG 到 Frame。", "完成", MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            RefreshWorkspaceUi(ComboVideo.SelectedIndex);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            TxtStatus.Text = "";
            RefreshWorkspaceUi(ComboVideo.SelectedIndex >= 0 ? ComboVideo.SelectedIndex : null);
        }
    }

    async Task RunMatteOnlyAsync()
    {
        PushSettingsFromUi();
        var root = Path.GetFullPath(TxtWorkspace.Text.Trim());
        if (!Directory.Exists(root))
        {
            System.Windows.MessageBox.Show("工作路径无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        WorkspaceLayout.FixLegacyNamesAndEnsureFolders(root);
        var frameDir = WorkspaceLayout.FrameDir(root);
        var mattedDir = WorkspaceLayout.MattedDir(root);

        if (!WorkspaceLayout.HasFrameImages(root))
        {
            System.Windows.MessageBox.Show("Frame 中没有 png/jpg。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            TxtStatus.Text = "正在抠图…";
            await RembgRunner.RunFolderAsync(BuildRembgOptions(frameDir, mattedDir));
            System.Windows.MessageBox.Show("已从 Frame 抠图并写入 Matted。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            TxtStatus.Text = "";
            RefreshWorkspaceUi(ComboVideo.SelectedIndex >= 0 ? ComboVideo.SelectedIndex : null);
        }
    }

    void SetBusy(bool busy)
    {
        ProgressIndeterminate.IsIndeterminate = busy;
        ProgressIndeterminate.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        var root = TxtWorkspace.Text.Trim();
        var rootOk = !string.IsNullOrEmpty(root) && Directory.Exists(root);
        var nVideo = ComboVideo.Items.Count;
        var hasFrames = rootOk && WorkspaceLayout.HasFrameImages(root);
        BtnVideoToFrame.IsEnabled = !busy && nVideo > 0;
        BtnVideoToFrameMatted.IsEnabled = !busy && nVideo > 0;
        BtnFrameToMatted.IsEnabled = !busy && hasFrames;
        TxtWorkspace.IsEnabled = !busy;
        BtnBrowseWorkspace.IsEnabled = !busy;
        ComboVideo.IsEnabled = !busy;
        TxtFfmpeg.IsEnabled = !busy;
        TxtWidth.IsEnabled = !busy;
        TxtHeight.IsEnabled = !busy;
        TxtFps.IsEnabled = !busy;
        ChkMirror.IsEnabled = !busy;
        TxtRembg.IsEnabled = !busy;
        ComboModel.IsEnabled = !busy;
        TxtCustomModel.IsEnabled = !busy;
        ChkAlphaMatting.IsEnabled = !busy;
        SliderErode.IsEnabled = !busy && ChkAlphaMatting.IsChecked == true;
        ChkPostProcess.IsEnabled = !busy;
        BtnDownloadFfmpeg.IsEnabled = !busy && ResolveFfmpegPath() == null;
        BtnWingetFfmpeg.IsEnabled = !busy && FfmpegInstaller.FindWingetPath() != null;
    }
}
