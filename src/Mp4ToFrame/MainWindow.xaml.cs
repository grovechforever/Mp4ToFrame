using System.Collections.ObjectModel;
using System.Globalization;
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
    bool _uiBusy;
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

        for (var p = 1; p <= 8; p++)
            ComboRembgParallel.Items.Add(p);
        ComboRembgParallel.SelectedIndex = 0;

        ComboMatteMode.Items.Add("rembg（神经网络）");
        ComboMatteMode.Items.Add("纯色底色键（FFmpeg colorkey）");
        ComboMatteMode.SelectedIndex = 0;

        ComboWhiteKeyDespill.Items.Add("自动（绿/蓝幕去边）");
        ComboWhiteKeyDespill.Items.Add("绿幕去边 (despill green=-1)");
        ComboWhiteKeyDespill.Items.Add("蓝幕去边 (despill blue=-1)");
        ComboWhiteKeyDespill.Items.Add("关闭");
        ComboWhiteKeyDespill.SelectedIndex = 0;

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
        TxtFps.Text = _settings.TargetFps.ToString(CultureInfo.InvariantCulture);
        ChkMirror.IsChecked = _settings.MirrorHorizontally;
        var inv = CultureInfo.InvariantCulture;
        TxtBorderL.Text = _settings.BorderLeft.ToString(inv);
        TxtBorderR.Text = _settings.BorderRight.ToString(inv);
        TxtBorderT.Text = _settings.BorderTop.ToString(inv);
        TxtBorderB.Text = _settings.BorderBottom.ToString(inv);
        TxtBorderFillArgb.Text = _settings.BorderFillArgb == 0
            ? "00000000"
            : _settings.BorderFillArgb.ToString("X8", inv);
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
        TxtRembgAf.Text = _settings.RembgAlphaFg.ToString(inv);
        TxtRembgAb.Text = _settings.RembgAlphaBg.ToString(inv);
        ChkPostProcess.IsChecked = _settings.RembgPostProcessMask;
        ComboRembgParallel.SelectedIndex = Math.Clamp(_settings.RembgParallelJobs - 1, 0, 7);
        ComboMatteMode.SelectedIndex = _settings.MatteMode == 1 ? 1 : 0;
        TxtWhiteKeyRgb.Text = (_settings.WhiteKeyRgb & 0xFFFFFF).ToString("X6", inv);
        TxtWhiteKeySimilarity.Text = _settings.WhiteKeySimilarity.ToString("0.###", inv);
        TxtWhiteKeyBlend.Text = _settings.WhiteKeyBlend.ToString("0.###", inv);
        ComboWhiteKeyDespill.SelectedIndex = Math.Clamp(_settings.WhiteKeyDespillMode, 0, 3);
        TxtMattedStemSuffix.Text = _settings.MattedOutputStemSuffix ?? "";
        TxtFinalStemSuffix.Text = _settings.FinalOutputStemSuffix ?? "";
        TxtErodeValue.Text = ((int)SliderErode.Value).ToString();
        TxtFinalWidth.Text = _settings.FinalExportWidth.ToString(inv);
        TxtFinalHeight.Text = _settings.FinalExportHeight.ToString(inv);
        TxtFinalOutputFolder.Text = _settings.FinalExportOutputFolder ?? "";
        TxtFinalBorderL.Text = _settings.FinalBorderLeft.ToString(inv);
        TxtFinalBorderR.Text = _settings.FinalBorderRight.ToString(inv);
        TxtFinalBorderT.Text = _settings.FinalBorderTop.ToString(inv);
        TxtFinalBorderB.Text = _settings.FinalBorderBottom.ToString(inv);
        TxtFinalBorderFillArgb.Text = _settings.FinalBorderFillArgb == 0
            ? "00000000"
            : _settings.FinalBorderFillArgb.ToString("X8", inv);
        UpdateCustomModelPanel();
        UpdateMatteModePanel();

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
        var inv = CultureInfo.InvariantCulture;
        _settings.WorkspaceRoot = TxtWorkspace.Text.Trim();
        if (int.TryParse(TxtWidth.Text, out var w)) _settings.Width = w;
        if (int.TryParse(TxtHeight.Text, out var h)) _settings.Height = h;
        if (double.TryParse(TxtFps.Text, System.Globalization.NumberStyles.Float, inv, out var fps))
            _settings.TargetFps = fps;
        _settings.MirrorHorizontally = ChkMirror.IsChecked == true;
        if (TryParseBorderInt(TxtBorderL.Text, out var bl)) _settings.BorderLeft = bl;
        if (TryParseBorderInt(TxtBorderR.Text, out var br)) _settings.BorderRight = br;
        if (TryParseBorderInt(TxtBorderT.Text, out var bt)) _settings.BorderTop = bt;
        if (TryParseBorderInt(TxtBorderB.Text, out var bb)) _settings.BorderBottom = bb;
        if (TryParseArgbHex(TxtBorderFillArgb.Text, out var argb)) _settings.BorderFillArgb = argb;
        _settings.RembgPath = string.IsNullOrWhiteSpace(TxtRembg.Text) ? null : TxtRembg.Text.Trim();
        _settings.FfmpegPath = string.IsNullOrWhiteSpace(TxtFfmpeg.Text) ? null : TxtFfmpeg.Text.Trim();
        _settings.RembgModelIndex = ComboModel.SelectedIndex >= 0 ? ComboModel.SelectedIndex : 0;
        _settings.RembgCustomModelId = TxtCustomModel.Text ?? "";
        _settings.RembgAlphaMatting = ChkAlphaMatting.IsChecked == true;
        _settings.RembgAlphaErode = (int)SliderErode.Value;
        if (int.TryParse(TxtRembgAf.Text, NumberStyles.Integer, inv, out var af) && af is >= 1 and <= 255)
            _settings.RembgAlphaFg = af;
        if (int.TryParse(TxtRembgAb.Text, NumberStyles.Integer, inv, out var ab) && ab is >= 0 and <= 255)
            _settings.RembgAlphaBg = ab;
        _settings.RembgPostProcessMask = ChkPostProcess.IsChecked == true;
        _settings.RembgParallelJobs = ComboRembgParallel.SelectedIndex >= 0 ? ComboRembgParallel.SelectedIndex + 1 : 1;
        _settings.MatteMode = ComboMatteMode.SelectedIndex == 1 ? 1 : 0;
        if (TryParseRgbHex6(TxtWhiteKeyRgb.Text, out var wRgb))
            _settings.WhiteKeyRgb = wRgb & 0xFFFFFF;
        if (double.TryParse(TxtWhiteKeySimilarity.Text, NumberStyles.Float, inv, out var wSim) && wSim > 0 && wSim <= 1)
            _settings.WhiteKeySimilarity = wSim;
        if (double.TryParse(TxtWhiteKeyBlend.Text, NumberStyles.Float, inv, out var wBlend) && wBlend >= 0 && wBlend <= 1)
            _settings.WhiteKeyBlend = wBlend;
        _settings.WhiteKeyDespillMode = ComboWhiteKeyDespill.SelectedIndex >= 0
            ? Math.Clamp(ComboWhiteKeyDespill.SelectedIndex, 0, 3)
            : 0;
        _settings.MattedOutputStemSuffix = TxtMattedStemSuffix.Text?.Trim() ?? "";
        _settings.FinalOutputStemSuffix = TxtFinalStemSuffix.Text?.Trim() ?? "";
        if (int.TryParse(TxtFinalWidth.Text, out var fw)) _settings.FinalExportWidth = fw;
        if (int.TryParse(TxtFinalHeight.Text, out var fh)) _settings.FinalExportHeight = fh;
        _settings.FinalExportOutputFolder = string.IsNullOrWhiteSpace(TxtFinalOutputFolder.Text)
            ? ""
            : TxtFinalOutputFolder.Text.Trim();
        if (TryParseBorderInt(TxtFinalBorderL.Text, out var fbl)) _settings.FinalBorderLeft = fbl;
        if (TryParseBorderInt(TxtFinalBorderR.Text, out var fbr)) _settings.FinalBorderRight = fbr;
        if (TryParseBorderInt(TxtFinalBorderT.Text, out var fbt)) _settings.FinalBorderTop = fbt;
        if (TryParseBorderInt(TxtFinalBorderB.Text, out var fbb)) _settings.FinalBorderBottom = fbb;
        if (TryParseArgbHex(TxtFinalBorderFillArgb.Text, out var fbfill)) _settings.FinalBorderFillArgb = fbfill;
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

    void ComboMatteMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        UpdateMatteModePanel();
    }

    void UpdateMatteModePanel()
    {
        var rembg = ComboMatteMode.SelectedIndex == 0;
        PanelRembgDetails.Visibility = rembg ? Visibility.Visible : Visibility.Collapsed;
        PanelWhiteKeyParams.Visibility = rembg ? Visibility.Collapsed : Visibility.Visible;
        PanelRembgDetails.IsEnabled = rembg && !_uiBusy;
        PanelWhiteKeyParams.IsEnabled = !rembg && !_uiBusy;
        UpdateErodePanel();
    }

    void UpdateErodePanel()
    {
        PanelErode.IsEnabled = ChkAlphaMatting.IsChecked == true && !_uiBusy;
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
            TxtPathFinal.Text = "";
            ComboVideo.ItemsSource = null;
            TxtVideoHint.Text = "请先设置有效的工作路径。";
            BtnVideoToFrame.IsEnabled = false;
            BtnVideoToFrameMatted.IsEnabled = false;
            BtnFrameToMatted.IsEnabled = false;
            BtnMattedToFinal.IsEnabled = false;
            return;
        }

        WorkspaceLayout.FixLegacyNamesAndEnsureFolders(root);
        TxtPathVideo.Text = $"{WorkspaceLayout.VideoFolderName}: {WorkspaceLayout.VideoDir(root)}";
        TxtPathFrame.Text = $"{WorkspaceLayout.FrameFolderName}: {WorkspaceLayout.FrameDir(root)}";
        TxtPathMatted.Text = $"{WorkspaceLayout.MattedFolderName}: {WorkspaceLayout.MattedDir(root)}";
        var defaultFinal = WorkspaceLayout.FinalExportDir(root);
        TxtPathFinal.Text =
            $"{WorkspaceLayout.FinalExportFolderName}（默认输出）: {defaultFinal}";

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
        var hasMatted = WorkspaceLayout.HasMattedImages(root);
        BtnVideoToFrame.IsEnabled = hasVideo;
        BtnVideoToFrameMatted.IsEnabled = hasVideo;
        BtnFrameToMatted.IsEnabled = hasFrames;
        BtnMattedToFinal.IsEnabled = hasMatted;

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

    async Task ExecuteMatteFrameToMattedAsync(string frameDir, string mattedDir, IProgress<string> progress)
    {
        if (ComboMatteMode.SelectedIndex == 1)
        {
            var ffmpeg = ResolveFfmpegPath();
            if (ffmpeg == null)
                throw new InvalidOperationException("未找到 ffmpeg.exe。");
            if (!TryBuildWhiteKeyOptions(frameDir, mattedDir, out var wopt, out var err) || wopt == null)
                throw new InvalidOperationException(err ?? "色键参数无效。");
            await WhiteKeyMatteRunner.RunFolderAsync(ffmpeg, wopt, progress, cancellationToken: default)
                .ConfigureAwait(true);
        }
        else
        {
            await RembgRunner.RunFolderAsync(BuildRembgOptions(frameDir, mattedDir), cancellationToken: default, progress)
                .ConfigureAwait(true);
        }
    }

    bool TryBuildWhiteKeyOptions(string frameDir, string mattedDir, out WhiteKeyMatteRunner.Options? opt,
        out string? errorMessage)
    {
        opt = null;
        var inv = CultureInfo.InvariantCulture;
        if (!TryParseRgbHex6(TxtWhiteKeyRgb.Text, out var rgb))
        {
            errorMessage = "键控颜色须为 6 位 RRGGBB（可带 #），例如 FFFFFF。";
            return false;
        }

        if (!double.TryParse(TxtWhiteKeySimilarity.Text, NumberStyles.Float, inv, out var sim) || sim <= 0 || sim > 1)
        {
            errorMessage = "similarity 须为小数，范围 (0, 1]，例如 0.08。";
            return false;
        }

        if (!double.TryParse(TxtWhiteKeyBlend.Text, NumberStyles.Float, inv, out var blend) || blend < 0 || blend > 1)
        {
            errorMessage = "blend 须为小数，范围 [0, 1]，例如 0.04。";
            return false;
        }

        opt = new WhiteKeyMatteRunner.Options
        {
            InputFolder = frameDir,
            OutputFolder = mattedDir,
            KeyR = (int)((rgb >> 16) & 255),
            KeyG = (int)((rgb >> 8) & 255),
            KeyB = (int)(rgb & 255),
            Similarity = sim,
            Blend = blend,
            OutputStemSuffix = string.IsNullOrWhiteSpace(TxtMattedStemSuffix.Text)
                ? null
                : TxtMattedStemSuffix.Text.Trim(),
            DespillMode = ComboWhiteKeyDespill.SelectedIndex >= 0
                ? Math.Clamp(ComboWhiteKeyDespill.SelectedIndex, 0, 3)
                : 0
        };
        errorMessage = null;
        return true;
    }

    RembgRunner.Options BuildRembgOptions(string frameDir, string mattedDir)
    {
        var jobs = ComboRembgParallel.SelectedIndex >= 0 ? ComboRembgParallel.SelectedIndex + 1 : 1;
        return new RembgRunner.Options
        {
            InputFolder = frameDir,
            OutputFolder = mattedDir,
            RembgExecutable = string.IsNullOrWhiteSpace(TxtRembg.Text) ? null : TxtRembg.Text.Trim(),
            Model = RembgModelCatalog.ResolveModelId(ComboModel.SelectedIndex, TxtCustomModel.Text),
            AlphaMatting = ChkAlphaMatting.IsChecked == true,
            AlphaMattingErodeSize = (int)SliderErode.Value,
            AlphaMattingForegroundThreshold = ParseRembgAf(TxtRembgAf.Text, _settings.RembgAlphaFg),
            AlphaMattingBackgroundThreshold = ParseRembgAb(TxtRembgAb.Text, _settings.RembgAlphaBg),
            PostProcessMask = ChkPostProcess.IsChecked == true,
            ParallelJobs = jobs,
            OutputStemSuffix = string.IsNullOrWhiteSpace(TxtMattedStemSuffix.Text)
                ? null
                : TxtMattedStemSuffix.Text.Trim()
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

    void BtnBrowseFinalOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        dlg.SelectedPath = string.IsNullOrWhiteSpace(TxtFinalOutputFolder.Text)
            ? (string.IsNullOrWhiteSpace(TxtWorkspace.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : TxtWorkspace.Text.Trim())
            : TxtFinalOutputFolder.Text.Trim();
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxtFinalOutputFolder.Text = dlg.SelectedPath;
    }

    async void BtnMattedToFinal_Click(object sender, RoutedEventArgs e)
    {
        await RunMattedToFinalAsync();
    }

    string ResolveFinalOutputDirectory(string workspaceRoot)
    {
        var custom = TxtFinalOutputFolder.Text.Trim();
        if (string.IsNullOrEmpty(custom))
            return WorkspaceLayout.FinalExportDir(workspaceRoot);
        return Path.GetFullPath(custom);
    }

    async Task RunMattedToFinalAsync()
    {
        PushSettingsFromUi();
        var root = Path.GetFullPath(TxtWorkspace.Text.Trim());
        if (!Directory.Exists(root))
        {
            System.Windows.MessageBox.Show("工作路径无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(TxtFinalWidth.Text, out var fw) || !int.TryParse(TxtFinalHeight.Text, out var fh) ||
            fw < 16 || fh < 16)
        {
            System.Windows.MessageBox.Show("最终导出宽高须为不小于 16 的整数。", "提示", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var ffmpeg = ResolveFfmpegPath();
        if (ffmpeg == null)
        {
            System.Windows.MessageBox.Show("未找到 ffmpeg.exe。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        WorkspaceLayout.FixLegacyNamesAndEnsureFolders(root);
        var mattedDir = WorkspaceLayout.MattedDir(root);
        var finalDir = ResolveFinalOutputDirectory(root);

        if (string.Equals(Path.GetFullPath(mattedDir), Path.GetFullPath(finalDir), StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show("输出文件夹不能与 Matted 相同，请指定其他目录或留空使用 Final。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!WorkspaceLayout.HasMattedImages(root))
        {
            System.Windows.MessageBox.Show("Matted 中没有 png/jpg。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseBorderInt(TxtFinalBorderL.Text, out var fbL) || !TryParseBorderInt(TxtFinalBorderR.Text, out var fbR) ||
            !TryParseBorderInt(TxtFinalBorderT.Text, out var fbT) || !TryParseBorderInt(TxtFinalBorderB.Text, out var fbB))
        {
            System.Windows.MessageBox.Show("最终导出 Border L/R/T/B 须为整数（可为负）。", "提示", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!TryParseArgbHex(TxtFinalBorderFillArgb.Text, out var fbFillArgb))
        {
            System.Windows.MessageBox.Show("最终导出外扩填充须为 6 或 8 位十六进制（AARRGGBB 或 RRGGBB），可带 #。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            FfmpegFrameExtractor.ValidateBorder(fw, fh, fbL, fbR, fbT, fbB);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? finalStemSuffix;
        try
        {
            finalStemSuffix = string.IsNullOrWhiteSpace(TxtFinalStemSuffix.Text) ? null : TxtFinalStemSuffix.Text.Trim();
            RembgRunner.ValidateOutputStemSuffix(finalStemSuffix);
        }
        catch (InvalidOperationException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(s =>
                Dispatcher.BeginInvoke(() => TxtStatus.Text = s));
            var n = await MattedFinalExporter.ExportFolderAsync(ffmpeg, mattedDir, finalDir, fw, fh,
                    fbL, fbR, fbT, fbB, fbFillArgb, finalStemSuffix, progress)
                .ConfigureAwait(true);
            System.Windows.MessageBox.Show($"已将 {n} 张图导出到：\n{finalDir}", "完成", MessageBoxButton.OK,
                MessageBoxImage.Information);
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

        if (!double.TryParse(TxtFps.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) || fps <= 0)
        {
            System.Windows.MessageBox.Show("帧率必须大于 0。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseBorderInt(TxtBorderL.Text, out var bL) || !TryParseBorderInt(TxtBorderR.Text, out var bR) ||
            !TryParseBorderInt(TxtBorderT.Text, out var bT) || !TryParseBorderInt(TxtBorderB.Text, out var bB))
        {
            System.Windows.MessageBox.Show("Border L/R/T/B 须为整数（可为负）。", "提示", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!TryParseArgbHex(TxtBorderFillArgb.Text, out var fillArgb))
        {
            System.Windows.MessageBox.Show("外扩填充须为 6 或 8 位十六进制（AARRGGBB 或 RRGGBB），可带 #。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            FfmpegFrameExtractor.ValidateBorder(w, h, bL, bR, bT, bB);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        if (matteAfter)
        {
            try
            {
                var ms = string.IsNullOrWhiteSpace(TxtMattedStemSuffix.Text) ? null : TxtMattedStemSuffix.Text.Trim();
                RembgRunner.ValidateOutputStemSuffix(ms);
            }
            catch (InvalidOperationException ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ComboMatteMode.SelectedIndex == 1)
            {
                if (ResolveFfmpegPath() == null)
                {
                    System.Windows.MessageBox.Show("纯色底色键模式需要 ffmpeg.exe。", "提示", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!TryBuildWhiteKeyOptions(frameDir, mattedDir, out _, out var wkErr))
                {
                    System.Windows.MessageBox.Show(wkErr ?? "色键参数无效。", "提示", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }
        }

        SetBusy(true);
        try
        {
            TxtStatus.Text = "正在导出序列帧…";
            var progress = new Progress<string>(s =>
                Dispatcher.BeginInvoke(() => TxtStatus.Text = s));
            var count = await FfmpegFrameExtractor.ExtractAsync(
                ffmpeg, vi.Path, frameDir, w, h, fps, ChkMirror.IsChecked == true,
                bL, bR, bT, bB, fillArgb, progress);

            if (matteAfter)
            {
                TxtStatus.Text = "正在抠图…";
                var matteProgress = new Progress<string>(s =>
                    Dispatcher.BeginInvoke(() => TxtStatus.Text = s));
                await ExecuteMatteFrameToMattedAsync(frameDir, mattedDir, matteProgress).ConfigureAwait(true);
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

        try
        {
            var ms = string.IsNullOrWhiteSpace(TxtMattedStemSuffix.Text) ? null : TxtMattedStemSuffix.Text.Trim();
            RembgRunner.ValidateOutputStemSuffix(ms);
        }
        catch (InvalidOperationException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ComboMatteMode.SelectedIndex == 1)
        {
            if (ResolveFfmpegPath() == null)
            {
                System.Windows.MessageBox.Show("纯色底色键模式需要 ffmpeg.exe。", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!TryBuildWhiteKeyOptions(frameDir, mattedDir, out _, out var wkErr))
            {
                System.Windows.MessageBox.Show(wkErr ?? "色键参数无效。", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        SetBusy(true);
        try
        {
            TxtStatus.Text = "正在抠图…";
            var matteProgress = new Progress<string>(s =>
                Dispatcher.BeginInvoke(() => TxtStatus.Text = s));
            await ExecuteMatteFrameToMattedAsync(frameDir, mattedDir, matteProgress).ConfigureAwait(true);
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
        _uiBusy = busy;
        ProgressIndeterminate.IsIndeterminate = busy;
        ProgressIndeterminate.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        var root = TxtWorkspace.Text.Trim();
        var rootOk = !string.IsNullOrEmpty(root) && Directory.Exists(root);
        var nVideo = ComboVideo.Items.Count;
        var hasFrames = rootOk && WorkspaceLayout.HasFrameImages(root);
        var hasMatted = rootOk && WorkspaceLayout.HasMattedImages(root);
        BtnVideoToFrame.IsEnabled = !busy && nVideo > 0;
        BtnVideoToFrameMatted.IsEnabled = !busy && nVideo > 0;
        BtnFrameToMatted.IsEnabled = !busy && hasFrames;
        BtnMattedToFinal.IsEnabled = !busy && hasMatted;
        TxtWorkspace.IsEnabled = !busy;
        BtnBrowseWorkspace.IsEnabled = !busy;
        ComboVideo.IsEnabled = !busy;
        TxtFfmpeg.IsEnabled = !busy;
        TxtWidth.IsEnabled = !busy;
        TxtHeight.IsEnabled = !busy;
        TxtFps.IsEnabled = !busy;
        ChkMirror.IsEnabled = !busy;
        TxtBorderL.IsEnabled = !busy;
        TxtBorderR.IsEnabled = !busy;
        TxtBorderT.IsEnabled = !busy;
        TxtBorderB.IsEnabled = !busy;
        TxtBorderFillArgb.IsEnabled = !busy;
        TxtRembg.IsEnabled = !busy;
        ComboModel.IsEnabled = !busy;
        TxtCustomModel.IsEnabled = !busy;
        ChkAlphaMatting.IsEnabled = !busy;
        ChkPostProcess.IsEnabled = !busy;
        ComboRembgParallel.IsEnabled = !busy;
        ComboMatteMode.IsEnabled = !busy;
        TxtWhiteKeyRgb.IsEnabled = !busy;
        TxtWhiteKeySimilarity.IsEnabled = !busy;
        TxtWhiteKeyBlend.IsEnabled = !busy;
        ComboWhiteKeyDespill.IsEnabled = !busy;
        TxtMattedStemSuffix.IsEnabled = !busy;
        TxtFinalStemSuffix.IsEnabled = !busy;
        TxtFinalWidth.IsEnabled = !busy;
        TxtFinalHeight.IsEnabled = !busy;
        TxtFinalOutputFolder.IsEnabled = !busy;
        BtnBrowseFinalOutput.IsEnabled = !busy;
        TxtFinalBorderL.IsEnabled = !busy;
        TxtFinalBorderR.IsEnabled = !busy;
        TxtFinalBorderT.IsEnabled = !busy;
        TxtFinalBorderB.IsEnabled = !busy;
        TxtFinalBorderFillArgb.IsEnabled = !busy;
        BtnDownloadFfmpeg.IsEnabled = !busy && ResolveFfmpegPath() == null;
        BtnWingetFfmpeg.IsEnabled = !busy && FfmpegInstaller.FindWingetPath() != null;
        UpdateMatteModePanel();
    }

    /// <summary>解析 6 位 RRGGBB（可带 #），不含 Alpha。</summary>
    static bool TryParseRgbHex6(string? s, out uint rgb)
    {
        rgb = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (t.StartsWith('#')) t = t[1..];
        if (t.Length != 6)
            return false;
        if (!uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return false;
        if (v > 0xFFFFFF)
            return false;
        rgb = v;
        return true;
    }

    static int ParseRembgAf(string? text, int fallback)
    {
        if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) &&
            v is >= 1 and <= 255)
            return v;
        return Math.Clamp(fallback, 1, 255);
    }

    static int ParseRembgAb(string? text, int fallback)
    {
        if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) &&
            v is >= 0 and <= 255)
            return v;
        return Math.Clamp(fallback, 0, 255);
    }

    static bool TryParseBorderInt(string? s, out int v)
    {
        v = 0;
        if (string.IsNullOrWhiteSpace(s)) return true;
        return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>空=全透明；6 位 RRGGBB 按不透明；8 位 AARRGGBB。</summary>
    static bool TryParseArgbHex(string? s, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(s)) return true;
        var t = s.Trim();
        if (t.StartsWith('#')) t = t[1..];
        if (t.Length == 6)
        {
            if (!uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return false;
            argb = 0xFF000000u | (rgb & 0xFFFFFFu);
            return true;
        }

        if (t.Length == 8)
            return uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb);
        return false;
    }
}
