//==================================================
// MainForm
// アプリの画面。録画の開始・停止、プレビューと切り取り、書き出し設定、
// 保存したファイルの受け渡しを担当する。
//==================================================

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms.Integration;
using NAudio.Wave;
using System.Windows.Media.Animation;
using MediaElement = System.Windows.Controls.MediaElement;
using MediaState = System.Windows.Controls.MediaState;
using WpfGrid = System.Windows.Controls.Grid;
using WpfCanvas = System.Windows.Controls.Canvas;
using WpfRectangleShape = System.Windows.Shapes.Rectangle;
using WpfPathShape = System.Windows.Shapes.Path;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfRectangleGeometry = System.Windows.Media.RectangleGeometry;
using WpfCombinedGeometry = System.Windows.Media.CombinedGeometry;
using WpfGeometryCombineMode = System.Windows.Media.GeometryCombineMode;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace uskCompressionRecorder;

public sealed class MainForm : Form
{
    private const int HotkeyId = 0xD15C;
    private const int WmHotkey = 0x0312;
    private const int WmSetRedraw = 0x000B;
    private const int SmCxVScroll = 2;
    private const int MaxSeconds = 30;
    private const int RecordSafetySeconds = 8; // ffmpegが自分で止まらないときの保険(録画開始の待ち時間ぶん)

    // 画面の並び(100%表示の値)。実際の大きさは LogicalToDeviceUnits で今の表示倍率へ換算する
    private const int BaseClientWidth = 572;
    private const int SetupCardTop = 88;
    private const int SetupCardHeight = 416;
    private const int CardGap = 16;
    private const int EditorHeight = 661;
    private const int FileCardGap = 12;
    private const int FileCardHeight = 96;
    private const int FullBottomMargin = 16;
    private const int FadeInMilliseconds = 200;
    private const int FadeGuardMilliseconds = 1500;

    private readonly Button recordButton = new();
    private readonly Button folderButton = new();
    private readonly Button refreshTargetsButton = new();
    private readonly Button exportButton = new();
    private readonly DecimalInput maxSize = new();
    private readonly ToggleSwitch limitEnabled = new();
    private readonly DropdownSelect captureTarget = new();
    private readonly ToggleSwitch audioEnabled = new();
    private readonly Panel setupCard = new();
    private readonly Label status = new();
    private readonly Label timerLabel = new();
    private readonly Label previewTime = new();
    private readonly Label trimInfo = new();
    private readonly Label resolutionInfo = new();
    private readonly TrimRangeSlider trimSlider = new();
    private readonly Label trimBubble = new();
    private readonly SegmentedSelect formatSelect = new();
    private readonly Label cropInfo = new();
    private readonly Button cropResetButton = new();
    private readonly WpfGrid previewRoot = new();
    private readonly WpfCanvas cropCanvas = new();
    private readonly WpfPathShape cropMask = new();
    private readonly WpfRectangleShape cropRectVisual = new();
    private readonly WpfRectangleShape[] cropHandles = Enumerable.Range(0, 4).Select(_ => new WpfRectangleShape { Width = Theme.CropHandleSize, Height = Theme.CropHandleSize, IsHitTestVisible = false }).ToArray();
    private readonly SeekBar previewSeek = new();
    private readonly PlayPauseButton playButton = new();
    private readonly Button markInButton = new();
    private readonly Button markOutButton = new();
    private readonly Panel editorPanel = new();
    private readonly Panel fileCard = new();
    private readonly Label fileName = new();
    private readonly Label fileInfo = new();
    private readonly Label fileHint = new();
    private readonly ElementHost previewHost = new();
    private readonly MediaElement media = new();
    private readonly ToolTip hintTip = new();
    private readonly System.Windows.Forms.Timer recordTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer previewTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer estimateTimer = new() { Interval = 500 }; // 操作が止まってから出力の予想を計算する
    private CancellationTokenSource? estimateCancellation;
    private bool exporting;
    private bool clipLoaded;                       // 録画した映像を編集できる状態か(画面の並びは録画前から変えず、中身だけ入れ替える)
    private CancellationTokenSource? exportCancellation; // 圧縮中にボタンを押すと取り消す
    private readonly Dictionary<Button, (ColorFader Back, ColorFader Fore)> buttonFaders = [];
    private readonly System.Windows.Controls.TextBlock previewPlaceholder = new();
    private readonly System.Windows.Forms.Timer busyTimer = new() { Interval = 33 }; // 圧縮中の読み込み表示を動かす
    private readonly System.Windows.Forms.Timer fadeTimer = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer fadeGuardTimer = new() { Interval = FadeGuardMilliseconds };
    private readonly AppSettings settings = AppSettings.Load();
    private Process? recorder;
    private WasapiLoopbackCapture? loopbackCapture;
    private WaveFileWriter? loopbackWriter;
    private TaskCompletionSource<bool>? loopbackStopped;
    private DateTime startedAt;
    private string? tempPath;
    private string? finalPath;
    private string? audioTempPath;
    private double recordedDuration;
    private double recordedSeconds; // ffmpegの進行表示から読んだ、実際に録画できた秒数
    private DateTime audioStartedAt; // 音声の録音を始めた時刻
    private long videoStartTicks;    // 映像が撮り始まった時刻(UTCのTicks)。0は未計測
    private bool stopping;
    private bool playing;
    private bool Playing { get => playing; set { playing = value; playButton.Playing = value; } } // 再生ボタンの形も合わせる
    private bool scrubbing; // つまみ・シークバーを動かしている間(再生を止めて位置を見せる)
    private double? pendingSeek;          // まだ映像へ反映していない、最新の再生位置の要求(秒)
    private long lastSeekAt;              // 最後に位置を動かした時刻(TickCount64)。動かした直後は映像の位置が追いつかない
    private double lastSeekTarget;        // 最後に映像へ反映した再生位置(秒)
    private readonly System.Windows.Forms.Timer cropTimer = new() { Interval = 15 }; // 切り取り範囲を動かして戻す
    private Action? cropAnimationStep;
    private ColorFader? statusFader;
    private ColorFader? fileCardFader;
    private readonly System.Windows.Forms.Timer seekTimer = new() { Interval = 40 };
    private bool recordedWithAudio;
    private ClipFormat outputFormat = ClipFormat.Mp4;
    private bool keptOnTop;              // 録画後の編集画面を、他のアプリの上へ出している間
    private bool activatedWhileOnTop;    // その間に利用者がアプリを操作したか
    private WpfRect cropFraction = new(0, 0, 1, 1);
    private WpfRect videoDisplayRect;
    private WpfPoint dragOrigin;
    private WpfRect dragOriginFraction;
    private CropDragMode cropDragMode = CropDragMode.None;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hWnd, string? appName, string? idList);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    private static int EditorTop => SetupCardTop + SetupCardHeight + CardGap;
    private static int FileCardTop => EditorTop + EditorHeight + FileCardGap;

    //--------------
    // 画面の組み立て
    //--------------
    public MainForm()
    {
        // 組み立てが終わるまで窓を透明にし、部品が1つずつ描かれる様子を見せない
        Opacity = 0;
        // 全体を組み立ててから表示倍率へ拡大する。途中で拡大すると後から足した部品だけ100%の位置に残る
        SuspendLayout();
        Text = "uskCompressionRecorder";
        ClientSize = new Size(BaseClientWidth, FileCardTop + FileCardHeight + FullBottomMargin);
        MinimumSize = new Size(588, 520);
        AutoScroll = true;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Font(Theme.FontSize.Body);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        ConfigureHintTip();

        var title = NewLabel("uskCompressionRecorder", Theme.FontSize.Title, FontStyle.Bold, Theme.Text);
        title.Location = new Point(30, 22); title.AutoSize = true;
        var subtitle = NewLabel($"30 FPS  •  最大 {ClipEncoder.MaxDimension}×{ClipEncoder.MaxDimension}  •  最長 {MaxSeconds}秒", Theme.FontSize.Body, FontStyle.Regular, Theme.Muted);
        subtitle.Location = new Point(32, 60); subtitle.AutoSize = true;

        setupCard.Location = new Point(16, SetupCardTop); setupCard.Size = new Size(538, SetupCardHeight); setupCard.BackColor = Theme.SurfaceElevated;
        ApplyRoundedCorners(setupCard, Theme.RadiusCard); setupCard.Paint += (_, e) => DrawRoundedBorder(setupCard, e.Graphics, Theme.RadiusCard, Theme.Border);

        var targetLabel = NewLabel("録画対象", Theme.FontSize.Body, FontStyle.Bold, Theme.Text);
        targetLabel.Location = new Point(16, 62); targetLabel.AutoSize = true;
        captureTarget.Location = new Point(16, 88); captureTarget.Size = new Size(406, 36); captureTarget.AccessibleName = "録画対象";
        captureTarget.Opening += (_, _) => RefreshCaptureTargets(true);
        refreshTargetsButton.Text = "更新"; refreshTargetsButton.Location = new Point(434, 86); refreshTargetsButton.Size = new Size(88, 40);
        StyleSecondary(refreshTargetsButton); refreshTargetsButton.Click += (_, _) => RefreshCaptureTargets(true);

        audioEnabled.Location = new Point(16, 145); audioEnabled.Size = new Size(54, 30); audioEnabled.Checked = false; audioEnabled.AccessibleName = "音声";
        var audioLabel = NewLabel("音声", Theme.FontSize.Body, FontStyle.Bold, Theme.Text); audioLabel.Location = new Point(84, 149); audioLabel.AutoSize = true;
        var audioHint = NewLabel("すべてのアプリ音声・マイクなし", Theme.FontSize.Small, FontStyle.Regular, Theme.Muted);
        audioHint.Location = new Point(276, 150); audioHint.AutoSize = true;

        limitEnabled.Location = new Point(16, 190); limitEnabled.Size = new Size(54, 30); limitEnabled.AccessibleName = "容量制限";
        limitEnabled.Checked = settings.SizeLimitEnabled;
        var limitLabel = NewLabel("容量を制限する", Theme.FontSize.Body, FontStyle.Bold, Theme.Text); limitLabel.Location = new Point(84, 194); limitLabel.AutoSize = true;
        var limitHelp = NewHelpMark(limitLabel, $"オンにすると、指定した容量に収まるよう、長さに合わせて細かく圧縮します。オフにすると圧縮せず、元の画質（最大{ClipEncoder.MaxDimension}×{ClipEncoder.MaxDimension}）で書き出します。", "容量制限のヘルプ");
        maxSize.Location = new Point(16, 232); maxSize.Size = new Size(150, 36); maxSize.EditorName = "容量上限(MB)";
        maxSize.Minimum = AppSettings.MinSizeMb; maxSize.Maximum = AppSettings.MaxSizeMb; maxSize.DecimalPlaces = 0; maxSize.Increment = 1; maxSize.Value = settings.SizeLimitMb; maxSize.Enabled = limitEnabled.Checked;
        var mb = NewLabel("MB", Theme.FontSize.Body, FontStyle.Bold, Theme.Muted); mb.Location = new Point(178, 238); mb.AutoSize = true;
        limitEnabled.CheckedChanged += (_, _) => { maxSize.Enabled = limitEnabled.Checked; settings.SizeLimitEnabled = limitEnabled.Checked; settings.Save(); UpdateTrimInfo(); };
        maxSize.ValueChanged += (_, _) => { settings.SizeLimitMb = maxSize.Value; settings.Save(); UpdateTrimInfo(); };
        folderButton.Text = "保存先"; folderButton.Location = new Point(356, 226); folderButton.Size = new Size(166, 44);
        StyleSecondary(folderButton); folderButton.Click += ChooseFolder;

        recordButton.Location = new Point(16, 288); recordButton.Size = new Size(506, 70);
        recordButton.FlatStyle = FlatStyle.Flat; recordButton.FlatAppearance.BorderSize = 0;
        SetRecordButtonAsPrimary("●  録画を開始   F8");
        recordButton.Font = Theme.Font(Theme.FontSize.Hero, FontStyle.Bold); recordButton.Cursor = Cursors.Hand;
        recordButton.Click += async (_, _) => await ToggleRecordingAsync();
        timerLabel.Text = TimerText(0); timerLabel.Location = new Point(16, 372); timerLabel.Size = new Size(506, 27);
        timerLabel.TextAlign = ContentAlignment.MiddleCenter; timerLabel.ForeColor = Theme.Muted; timerLabel.Font = Theme.MonoFont(11F, FontStyle.Bold);

        BuildEditor();
        BuildFileCard();
        ResetEditor();
        status.Text = "●  準備完了 — F8でも録画できます"; status.Location = new Point(16, 16); status.Size = new Size(506, 28);
        status.ForeColor = Theme.Success; status.TextAlign = ContentAlignment.MiddleLeft; status.AutoEllipsis = true; status.Font = Theme.Font(Theme.FontSize.Heading, FontStyle.Bold);
        statusFader = new ColorFader(status.ForeColor, c => status.ForeColor = c);

        setupCard.Controls.AddRange([status, targetLabel, captureTarget, refreshTargetsButton, audioEnabled, audioLabel, audioHint, limitEnabled, limitLabel, limitHelp, maxSize, mb, folderButton, recordButton, timerLabel]);
        Controls.AddRange([title, subtitle, setupCard, editorPanel, fileCard]);
        recordTimer.Tick += RecordTimerTick;
        previewTimer.Tick += PreviewTimerTick;
        seekTimer.Tick += (_, _) => FlushSeek(false);
        cropTimer.Tick += (_, _) => cropAnimationStep?.Invoke();
        estimateTimer.Tick += async (_, _) => { estimateTimer.Stop(); await EstimateOutputAsync(); };
        FormClosing += OnClosing;
        Load += (_, _) => fadeGuardTimer.Start();
        Activated += (_, _) => { if (keptOnTop) activatedWhileOnTop = true; };
        Deactivate += (_, _) => { if (keptOnTop && activatedWhileOnTop) ReleaseOnTop(); };
        fadeGuardTimer.Tick += (_, _) => { fadeGuardTimer.Stop(); fadeTimer.Stop(); Opacity = 1; };
        Shown += (_, _) =>
        {
            var dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); SetWindowTheme(Handle, "DarkMode_Explorer", null);
            RegisterHotKey(Handle, HotkeyId, 0, (uint)Keys.F8); RefreshCaptureTargets(false);
            SweepOldTempFiles();
            // 同梱のFFmpegを初回だけ裏で展開し、各モニターで ddagrab が使えるかも先に確かめておく(録画の開始を待たせない)
            var deviceNames = Screen.AllScreens.Select(screen => screen.DeviceName).ToArray();
            _ = Task.Run(() =>
            {
                var ffmpeg = FfmpegLocator.Path;
                foreach (var name in deviceNames)
                    if (DesktopDuplication.Find(name) is { } output) _ = DesktopDuplication.IsUsableAsync(ffmpeg, output.Index);
            });
            BeginInvoke(() => { FitWindow(); StartFadeIn(); });
        };
        // 表示倍率の違うモニターへ移すと部品が拡大・縮小されるため、窓も中身に合わせ直す
        DpiChanged += (_, _) => BeginInvoke(FitWindow);

        ApplyRoundedCorners(recordButton, Theme.RadiusControl);
        ApplyRoundedCorners(refreshTargetsButton, Theme.RadiusControl);
        ApplyRoundedCorners(folderButton, Theme.RadiusControl);
        ResumeLayout(false);
        PerformLayout();
    }

    private void BuildEditor()
    {
        editorPanel.Location = new Point(32, EditorTop); editorPanel.Size = new Size(506, EditorHeight); editorPanel.BackColor = Theme.SurfaceElevated;
        var heading = NewLabel("プレビューと切り取り", Theme.FontSize.Heading, FontStyle.Bold, Theme.Text); heading.Location = new Point(18, 10); heading.AutoSize = true;
        var cropHelp = NewHelpMark(heading, "映像をドラッグ：切り取る範囲を指定\n角をドラッグ：範囲の大きさを変更\n枠の内側をドラッグ：範囲を移動\n選択をリセット：フレーム全体に戻す", "切り取りのヘルプ");
        resolutionInfo.Location = new Point(220, 10); resolutionInfo.Size = new Size(268, 22); resolutionInfo.TextAlign = ContentAlignment.MiddleRight;
        resolutionInfo.ForeColor = Theme.Muted; resolutionInfo.Font = Theme.Font(Theme.FontSize.Small); resolutionInfo.AutoEllipsis = true;
        previewHost.Location = new Point(0, 42); previewHost.Size = new Size(506, 285); previewHost.BackColor = Theme.Surface;
        media.LoadedBehavior = MediaState.Manual; media.UnloadedBehavior = MediaState.Manual; media.Stretch = System.Windows.Media.Stretch.Uniform;
        media.MediaEnded += (_, _) => { if (Playing && !scrubbing) { SeekTo(TrimStartSeconds, true); media.Play(); } };
        BuildCropOverlay();
        cropInfo.Text = "選択範囲: フレーム全体"; cropInfo.Location = new Point(18, 331); cropInfo.Size = new Size(330, 20); cropInfo.ForeColor = Theme.Muted; cropInfo.Font = Theme.Font(Theme.FontSize.Caption); cropInfo.AutoEllipsis = true;
        cropResetButton.Text = "選択をリセット"; cropResetButton.Location = new Point(368, 327); cropResetButton.Size = new Size(120, 26); cropResetButton.Font = Theme.Font(Theme.FontSize.Caption);
        StyleSecondary(cropResetButton); cropResetButton.Click += (_, _) => AnimateCropTo(new WpfRect(0, 0, 1, 1));
        previewSeek.Location = new Point(14, 363); previewSeek.Size = new Size(478, 36); previewSeek.Minimum = 0; previewSeek.Maximum = 300;
        media.ScrubbingEnabled = true; // 一時停止中に位置を動かしても、その場の映像を表示する
        previewSeek.Scroll += SeekChanged;
        previewSeek.DragStarted += (_, _) => BeginScrub();
        previewSeek.DragEnded += (_, _) => EndScrub(null);
        // 再生・始点と終点の設定・経過時間を、シークバーの下に1行で並べる
        playButton.Location = new Point(18, 400); playButton.Size = new Size(26, 26);
        playButton.Click += (_, _) => TogglePlayback();
        markInButton.Text = "始点に設定"; markInButton.Location = new Point(54, 401); markInButton.Size = new Size(92, 24); markInButton.Font = Theme.Font(Theme.FontSize.Caption);
        markOutButton.Text = "終点に設定"; markOutButton.Location = new Point(152, 401); markOutButton.Size = new Size(92, 24); markOutButton.Font = Theme.Font(Theme.FontSize.Caption);
        StyleSecondary(markInButton); markInButton.Click += (_, _) => MarkIn();
        StyleSecondary(markOutButton); markOutButton.Click += (_, _) => MarkOut();
        var timeHelp = new HelpMark { AccessibleName = "時間の切り取りのヘルプ", Location = new Point(252, 404) };
        hintTip.SetToolTip(timeHelp, "Space：再生／一時停止\n← →：0.1秒ずつ移動（Shift：1秒）\nI・O：今の位置を始点・終点にする\nつまみの間をドラッグ：長さを保って範囲をずらす");
        previewTime.Text = "00:00.0 / 00:00.0"; previewTime.Location = new Point(280, 401); previewTime.Size = new Size(208, 24); previewTime.TextAlign = ContentAlignment.MiddleRight; previewTime.ForeColor = Theme.Muted;
        previewTime.Font = Theme.MonoFont(Theme.FontSize.Small); // 数字の幅をそろえ、再生中に文字が揺れないようにする
        trimSlider.Location = new Point(18, 429); trimSlider.Size = new Size(470, 30);
        trimSlider.RangeChanged += (_, _) => { SyncSeekToTrim(); UpdateTrimInfo(); };
        // つまみを動かしている間は一時停止してその位置を見せる。離したら、再生中だったときは切り取り範囲の頭から再生を再開する
        trimSlider.DragPositionChanged += (_, value) =>
        {
            BeginScrub();
            SeekTo((double)value, false);
            ShowTrimBubble(value);
        };
        trimSlider.DragEnded += (_, _) =>
        {
            trimBubble.Visible = false;
            EndScrub(Playing ? TrimStartSeconds : null);
        };
        trimBubble.Visible = false; trimBubble.AutoSize = true; trimBubble.BackColor = Theme.Accent; trimBubble.ForeColor = Theme.TextOnAccent;
        trimBubble.Font = Theme.MonoFont(Theme.FontSize.Caption, FontStyle.Bold); trimBubble.Padding = new Padding(6, 2, 6, 2);

        var formatLabel = NewLabel("出力形式", Theme.FontSize.Small, FontStyle.Bold, Theme.Text); formatLabel.Location = new Point(18, 471); formatLabel.AutoSize = true;
        var formatHelp = NewHelpMark(formatLabel, "MP4：画質が最も良く、容量も小さい\nGIF：どこでも動くが、容量が大きい\nWebP：GIFより小さく高画質（アニメーション）", "出力形式のヘルプ");
        formatSelect.Location = new Point(18, 495); formatSelect.Size = new Size(470, 38); formatSelect.AccessibleName = "出力形式";
        formatSelect.SetItems("MP4 動画", "GIF アニメ", "WebP アニメ");
        formatSelect.SelectedIndexChanged += (_, _) => { outputFormat = (ClipFormat)formatSelect.SelectedIndex; UpdateFormat(); };
        trimInfo.Location = new Point(18, 543); trimInfo.Size = new Size(470, 40); trimInfo.ForeColor = Theme.Muted; trimInfo.TextAlign = ContentAlignment.MiddleCenter;
        exportButton.Location = new Point(18, 591); exportButton.Size = new Size(470, 50);
        exportButton.FlatStyle = FlatStyle.Flat; exportButton.FlatAppearance.BorderSize = 0; exportButton.BackColor = Theme.Accent; exportButton.ForeColor = Theme.TextOnAccent; exportButton.Cursor = Cursors.Hand;
        exportButton.Font = Theme.Font(Theme.FontSize.Heading, FontStyle.Bold); // 主な操作のため、録画ボタンと同じく太字にする
        exportButton.Click += async (_, _) => { if (exporting) CancelExport(); else if (clipLoaded) await ExportAsync(); };
        exportButton.Paint += DrawExportBusy;
        busyTimer.Tick += (_, _) => exportButton.Invalidate();
        editorPanel.Controls.AddRange([heading, cropHelp, resolutionInfo, previewHost, cropInfo, cropResetButton, previewSeek, playButton, markInButton, markOutButton, timeHelp, previewTime, trimSlider, formatLabel, formatHelp, formatSelect, trimInfo, exportButton, trimBubble]);
        ApplyRoundedCorners(editorPanel, Theme.RadiusCard);
        ApplyRoundedCorners(exportButton, Theme.RadiusControl);
        ApplyRoundedCorners(cropResetButton, Theme.RadiusSmall);
        ApplyRoundedCorners(markInButton, Theme.RadiusSmall);
        ApplyRoundedCorners(markOutButton, Theme.RadiusSmall);
        ApplyRoundedCorners(trimBubble, Theme.RadiusTag);
        UpdateFormat();
    }

    private void BuildFileCard()
    {
        fileCard.Location = new Point(32, FileCardTop); fileCard.Size = new Size(506, FileCardHeight); fileCard.BackColor = Theme.SurfaceElevated;
        fileCard.MouseDown += StartFileDrag; fileName.MouseDown += StartFileDrag; fileInfo.MouseDown += StartFileDrag; fileHint.MouseDown += StartFileDrag;
        fileName.Location = new Point(18, 10); fileName.Size = new Size(470, 26); fileName.Font = Theme.Font(Theme.FontSize.Body, FontStyle.Bold); fileName.AutoEllipsis = true;
        fileInfo.Location = new Point(18, 38); fileInfo.Size = new Size(470, 24); fileInfo.ForeColor = Theme.Text; fileInfo.AutoEllipsis = true;
        fileHint.Location = new Point(18, 66); fileHint.Size = new Size(470, 20); fileHint.ForeColor = Theme.Muted; fileHint.Font = Theme.Font(Theme.FontSize.Caption); fileHint.AutoEllipsis = true;
        fileCard.Controls.AddRange([fileName, fileInfo, fileHint]);
        fileCardFader = new ColorFader(Theme.SurfaceElevated, c => fileCard.BackColor = c);
        foreach (var part in new Control[] { fileCard, fileName, fileInfo, fileHint })
        {
            part.MouseEnter += (_, _) => UpdateFileCardHover();
            part.MouseLeave += (_, _) => UpdateFileCardHover();
        }
        ApplyRoundedCorners(fileCard, Theme.RadiusCard);
        ResetFileCard();
    }

    // 保存したファイルがあるときだけ、カーソルを乗せるとカードを少し明るくして、ドラッグできることを示す
    private void UpdateFileCardHover()
    {
        var inside = fileCard.ClientRectangle.Contains(fileCard.PointToClient(Cursor.Position));
        fileCardFader?.FadeTo(inside && finalPath is not null ? Theme.SurfaceSubtle : Theme.SurfaceElevated);
    }

    //--------------
    // 説明(「？」と説明札)
    //--------------
    private void ConfigureHintTip()
    {
        hintTip.OwnerDraw = true; hintTip.InitialDelay = 300; hintTip.ReshowDelay = 100; hintTip.AutoPopDelay = 30000; hintTip.ShowAlways = true;
        hintTip.Popup += (_, e) => e.ToolTipSize = HintPopup.Measure(e.AssociatedControl!, hintTip.GetToolTip(e.AssociatedControl!) ?? string.Empty);
        hintTip.Draw += (_, e) => HintPopup.Draw(e.Graphics, e.Bounds, e.AssociatedControl!, hintTip.GetToolTip(e.AssociatedControl!) ?? string.Empty);
    }

    // 文字の大きさは今の表示倍率の画素で測れるため、100%基準の位置へ戻してから「？」を置く
    private HelpMark NewHelpMark(Label anchor, string text, string accessibleName)
    {
        var mark = new HelpMark { AccessibleName = accessibleName };
        var textWidth = (int)Math.Ceiling(anchor.PreferredSize.Width * 96.0 / anchor.DeviceDpi);
        var textHeight = (int)Math.Ceiling(anchor.PreferredSize.Height * 96.0 / anchor.DeviceDpi);
        mark.Location = new Point(anchor.Left + textWidth + 4, anchor.Top + (textHeight - mark.Height) / 2);
        hintTip.SetToolTip(mark, text);
        return mark;
    }

    //--------------
    // 起動時のフェードイン
    //--------------
    private void StartFadeIn()
    {
        if (!SystemInformation.UIEffectsEnabled) { fadeGuardTimer.Stop(); Opacity = 1; return; }
        var clock = Stopwatch.StartNew();
        fadeTimer.Tick += (_, _) =>
        {
            var progress = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / FadeInMilliseconds);
            Opacity = 1 - Math.Pow(1 - progress, 3);
            if (progress >= 1) { fadeTimer.Stop(); fadeGuardTimer.Stop(); Opacity = 1; }
        };
        fadeTimer.Start();
    }

    //--------------
    // 窓の大きさ
    //--------------
    private int Dp(int logical) => LogicalToDeviceUnits(logical);

    // 画面は録画の前後で変えず、設定・プレビュー・保存カードの全体をいつも見せる。
    // 値はすべて表示倍率へ換算する。画面に収まらないときだけ高さを抑え、スクロールバーの幅を足す。
    private void FitWindow()
    {
        if (WindowState != FormWindowState.Normal) return;
        var area = Screen.FromControl(this).WorkingArea;
        var desiredHeight = fileCard.Bottom + Dp(FullBottomMargin);
        var maxHeight = area.Height - (Height - ClientSize.Height);
        var scrollBarWidth = desiredHeight > maxHeight ? GetSystemMetricsForDpi(SmCxVScroll, (uint)DeviceDpi) : 0;
        ClientSize = new Size(Dp(BaseClientWidth) + scrollBarWidth, Math.Min(desiredHeight, maxHeight));
        Location = new Point(
            Math.Clamp(Location.X, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Location.Y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    // 録画が終わるのは、ゲームなど別のアプリを操作している最中(F8・30秒で自動停止)。
    // そのままでは編集画面が全画面のゲームの後ろに隠れて見えない。ゲームは数秒後に自分を最前面へ戻すため、
    // 通常の前面化では再び隠れる。最小化を解いて「最前面」で出し、フォーカスは奪わない(ゲームの操作を止めない)。
    private void BringToFrontSafely()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        keptOnTop = true; activatedWhileOnTop = false;
        TopMost = true;
    }

    // 利用者がアプリを操作して離れたとき・次の録画を始めるときに、最前面を解く
    private void ReleaseOnTop()
    {
        if (!keptOnTop) return;
        keptOnTop = false; activatedWhileOnTop = false;
        TopMost = false;
    }

    // 画面全体が入れ替わる変更は、描画を止めて行い、最後に全体を一度に描き直す
    private void RunBulkUpdate(Action change)
    {
        SendMessage(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        try { change(); }
        finally
        {
            SendMessage(Handle, WmSetRedraw, (IntPtr)1, IntPtr.Zero);
            Refresh();
        }
    }

    //--------------
    // 出力形式・トリミング表示
    //--------------
    private void ShowTrimBubble(decimal value)
    {
        trimBubble.Text = FormatTime((double)value);
        var x = trimSlider.Left + (int)trimSlider.PositionForValue(value) - trimBubble.Width / 2;
        trimBubble.Location = new Point(Math.Clamp(x, 0, editorPanel.Width - trimBubble.Width), trimSlider.Top - trimBubble.Height - 4);
        trimBubble.BringToFront();
        trimBubble.Visible = true;
    }

    private void UpdateFormat()
    {
        ApplyExportLook();
        UpdateTrimInfo();
    }

    private static string FormatName(ClipFormat format) => format switch { ClipFormat.Gif => "GIF", ClipFormat.Webp => "WebP", _ => "MP4" };
    private static string FormatExtension(ClipFormat format) => format switch { ClipFormat.Gif => "gif", ClipFormat.Webp => "webp", _ => "mp4" };

    // ボタンの色をなめらかに切り替える。最初の1回だけは動かさず、そのまま設定する。
    // カーソルを乗せている間は、基本の色を少し明るくする(押せないときは変えない)。
    private readonly Dictionary<Button, (Color Back, Color Fore)> restColors = [];
    private readonly HashSet<Button> hoveredButtons = [];

    private void FadeButton(Button button, Color back, Color fore)
    {
        restColors[button] = (back, fore);
        if (!buttonFaders.TryGetValue(button, out var faders))
        {
            button.BackColor = back; button.ForeColor = fore; button.FlatAppearance.MouseOverBackColor = back; button.FlatAppearance.MouseDownBackColor = back;
            // 平らなボタンはカーソルを乗せた色をOSがすぐ描くため、乗せたとき・押したときの色も今の色に合わせる
            buttonFaders[button] = (new ColorFader(back, c => { button.BackColor = c; button.FlatAppearance.MouseOverBackColor = c; button.FlatAppearance.MouseDownBackColor = c; }, 140), new ColorFader(fore, c => button.ForeColor = c, 140));
            button.MouseEnter += (_, _) => { hoveredButtons.Add(button); ApplyButtonColors(button); };
            button.MouseLeave += (_, _) => { hoveredButtons.Remove(button); ApplyButtonColors(button); };
            return;
        }
        ApplyButtonColors(button);
    }

    private void ApplyButtonColors(Button button)
    {
        var (back, fore) = restColors[button];
        var canPress = button.Enabled && (button != exportButton || clipLoaded || exporting);
        var hot = hoveredButtons.Contains(button) && canPress;
        buttonFaders[button].Back.FadeTo(hot ? Motion.Lerp(back, Color.White, 0.10f) : back);
        buttonFaders[button].Fore.FadeTo(fore);
    }

    // 保存ボタンの見た目: 映像がないとき・保存できるとき(圧縮中は別に描く)
    private void ApplyExportLook()
    {
        if (exporting) return;
        exportButton.Text = clipLoaded ? $"保存 ({FormatName(outputFormat)})" : "録画すると、ここから保存できます";
        FadeButton(exportButton, clipLoaded ? Theme.Accent : Theme.SurfaceSubtle, clipLoaded ? Theme.TextOnAccent : Theme.Muted);
        exportButton.Cursor = clipLoaded ? Cursors.Hand : Cursors.Default;
    }

    private double TrimStartSeconds => (double)trimSlider.StartValue;
    private double TrimEndSeconds => (double)trimSlider.EndValue;
    private static string FormatTime(double value) => $"{(int)value / 60:00}:{value % 60:00.0}";

    private void UpdateTrimInfo()
    {
        if (!clipLoaded) return;
        UpdateTrimText();
        UpdateOutputResolution();
    }

    private void UpdateTrimText()
    {
        if (!clipLoaded) return;
        var limit = limitEnabled.Checked ? $"{maxSize.Value:F0}MB" : "なし";
        trimInfo.Text = $"範囲 {FormatTime(TrimStartSeconds)} – {FormatTime(TrimEndSeconds)}（{TrimEndSeconds - TrimStartSeconds:F1}秒）  •  容量上限 {limit}  •  {FormatName(outputFormat)}";
    }

    // 「現在の解像度」: 切り取り範囲・長さ・容量上限・形式を反映した、書き出し後の解像度を表示する。
    // 容量上限があるMP4・WebPは映像の一部を試しに圧縮して見積もるため、操作が止まってから計算する。
    private void UpdateOutputResolution()
    {
        if (!clipLoaded || media.NaturalVideoWidth <= 0 || exporting) return;
        estimateCancellation?.Cancel();
        estimateTimer.Stop();
        if (limitEnabled.Checked && outputFormat != ClipFormat.Gif && tempPath is not null)
        {
            resolutionInfo.Text = "現在の解像度  計算中…";
            estimateTimer.Start();
            return;
        }
        ShowEstimate(ClipEncoder.EstimateOutput(outputFormat, media.NaturalVideoWidth, media.NaturalVideoHeight,
            cropFraction.Width, cropFraction.Height, Math.Max(0.1, TrimEndSeconds - TrimStartSeconds), CurrentLimit, recordedWithAudio));
    }

    private long? CurrentLimit => limitEnabled.Checked ? (long)(maxSize.Value * 1024 * 1024) : null;

    private void ShowEstimate((int Width, int Height, double Fps) estimate) =>
        resolutionInfo.Text = $"現在の解像度  {estimate.Width}×{estimate.Height}  •  {estimate.Fps:0.#}fps";

    private async Task EstimateOutputAsync()
    {
        if (!clipLoaded || tempPath is null || media.NaturalVideoWidth <= 0 || exporting) return;
        var cancellation = estimateCancellation = new CancellationTokenSource();
        var request = new ClipRequest
        {
            Input = tempPath, Output = string.Empty, Start = TrimStartSeconds, Duration = Math.Max(0.1, TrimEndSeconds - TrimStartSeconds),
            Format = outputFormat, IncludeAudio = recordedWithAudio, ByteLimit = CurrentLimit,
            CropX = cropFraction.X, CropY = cropFraction.Y, CropWidth = cropFraction.Width, CropHeight = cropFraction.Height,
        };
        try
        {
            var estimate = await new ClipEncoder(FfmpegPath, _ => { }).EstimateOutputAsync(request, media.NaturalVideoWidth, media.NaturalVideoHeight, cancellation.Token);
            if (!cancellation.IsCancellationRequested) ShowEstimate(estimate);
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!cancellation.IsCancellationRequested)
                ShowEstimate(ClipEncoder.EstimateOutput(request.Format, media.NaturalVideoWidth, media.NaturalVideoHeight, request.CropWidth, request.CropHeight, request.Duration, request.ByteLimit, request.IncludeAudio));
        }
    }

    //--------------
    // 画面の範囲切り取り
    //--------------
    private void BuildCropOverlay()
    {
        previewRoot.Children.Add(media);
        cropCanvas.Background = WpfBrushes.Transparent;
        cropMask.Fill = new WpfBrush(Theme.ToWpf(Color.Black, 150));
        cropMask.IsHitTestVisible = false;
        cropRectVisual.Stroke = new WpfBrush(Theme.ToWpf(Theme.Accent));
        cropRectVisual.StrokeThickness = Theme.AccentBorderWidth;
        cropRectVisual.Fill = WpfBrushes.Transparent;
        cropRectVisual.IsHitTestVisible = false;
        cropCanvas.Children.Add(cropMask);
        cropCanvas.Children.Add(cropRectVisual);
        foreach (var handle in cropHandles) { handle.Fill = new WpfBrush(Theme.ToWpf(Theme.Accent)); cropCanvas.Children.Add(handle); }
        cropCanvas.MouseLeftButtonDown += CropMouseDown;
        cropCanvas.MouseMove += CropMouseMove;
        cropCanvas.MouseLeftButtonUp += CropMouseUp;
        cropCanvas.SizeChanged += (_, _) => { RecomputeVideoDisplayRect(); UpdateCropVisual(); };
        media.MediaOpened += (_, _) => { RecomputeVideoDisplayRect(); UpdateCropVisual(); OnMediaOpened(); };
        previewPlaceholder.Text = "録画すると、ここにプレビューが表示されます\nF8 または「録画を開始」を押してください";
        previewPlaceholder.Foreground = new WpfBrush(Theme.ToWpf(Theme.Muted));
        previewPlaceholder.FontFamily = new System.Windows.Media.FontFamily(Theme.FontFamily);
        previewPlaceholder.FontSize = 14;
        previewPlaceholder.TextAlignment = System.Windows.TextAlignment.Center;
        previewPlaceholder.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        previewPlaceholder.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        previewPlaceholder.IsHitTestVisible = false;
        previewRoot.Children.Add(previewPlaceholder);
        previewRoot.Children.Add(cropCanvas);
        previewHost.Child = previewRoot;
    }

    private static bool Near(WpfPoint a, WpfPoint b, double tolerance) => Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;

    private void CropMouseDown(object? sender, WpfMouseButtonEventArgs e)
    {
        if (!clipLoaded || exporting || videoDisplayRect.Width <= 0 || videoDisplayRect.Height <= 0) return;
        cropTimer.Stop();
        var point = e.GetPosition(cropCanvas);
        var currentPixelRect = FractionToPixelRect(cropFraction);
        const double handleZone = 10;
        if (Near(point, new WpfPoint(currentPixelRect.Left, currentPixelRect.Top), handleZone)) cropDragMode = CropDragMode.ResizeTopLeft;
        else if (Near(point, new WpfPoint(currentPixelRect.Right, currentPixelRect.Top), handleZone)) cropDragMode = CropDragMode.ResizeTopRight;
        else if (Near(point, new WpfPoint(currentPixelRect.Left, currentPixelRect.Bottom), handleZone)) cropDragMode = CropDragMode.ResizeBottomLeft;
        else if (Near(point, new WpfPoint(currentPixelRect.Right, currentPixelRect.Bottom), handleZone)) cropDragMode = CropDragMode.ResizeBottomRight;
        else if (currentPixelRect.Contains(point)) cropDragMode = CropDragMode.Move;
        else if (videoDisplayRect.Contains(point)) cropDragMode = CropDragMode.Create;
        else return;
        dragOrigin = point;
        dragOriginFraction = cropFraction;
        cropCanvas.CaptureMouse();
    }

    private void CropMouseMove(object? sender, WpfMouseEventArgs e)
    {
        if (cropDragMode == CropDragMode.None || videoDisplayRect.Width <= 0) return;
        var raw = e.GetPosition(cropCanvas);
        var point = new WpfPoint(
            Math.Clamp(raw.X, videoDisplayRect.Left, videoDisplayRect.Right),
            Math.Clamp(raw.Y, videoDisplayRect.Top, videoDisplayRect.Bottom));
        var dxFraction = (point.X - dragOrigin.X) / videoDisplayRect.Width;
        var dyFraction = (point.Y - dragOrigin.Y) / videoDisplayRect.Height;
        var originStartFraction = new WpfPoint((dragOrigin.X - videoDisplayRect.Left) / videoDisplayRect.Width, (dragOrigin.Y - videoDisplayRect.Top) / videoDisplayRect.Height);
        var currentFraction = new WpfPoint((point.X - videoDisplayRect.Left) / videoDisplayRect.Width, (point.Y - videoDisplayRect.Top) / videoDisplayRect.Height);
        switch (cropDragMode)
        {
            case CropDragMode.Create:
                var left = Math.Min(originStartFraction.X, currentFraction.X);
                var top = Math.Min(originStartFraction.Y, currentFraction.Y);
                cropFraction = new WpfRect(left, top, Math.Abs(currentFraction.X - originStartFraction.X), Math.Abs(currentFraction.Y - originStartFraction.Y));
                break;
            case CropDragMode.Move:
                var newX = Math.Clamp(dragOriginFraction.X + dxFraction, 0, 1 - dragOriginFraction.Width);
                var newY = Math.Clamp(dragOriginFraction.Y + dyFraction, 0, 1 - dragOriginFraction.Height);
                cropFraction = new WpfRect(newX, newY, dragOriginFraction.Width, dragOriginFraction.Height);
                break;
            case CropDragMode.ResizeTopLeft: ResizeFromCorner(currentFraction, new WpfPoint(dragOriginFraction.Right, dragOriginFraction.Bottom)); break;
            case CropDragMode.ResizeTopRight: ResizeFromCorner(currentFraction, new WpfPoint(dragOriginFraction.Left, dragOriginFraction.Bottom)); break;
            case CropDragMode.ResizeBottomLeft: ResizeFromCorner(currentFraction, new WpfPoint(dragOriginFraction.Right, dragOriginFraction.Top)); break;
            case CropDragMode.ResizeBottomRight: ResizeFromCorner(currentFraction, new WpfPoint(dragOriginFraction.Left, dragOriginFraction.Top)); break;
        }
        UpdateCropVisual();
    }

    private void ResizeFromCorner(WpfPoint dragged, WpfPoint opposite)
    {
        var left = Math.Clamp(Math.Min(dragged.X, opposite.X), 0, 1);
        var top = Math.Clamp(Math.Min(dragged.Y, opposite.Y), 0, 1);
        var width = Math.Min(Math.Abs(dragged.X - opposite.X), 1 - left);
        var height = Math.Min(Math.Abs(dragged.Y - opposite.Y), 1 - top);
        cropFraction = new WpfRect(left, top, width, height);
    }

    private void CropMouseUp(object? sender, WpfMouseButtonEventArgs e)
    {
        if (cropDragMode == CropDragMode.None) return;
        cropDragMode = CropDragMode.None;
        cropCanvas.ReleaseMouseCapture();
        if (cropFraction.Width < 0.03 || cropFraction.Height < 0.03) { AnimateCropTo(new WpfRect(0, 0, 1, 1)); return; }
        UpdateCropVisual();
    }

    // 範囲を目標へなめらかに動かす。途中で操作し直したら、今の範囲から動かす
    private void AnimateCropTo(WpfRect target)
    {
        cropTimer.Stop();
        if (!Motion.Enabled || cropFraction == target) { cropFraction = target; UpdateCropVisual(); return; }
        static double Lerp(double a, double b, double t) => a + (b - a) * t;
        var from = cropFraction; var clock = Stopwatch.StartNew();
        cropAnimationStep = () =>
        {
            var t = Motion.EaseOut((float)(clock.Elapsed.TotalMilliseconds / 220));
            cropFraction = t >= 1 ? target : new WpfRect(Lerp(from.X, target.X, t), Lerp(from.Y, target.Y, t), Lerp(from.Width, target.Width, t), Lerp(from.Height, target.Height, t));
            UpdateCropVisual();
            if (t >= 1) cropTimer.Stop();
        };
        cropTimer.Start();
    }

    private WpfRect FractionToPixelRect(WpfRect fraction) => new(
        videoDisplayRect.X + fraction.X * videoDisplayRect.Width,
        videoDisplayRect.Y + fraction.Y * videoDisplayRect.Height,
        fraction.Width * videoDisplayRect.Width,
        fraction.Height * videoDisplayRect.Height);

    private void RecomputeVideoDisplayRect()
    {
        var canvasWidth = cropCanvas.ActualWidth; var canvasHeight = cropCanvas.ActualHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0) { videoDisplayRect = new WpfRect(0, 0, 0, 0); return; }
        var videoWidth = media.NaturalVideoWidth > 0 ? media.NaturalVideoWidth : canvasWidth;
        var videoHeight = media.NaturalVideoHeight > 0 ? media.NaturalVideoHeight : canvasHeight;
        var scale = Math.Min(canvasWidth / videoWidth, canvasHeight / videoHeight);
        var displayWidth = videoWidth * scale; var displayHeight = videoHeight * scale;
        videoDisplayRect = new WpfRect((canvasWidth - displayWidth) / 2, (canvasHeight - displayHeight) / 2, displayWidth, displayHeight);
    }

    private void UpdateCropVisual()
    {
        var visibility = clipLoaded ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        cropRectVisual.Visibility = cropMask.Visibility = visibility;
        foreach (var handle in cropHandles) handle.Visibility = visibility;
        if (videoDisplayRect.Width <= 0 || videoDisplayRect.Height <= 0) return;
        var pixelRect = FractionToPixelRect(cropFraction);
        WpfCanvas.SetLeft(cropRectVisual, pixelRect.X); WpfCanvas.SetTop(cropRectVisual, pixelRect.Y);
        cropRectVisual.Width = Math.Max(0, pixelRect.Width); cropRectVisual.Height = Math.Max(0, pixelRect.Height);
        var positions = new[] { new WpfPoint(pixelRect.Left, pixelRect.Top), new WpfPoint(pixelRect.Right, pixelRect.Top), new WpfPoint(pixelRect.Left, pixelRect.Bottom), new WpfPoint(pixelRect.Right, pixelRect.Bottom) };
        for (var i = 0; i < cropHandles.Length; i++)
        {
            WpfCanvas.SetLeft(cropHandles[i], positions[i].X - cropHandles[i].Width / 2);
            WpfCanvas.SetTop(cropHandles[i], positions[i].Y - cropHandles[i].Height / 2);
        }
        var outer = new WpfRectangleGeometry(new WpfRect(0, 0, cropCanvas.ActualWidth, cropCanvas.ActualHeight));
        var inner = new WpfRectangleGeometry(pixelRect);
        cropMask.Data = new WpfCombinedGeometry(WpfGeometryCombineMode.Exclude, outer, inner);
        UpdateCropInfo();
    }

    private void UpdateCropInfo()
    {
        UpdateOutputResolution();
        if (cropFraction.Width >= 0.999 && cropFraction.Height >= 0.999) { cropInfo.Text = "選択範囲: フレーム全体"; return; }
        var videoWidth = media.NaturalVideoWidth; var videoHeight = media.NaturalVideoHeight;
        cropInfo.Text = videoWidth > 0
            ? $"選択範囲: {Math.Round(cropFraction.Width * videoWidth)}×{Math.Round(cropFraction.Height * videoHeight)} ({cropFraction.Width * 100:F0}%)"
            : $"選択範囲: {cropFraction.Width * 100:F0}%×{cropFraction.Height * 100:F0}%";
    }

    //--------------
    // 部品の共通処理
    //--------------
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId) _ = ToggleRecordingAsync();
        base.WndProc(ref m);
    }

    private static Label NewLabel(string text, float size, FontStyle style, Color color) => new() { Text = text, Font = Theme.Font(size, style), ForeColor = color };
    private void StyleSecondary(Button b) { b.FlatStyle = FlatStyle.Flat; b.Cursor = Cursors.Hand; FadeButton(b, Theme.SurfaceElevated, Theme.Text); }

    // 角丸の半径は100%表示の値で受け取り、今の表示倍率の画素へ換算して切り抜く
    private static void ApplyRoundedCorners(Control control, int radius)
    {
        void UpdateRegion()
        {
            if (control.Width <= 0 || control.Height <= 0) return;
            var diameter = Math.Max(2, (int)Math.Round(radius * 2 * control.DeviceDpi / 96.0));
            using var path = new GraphicsPath();
            path.AddArc(0, 0, diameter, diameter, 180, 90);
            path.AddArc(control.Width - diameter, 0, diameter, diameter, 270, 90);
            path.AddArc(control.Width - diameter, control.Height - diameter, diameter, diameter, 0, 90);
            path.AddArc(0, control.Height - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            control.Region?.Dispose();
            control.Region = new Region(path);
        }
        UpdateRegion();
        control.Resize += (_, _) => UpdateRegion();
        control.DpiChangedAfterParent += (_, _) => UpdateRegion();
        // ボタンの既定の枠は四角で、角丸に切り抜くと角が欠けるため、副ボタンの枠は角丸で自前に描く
        if (control is Button button)
        {
            button.FlatAppearance.BorderSize = 0;
            button.Paint += (_, e) =>
            {
                // 押せないとき、OSは文字を暗い灰色で描き、暗い面の上では読めない。面の色で塗り直し、淡い文字で描き直す
                if (!button.Enabled)
                {
                    using var back = new SolidBrush(button.BackColor);
                    e.Graphics.FillRectangle(back, button.ClientRectangle);
                    TextRenderer.DrawText(e.Graphics, button.Text, button.Font, button.ClientRectangle, Theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
                DrawRoundedBorder(button, e.Graphics, radius, BorderFor(button.BackColor));
            };
        }
    }

    // 枠は薄い面のボタンにだけ見える色にし、色の付いた面(アクセント・録画中)へ移るほど面の色に溶かす
    private static Color BorderFor(Color back)
    {
        static float Luma(Color c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
        var span = Luma(Theme.Accent) - Luma(Theme.SurfaceElevated);
        var t = Math.Clamp((Luma(back) - Luma(Theme.SurfaceElevated)) / span, 0f, 1f);
        return Motion.Lerp(Theme.Border, back, t);
    }

    private static void DrawRoundedBorder(Control control, Graphics graphics, int radius, Color color)
    {
        var bounds = control.ClientRectangle;
        bounds.Width -= 1; bounds.Height -= 1;
        var diameter = Math.Max(2, (int)Math.Round(radius * 2 * control.DeviceDpi / 96.0));
        using var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        using var pen = new Pen(color, Theme.BorderWidth);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawPath(pen, path);
    }

    private void SetRecordButtonAsPrimary(string text)
    {
        recordButton.Text = text;
        FadeButton(recordButton, Theme.Accent, Theme.TextOnAccent);
        recordButton.FlatAppearance.BorderSize = 0;
    }

    private void SetRecordButtonAsSecondary()
    {
        recordButton.Text = "↻  もう一度録画   F8";
        FadeButton(recordButton, Theme.SurfaceElevated, Theme.Text);
        recordButton.Invalidate();
    }

    private void SetStatus(string text, Color color) { status.Text = text; if (statusFader is null) status.ForeColor = color; else statusFader.FadeTo(color); }

    private static string TimerText(int seconds) => $"{seconds / 60:00}:{seconds % 60:00} / {MaxSeconds / 60:00}:{MaxSeconds % 60:00}";

    //--------------
    // 録画対象
    //--------------
    private void RefreshCaptureTargets(bool preserveSelection)
    {
        var previousKey = preserveSelection && captureTarget.SelectedItem is CaptureSource selected ? selected.Key : null;
        var targets = new List<CaptureSource>();
        var screens = Screen.AllScreens.OrderByDescending(screen => screen.Primary).ThenBy(screen => screen.DeviceName).ToArray();
        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var primary = screen.Primary ? "・メイン" : string.Empty;
            targets.Add(CaptureSource.ForMonitor($"モニター {i + 1}{primary}  {screen.Bounds.Width}×{screen.Bounds.Height}", screen.Bounds, screen.DeviceName));
        }

        var windows = new List<CaptureSource>();
        EnumWindows((hWnd, _) =>
        {
            if (hWnd == Handle || !IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out var processId);
            if (processId == (uint)Environment.ProcessId) return true;
            var length = GetWindowTextLength(hWnd);
            if (length < 1) return true;
            var buffer = new StringBuilder(length + 1);
            GetWindowText(hWnd, buffer, buffer.Capacity);
            var title = buffer.ToString().Trim();
            if (title.Length == 0) return true;
            if (title.Length > 72) title = title[..69] + "…";
            windows.Add(CaptureSource.ForWindow("ウィンドウ — " + title, hWnd));
            return true;
        }, IntPtr.Zero);
        targets.AddRange(windows.OrderBy(window => window.DisplayName, StringComparer.CurrentCultureIgnoreCase));

        var restoreIndex = previousKey is null ? -1 : targets.FindIndex(target => target.Key == previousKey);
        captureTarget.SetItems(targets.Cast<object>(), restoreIndex >= 0 ? restoreIndex : (targets.Count > 0 ? 0 : -1));
    }

    // 録画される大きさ(4096×4096を超える画面だけ縮小される)
    private static (int Width, int Height) CaptureSize(CaptureSource target)
    {
        int width, height;
        if (target.Kind == CaptureKind.Monitor) { width = target.Bounds.Width; height = target.Bounds.Height; }
        else { var client = WindowClientBounds(target.WindowHandle); width = client.Width; height = client.Height; }
        if (width <= 0 || height <= 0) return (0, 0);
        var shrink = Math.Min(1.0, Math.Min(ClipEncoder.MaxDimension / (double)width, ClipEncoder.MaxDimension / (double)height));
        return ((int)(width * shrink) / 2 * 2, (int)(height * shrink) / 2 * 2);
    }

    // ウィンドウの中身(タイトルバー・枠を除く)の、画面上の範囲(画素)。gdigrab のウィンドウ録画と同じ範囲
    private static Rectangle WindowClientBounds(IntPtr hWnd)
    {
        if (!GetClientRect(hWnd, out var client)) return Rectangle.Empty;
        var origin = new NativePoint();
        if (!ClientToScreen(hWnd, ref origin)) return Rectangle.Empty;
        return new Rectangle(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    // 録画の入力。ddagrab(Desktop Duplication)が使えるときはそれを使う。gdigrab は取り込むたびに画面上のカーソルがちらつき、
    // 表示倍率によってはカーソルをずれた位置に描くため。別のGPUにつながるモニター・複数のモニターにまたがるウィンドウなどは gdigrab で録る
    private async Task<(string Input, string Download)> CaptureInputAsync(CaptureSource target)
    {
        var region = target.Kind == CaptureKind.Monitor ? target.Bounds : WindowClientBounds(target.WindowHandle);
        var deviceName = target.Kind == CaptureKind.Monitor ? target.DeviceName : Screen.FromHandle(target.WindowHandle).DeviceName;
        if (region.Width >= 2 && region.Height >= 2 && deviceName is not null
            && DesktopDuplication.Find(deviceName) is { } output && output.Bounds.Contains(region)
            && (region == output.Bounds || output.Unrotate.Length == 0) // 回転したモニターの一部は、撮る範囲の座標が回転前になるため gdigrab で録る
            && await DesktopDuplication.IsUsableAsync(FfmpegPath, output.Index))
        {
            var area = region == output.Bounds ? string.Empty
                : $":video_size={region.Width}x{region.Height}:offset_x={region.X - output.Bounds.X}:offset_y={region.Y - output.Bounds.Y}";
            return ($"-f lavfi -i ddagrab=output_idx={output.Index}:framerate=30:draw_mouse=1{area}", "hwdownload,format=bgra," + output.Unrotate);
        }
        var gdi = target.Kind == CaptureKind.Monitor
            ? $"-offset_x {target.Bounds.X} -offset_y {target.Bounds.Y} -video_size {target.Bounds.Width}x{target.Bounds.Height} -i desktop"
            : $"-i hwnd=0x{target.WindowHandle.ToInt64():X}";
        return ($"-f gdigrab -framerate 30 {gdi}", string.Empty);
    }

    //--------------
    // ffmpeg・音声
    //--------------
    private static string FfmpegPath => FfmpegLocator.Path;

    private void StartDesktopAudio()
    {
        audioTempPath = Path.Combine(Path.GetTempPath(), $"usk-audio-{Guid.NewGuid():N}.wav");
        loopbackCapture = new WasapiLoopbackCapture();
        loopbackWriter = new WaveFileWriter(audioTempPath, loopbackCapture.WaveFormat);
        loopbackStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        loopbackCapture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0) loopbackWriter?.Write(e.Buffer, 0, e.BytesRecorded);
        };
        loopbackCapture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) loopbackStopped.TrySetException(e.Exception);
            else loopbackStopped.TrySetResult(true);
        };
        audioStartedAt = DateTime.UtcNow;
        loopbackCapture.StartRecording();
    }

    private async Task StopDesktopAudioAsync()
    {
        var capture = loopbackCapture;
        if (capture is null) return;
        try
        {
            capture.StopRecording();
            if (loopbackStopped is not null) await loopbackStopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            loopbackWriter?.Dispose();
            capture.Dispose();
            loopbackWriter = null;
            loopbackCapture = null;
            loopbackStopped = null;
        }
    }

    private async Task MergeDesktopAudioAsync(double duration)
    {
        if (tempPath is null || audioTempPath is null || !File.Exists(audioTempPath) || new FileInfo(audioTempPath).Length <= 44)
            throw new InvalidOperationException("デスクトップ音声を取得できませんでした。Windowsの再生デバイスを確認してください。");
        var combined = Path.Combine(Path.GetTempPath(), $"usk-preview-{Guid.NewGuid():N}.mp4");
        // 音声はffmpegの起動を待たずに始まるため、映像が撮り始まるまでの分だけ音声の頭を切って映像にそろえる
        var videoStart = Interlocked.Read(ref videoStartTicks);
        var audioOffset = videoStart > 0 ? Math.Clamp((new DateTime(videoStart, DateTimeKind.Utc) - audioStartedAt).TotalSeconds, 0, 5) : 0;
        await RunAsync(string.Create(CultureInfo.InvariantCulture, $"-hide_banner -loglevel error -i \"{tempPath}\" -ss {audioOffset:F3} -i \"{audioTempPath}\" -shortest -t {duration + 1:F3} -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 80k -movflags +faststart -y \"{combined}\""));
        DeleteInBackground(tempPath);
        DeleteInBackground(audioTempPath);
        tempPath = combined;
        audioTempPath = null;
    }

    //--------------
    // 録画
    //--------------
    private async Task ToggleRecordingAsync()
    {
        if (stopping) return;
        if (recorder is { HasExited: false }) { await StopRecordingAsync(); return; }
        await StartRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        if (!HasFfmpeg()) { MessageBox.Show("FFmpeg を準備できませんでした。ディスクの空き容量と書き込み権限を確認してください。", "FFmpegが必要です", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (captureTarget.SelectedItem is not CaptureSource target)
        {
            RefreshCaptureTargets(false);
            target = captureTarget.SelectedItem as CaptureSource ?? CaptureSource.ForMonitor("メインモニター", Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720), Screen.PrimaryScreen?.DeviceName);
        }
        if (target.Kind == CaptureKind.Window && !IsWindow(target.WindowHandle))
        {
            RefreshCaptureTargets(false);
            MessageBox.Show("選択したウィンドウは閉じられました。録画対象を選び直してください。", "録画対象が見つかりません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var useAudio = audioEnabled.Checked;
        Directory.CreateDirectory(settings.SaveDirectory!);
        CleanupTemp();
        recordedWithAudio = useAudio;
        ReleaseOnTop();
        recordedSeconds = 0; Interlocked.Exchange(ref videoStartTicks, 0);
        tempPath = Path.Combine(Path.GetTempPath(), $"usk-clip-{Guid.NewGuid():N}.mp4");
        finalPath = null;
        RunBulkUpdate(() =>
        {
            ResetEditor(); ResetFileCard();
            SetStatus("●  準備完了 — F8でも録画できます", Theme.Success);
            AutoScrollPosition = Point.Empty;
        });
        var (input, download) = await CaptureInputAsync(target);
        var cap = ClipEncoder.MaxDimension;
        // -t は録画の長さの上限。ffmpegが映像を撮り始めてからMaxSeconds秒で自分で止まる(起動の待ち時間ぶん短くならない)
        var args = $"-hide_banner -loglevel error -nostats -progress pipe:1 {input} -map 0:v:0 -vf \"{download}scale='min({cap},iw)':'min({cap},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos\" -c:v libx264 -preset ultrafast -crf 18 -pix_fmt yuv420p -an -t {MaxSeconds} -y \"{tempPath}\"";
        try
        {
            if (useAudio) StartDesktopAudio();
            recorder = Process.Start(new ProcessStartInfo(FfmpegPath, args) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true });
            if (recorder is null) throw new InvalidOperationException("FFmpegを開始できませんでした。");
            recorder.OutputDataReceived += OnRecorderProgress;
            recorder.BeginOutputReadLine();
            startedAt = DateTime.UtcNow; recordTimer.Start(); recordButton.Text = "■  録画を停止"; FadeButton(recordButton, Theme.Danger, Theme.TextOnAccent); recordButton.FlatAppearance.BorderSize = 0;
            var (captureWidth, captureHeight) = CaptureSize(target);
            var sizeText = captureWidth > 0 ? $"{captureWidth}×{captureHeight} / " : string.Empty;
            SetStatus($"●  録画中 — {sizeText}音声: {(useAudio ? "オン" : "オフ")}", Theme.Danger);
            folderButton.Enabled = false; captureTarget.Enabled = false; refreshTargetsButton.Enabled = false; audioEnabled.Enabled = false;
        }
        catch (Exception ex) { await StopDesktopAudioAsync(); MessageBox.Show(ex.Message, "録画エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ffmpegの進行表示(out_time_us=経過マイクロ秒)から、実際に録画できた秒数を読む
    private void OnRecorderProgress(object? sender, DataReceivedEventArgs e)
    {
        const string key = "out_time_us=";
        if (e.Data is { } line && line.StartsWith(key, StringComparison.Ordinal) && long.TryParse(line.AsSpan(key.Length), out var microseconds) && microseconds >= 0)
        {
            Volatile.Write(ref recordedSeconds, microseconds / 1_000_000d);
            Interlocked.CompareExchange(ref videoStartTicks, DateTime.UtcNow.Ticks - microseconds * 10, 0); // 最初の1回だけ(1マイクロ秒=10 Ticks)
        }
    }

    private async Task StopRecordingAsync()
    {
        if (recorder is null || stopping) return;
        stopping = true; recordTimer.Stop(); recordButton.Enabled = false; SetStatus("プレビューを準備しています…", Theme.Muted);
        try
        {
            if (!recorder.HasExited)
            {
                await recorder.StandardInput.WriteLineAsync("q");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var videoStop = recorder.WaitForExitAsync(timeout.Token);
                await StopDesktopAudioAsync();
                try { await videoStop; } catch { recorder.Kill(true); }
            }
            else await StopDesktopAudioAsync();
            if (tempPath is null || !File.Exists(tempPath) || new FileInfo(tempPath).Length == 0) throw new InvalidOperationException("録画ファイルを作成できませんでした。");
            var measured = Volatile.Read(ref recordedSeconds);
            recordedDuration = Math.Clamp(measured > 0 ? measured : (DateTime.UtcNow - startedAt).TotalSeconds, 0.5, MaxSeconds);
            if (recordedWithAudio) await MergeDesktopAudioAsync(recordedDuration);
            PrepareEditor(); SetStatus("●  録画完了", Theme.Success);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "録画エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); SetStatus("録画に失敗しました", Theme.Danger); }
        finally
        {
            recorder?.Dispose(); recorder = null; stopping = false; recordButton.Enabled = true; folderButton.Enabled = true; captureTarget.Enabled = true; refreshTargetsButton.Enabled = true; audioEnabled.Enabled = true;
            SetRecordButtonAsSecondary(); timerLabel.Text = TimerText(0);
        }
    }

    private void RecordTimerTick(object? sender, EventArgs e)
    {
        var wallClock = (DateTime.UtcNow - startedAt).TotalSeconds;
        var measured = Volatile.Read(ref recordedSeconds);
        timerLabel.Text = TimerText(Math.Min(MaxSeconds, (int)(measured > 0 ? measured : 0)));
        // 30秒に達するとffmpegが自分で止まる。起動に失敗して止まっていたときもここで画面を元へ戻す
        if (recorder is { HasExited: true } || wallClock >= MaxSeconds + RecordSafetySeconds) _ = StopRecordingAsync();
    }

    //--------------
    // プレビュー
    //--------------
    private void PrepareEditor()
    {
        var end = Math.Max(0.5M, (decimal)Math.Floor(recordedDuration * 10) / 10M);
        trimSlider.SetRange(0, end, 0, end); trimBubble.Visible = false;
        SyncSeekToTrim(); previewSeek.Value = previewSeek.Minimum;
        cropTimer.Stop(); cropFraction = new WpfRect(0, 0, 1, 1); cropDragMode = CropDragMode.None; UpdateCropVisual();
        resolutionInfo.Text = "現在の解像度  読み込み中…";
        clipLoaded = true; SetEditorReady(true);
        media.Source = new Uri(tempPath!); media.Position = TimeSpan.Zero; ResumePlayback();
        previewTime.Text = $"00:00.0 / {FormatTime(TrimEndSeconds - TrimStartSeconds)}";
        UpdateTrimInfo();
        BringToFrontSafely();
        // 画面が小さくて全体が入らないときだけ、編集画面が見える位置までスクロールする
        AutoScrollPosition = ClientSize.Height < editorPanel.Bottom ? new Point(0, editorPanel.Top - Dp(CardGap)) : Point.Empty;
    }

    // 録画前・録画中の編集画面。並びは変えず、中身を空にして操作できないようにする
    private void ResetEditor()
    {
        clipLoaded = false; Playing = false; scrubbing = false; previewTimer.Stop();
        estimateCancellation?.Cancel(); estimateTimer.Stop();
        cropTimer.Stop(); cropFraction = new WpfRect(0, 0, 1, 1); cropDragMode = CropDragMode.None; UpdateCropVisual();
        trimSlider.SetRange(0, 0.1M, 0, 0.1M); trimBubble.Visible = false;
        previewSeek.Minimum = 0; previewSeek.Maximum = 5; previewSeek.Value = 0;
        previewTime.Text = "00:00.0 / 00:00.0";
        resolutionInfo.Text = "現在の解像度  —";
        trimInfo.Text = "録画すると、ここで切り取りと保存ができます";
        cropInfo.Text = "選択範囲: フレーム全体";
        SetEditorReady(false);
    }

    // 映像があるときだけ、切り取り・シーク・保存を操作できる
    private void SetEditorReady(bool ready)
    {
        trimSlider.Enabled = previewSeek.Enabled = cropResetButton.Enabled = ready;
        playButton.Enabled = markInButton.Enabled = markOutButton.Enabled = ready;
        ApplyExportLook();
        ShowPreviewPlaceholder(!ready);
    }

    private void ShowPreviewPlaceholder(bool show)
    {
        previewPlaceholder.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
        if (show)
        {
            previewPlaceholder.Visibility = System.Windows.Visibility.Visible; previewPlaceholder.Opacity = 1;
            if (Motion.Enabled) previewPlaceholder.BeginAnimation(System.Windows.UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            return;
        }
        if (!Motion.Enabled) { previewPlaceholder.Visibility = System.Windows.Visibility.Collapsed; return; }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        fade.Completed += (_, _) => { if (clipLoaded) previewPlaceholder.Visibility = System.Windows.Visibility.Collapsed; };
        previewPlaceholder.BeginAnimation(System.Windows.UIElement.OpacityProperty, fade);
    }

    private void ResetFileCard()
    {
        finalPath = null;
        fileName.Text = "保存したファイルがここに表示されます"; fileName.ForeColor = Theme.Muted;
        fileInfo.Text = string.Empty;
        fileHint.Text = "保存すると、このカードを Discord などの投稿欄へドラッグして添付できます";
        fileCard.Cursor = Cursors.Default;
        fileCardFader?.FadeTo(Theme.SurfaceElevated);
    }

    // 読み込めた映像の実際の大きさと長さを表示・反映する
    private void OnMediaOpened()
    {
        if (Motion.Enabled) media.BeginAnimation(System.Windows.UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        UpdateCropInfo();
        if (!media.NaturalDuration.HasTimeSpan) return;
        var actual = Math.Min(MaxSeconds, media.NaturalDuration.TimeSpan.TotalSeconds);
        if (Math.Abs(actual - recordedDuration) < 0.1 || actual < 0.5) return;
        recordedDuration = actual;
        var end = Math.Max(0.5M, (decimal)Math.Floor(actual * 10) / 10M);
        trimSlider.SetRange(0, end, 0, end);
        SyncSeekToTrim();
        UpdateTrimInfo();
    }

    // 再生位置を動かす。つまみを素早く動かすと要求が大量に来るため、最新の1つだけを一定間隔で映像へ反映する。
    // 動かした直後は映像の位置が追いつかず古い値を返すので、その間は位置の確認をしない(確認すると、戻し直しが重なって小刻みに戻る)。
    private void SeekTo(double seconds, bool immediate)
    {
        pendingSeek = Math.Clamp(seconds, 0, Math.Max(0.1, recordedDuration));
        if (immediate) FlushSeek(true);
        else if (!seekTimer.Enabled) seekTimer.Start();
    }

    private void FlushSeek(bool force)
    {
        if (pendingSeek is not double target) { seekTimer.Stop(); return; }
        if (!force && Environment.TickCount64 - lastSeekAt < 70) return;
        pendingSeek = null; lastSeekAt = Environment.TickCount64; lastSeekTarget = target;
        media.Position = TimeSpan.FromSeconds(target);
        if (pendingSeek is null) seekTimer.Stop();
    }

    private bool SeekSettled => pendingSeek is null && Environment.TickCount64 - lastSeekAt > 450;

    // 今の再生位置。動かした直後は映像の位置が追いつかないため、動かした先を使う
    private double CurrentPosition => pendingSeek ?? (SeekSettled ? media.Position.TotalSeconds : lastSeekTarget);

    //--------------
    // 再生・一時停止・コマ送り
    //--------------
    private void ResumePlayback() { media.Play(); Playing = true; previewTimer.Start(); }

    private void TogglePlayback()
    {
        if (!clipLoaded || exporting) return;
        if (Playing) { media.Pause(); Playing = false; return; }
        // 範囲の終わりで止めていたときは、頭から再生する
        var position = CurrentPosition;
        if (position >= TrimEndSeconds - 0.05 || position < TrimStartSeconds) SeekTo(TrimStartSeconds, true);
        ResumePlayback();
    }

    // つまみ・シークバーを動かしている間は一時停止してその位置を見せ、離したら止める前の状態へ戻す
    private void BeginScrub()
    {
        if (scrubbing) return;
        scrubbing = true; media.Pause();
    }

    private void EndScrub(double? restartAt)
    {
        if (!scrubbing) return;
        scrubbing = false;
        if (!clipLoaded || exporting) return;
        if (restartAt is double at) SeekTo(at, true);
        if (Playing) ResumePlayback();
    }

    private void StepFrame(double seconds)
    {
        if (Playing) { media.Pause(); Playing = false; }
        var position = Math.Clamp(CurrentPosition + seconds, TrimStartSeconds, TrimEndSeconds);
        SeekTo(position, true);
        previewSeek.Value = Math.Clamp((int)Math.Round(position * 10), previewSeek.Minimum, previewSeek.Maximum);
        ShowPreviewTime(position);
    }

    private void MarkIn() { if (clipLoaded && !exporting) trimSlider.SetStart((decimal)CurrentPosition); }
    private void MarkOut() { if (clipLoaded && !exporting) trimSlider.SetEnd((decimal)CurrentPosition); }

    // 編集のキー操作。文字の入力中と、矢印キーを自分で使う部品の操作中は、その部品へそのまま渡す
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (clipLoaded && !exporting && !scrubbing && ActiveControl is not TextBoxBase)
        {
            var arrowsFree = ActiveControl is not (SegmentedSelect or DropdownSelect or DecimalInput);
            switch (keyData)
            {
                case Keys.Space: TogglePlayback(); return true;
                case Keys.I: MarkIn(); return true;
                case Keys.O: MarkOut(); return true;
                case Keys.Left when arrowsFree: StepFrame(-0.1); return true;
                case Keys.Right when arrowsFree: StepFrame(0.1); return true;
                case Keys.Shift | Keys.Left when arrowsFree: StepFrame(-1); return true;
                case Keys.Shift | Keys.Right when arrowsFree: StepFrame(1); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void PreviewTimerTick(object? sender, EventArgs e)
    {
        if (!Playing || scrubbing) return;
        var position = media.Position.TotalSeconds;
        if (SeekSettled && (position >= TrimEndSeconds - 0.03 || position < TrimStartSeconds - 0.3))
        {
            // 範囲の終わりに来たら頭へ戻して再生を続ける(戻し先へ着くまでは、位置の確認をしない)
            SeekTo(TrimStartSeconds, true);
            media.Play();
            position = TrimStartSeconds;
        }
        position = Math.Clamp(position, TrimStartSeconds, TrimEndSeconds);
        previewSeek.Value = Math.Clamp((int)Math.Round(position * 10), previewSeek.Minimum, previewSeek.Maximum);
        ShowPreviewTime(position);
    }

    private void SeekChanged(object? sender, EventArgs e)
    {
        var position = Math.Clamp(previewSeek.Value / 10d, TrimStartSeconds, TrimEndSeconds);
        SeekTo(position, false);
        ShowPreviewTime(position);
    }

    // シークバーは切り取り範囲いっぱいを表す(範囲を狭めると、バーの全幅がその範囲になる)。時間も切り取り範囲の頭からの経過で表す
    private void SyncSeekToTrim()
    {
        var minimum = (int)Math.Round(TrimStartSeconds * 10);
        var maximum = Math.Max(minimum + 1, (int)Math.Round(TrimEndSeconds * 10));
        previewSeek.Minimum = minimum; previewSeek.Maximum = maximum;
        previewSeek.Value = Math.Clamp(previewSeek.Value, minimum, maximum);
        if (clipLoaded) ShowPreviewTime(Math.Clamp(media.Position.TotalSeconds, TrimStartSeconds, TrimEndSeconds));
    }

    private void ShowPreviewTime(double position) =>
        previewTime.Text = $"{FormatTime(Math.Clamp(position - TrimStartSeconds, 0, TrimEndSeconds - TrimStartSeconds))} / {FormatTime(TrimEndSeconds - TrimStartSeconds)}";

    //--------------
    // 書き出し
    //--------------
    private void SetExportBusy(bool busy)
    {
        recordButton.Enabled = folderButton.Enabled = !busy;
        limitEnabled.Enabled = !busy; maxSize.Enabled = !busy && limitEnabled.Checked;
        formatSelect.Enabled = !busy;
        trimSlider.Enabled = previewSeek.Enabled = cropResetButton.Enabled = !busy && clipLoaded;
        playButton.Enabled = markInButton.Enabled = markOutButton.Enabled = !busy && clipLoaded;
    }

    private async Task ExportAsync()
    {
        if (tempPath is null || !File.Exists(tempPath)) return;
        var resumeAfterExport = Playing;
        Playing = false; previewTimer.Stop(); media.Stop(); SetExportBusy(true);
        exporting = true; estimateCancellation?.Cancel(); estimateTimer.Stop();
        var compressing = limitEnabled.Checked;
        exportButton.Text = compressing ? "圧縮中…  クリックで中止" : "書き出し中…  クリックで中止";
        exportButton.Cursor = Cursors.Hand;
        trimInfo.Text = "準備しています…";
        exportCancellation = new CancellationTokenSource();
        var cancellation = exportCancellation.Token;
        var previousPath = finalPath;
        busyTimer.Start(); exportButton.Invalidate();
        try
        {
            Directory.CreateDirectory(settings.SaveDirectory!);
            finalPath = NextOutputPath(settings.SaveDirectory!, FormatExtension(outputFormat));
            var limit = CurrentLimit;
            var encoder = new ClipEncoder(FfmpegPath, text => { SetStatus(text, Theme.Muted); trimInfo.Text = text; });
            var result = await encoder.EncodeAsync(new ClipRequest
            {
                Input = tempPath!, Output = finalPath, Start = TrimStartSeconds, Duration = TrimEndSeconds - TrimStartSeconds,
                Format = outputFormat, IncludeAudio = recordedWithAudio, ByteLimit = limit,
                CropX = cropFraction.X, CropY = cropFraction.Y, CropWidth = cropFraction.Width, CropHeight = cropFraction.Height,
            }, cancellation);
            ShowSavedFile(finalPath, result, limit); OpenInExplorer(finalPath);
        }
        catch (OperationCanceledException)
        {
            finalPath = previousPath; // 途中の出力は消えている。カードには前に保存したファイルが残る
            SetStatus("●  書き出しを中止しました", Theme.Muted);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); SetStatus("保存に失敗しました", Theme.Danger); }
        finally
        {
            exporting = false; busyTimer.Stop();
            exportCancellation?.Dispose(); exportCancellation = null;
            SetExportBusy(false);
            ApplyExportLook(); exportButton.Invalidate();
            UpdateTrimText(); // 保存後の「現在の解像度」は実際の値のまま残す
            if (tempPath is not null) { SeekTo(TrimStartSeconds, true); if (resumeAfterExport) ResumePlayback(); else media.Pause(); }
        }
    }

    // 出力名は保存した日付と時刻(年月日時。例 2026102810.mp4)。同じ時間内に2つ目以降を保存したときは _2, _3 … を付け、上書きしない
    private static string NextOutputPath(string directory, string extension)
    {
        var stem = DateTime.Now.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"{stem}.{extension}");
        for (var number = 2; File.Exists(path); number++) path = Path.Combine(directory, $"{stem}_{number}.{extension}");
        return path;
    }

    // 圧縮中にもう一度押すと、実行中のffmpegを止めて書き出しをやめる
    private void CancelExport()
    {
        if (exportCancellation is null || exportCancellation.IsCancellationRequested) return;
        exportCancellation.Cancel();
        exportButton.Text = "中止しています…";
        trimInfo.Text = "書き出しを中止しています…";
    }

    // 圧縮中の読み込み表示: ボタンの上を光の帯が流れ、左に回る円弧を出す
    private void DrawExportBusy(object? sender, PaintEventArgs e)
    {
        if (!exporting) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var zoom = exportButton.DeviceDpi / 96f;
        var phase = Environment.TickCount64 % 1400 / 1400f;
        var bandWidth = exportButton.Width * 0.35f;
        var x = -bandWidth + phase * (exportButton.Width + bandWidth);
        using (var band = new LinearGradientBrush(new RectangleF(x, 0, bandWidth, exportButton.Height), Color.White, Color.White, LinearGradientMode.Horizontal))
        {
            band.InterpolationColors = new ColorBlend(3)
            {
                Positions = [0f, 0.5f, 1f],
                Colors = [Color.FromArgb(0, 255, 255, 255), Color.FromArgb(80, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)],
            };
            g.FillRectangle(band, x, 0, bandWidth, exportButton.Height);
        }
        var radius = 9 * zoom; var cx = 30 * zoom; var cy = exportButton.Height / 2f;
        using var track = new Pen(Color.FromArgb(70, Theme.TextOnAccent), 2.6f * zoom);
        using var arc = new Pen(Theme.TextOnAccent, 2.6f * zoom) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(track, cx - radius, cy - radius, radius * 2, radius * 2);
        g.DrawArc(arc, cx - radius, cy - radius, radius * 2, radius * 2, Environment.TickCount64 % 900 / 900f * 360f, 100f);
    }

    private bool HasFfmpeg() { if (File.Exists(FfmpegPath)) return true; try { using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { UseShellExecute = false, CreateNoWindow = true }); p?.WaitForExit(1500); return p?.ExitCode == 0; } catch { return false; } }

    private async Task RunAsync(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(FfmpegPath, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
        if (p is null) throw new InvalidOperationException("エンコードを開始できませんでした。");
        var errorTask = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); var error = await errorTask;
        if (p.ExitCode != 0) throw new InvalidOperationException("エンコードに失敗しました。\n" + error);
    }

    private void ShowSavedFile(string path, ClipResult result, long? limit)
    {
        var info = new FileInfo(path);
        fileName.Text = "↗  " + info.Name; fileName.ForeColor = Theme.Text; fileCard.Cursor = Cursors.Hand;
        var fill = limit is long l ? $"  •  上限の{(double)result.Bytes / l * 100:F0}%" : string.Empty;
        var fps = result.Fps > 0 ? $"  •  {result.Fps:0.#}fps" : string.Empty;
        fileInfo.Text = $"{result.Bytes / 1024d / 1024d:F2} MB{fill}  •  {result.Width}×{result.Height}{fps}";
        resolutionInfo.Text = $"現在の解像度  {result.Width}×{result.Height}{fps}";
        fileHint.Text = "このカードを Discord などの投稿欄へドラッグして添付";
        ScrollControlIntoView(fileCard);
        SetStatus("●  保存完了", Theme.Success);
    }

    private void StartFileDrag(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && finalPath is not null && File.Exists(finalPath)) fileCard.DoDragDrop(new DataObject(DataFormats.FileDrop, new[] { finalPath }), DragDropEffects.Copy); }

    private void ChooseFolder(object? sender, EventArgs e)
    {
        Directory.CreateDirectory(settings.SaveDirectory!);
        using var dialog = new FolderBrowserDialog { Description = "録画クリップの保存先", SelectedPath = settings.SaveDirectory, UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == DialogResult.OK) { settings.SaveDirectory = dialog.SelectedPath; settings.Save(); SetStatus("保存先: " + settings.SaveDirectory, Theme.Muted); }
    }

    private static void OpenInExplorer(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    private void CleanupTemp()
    {
        previewTimer.Stop(); Playing = false; media.Stop(); media.Source = null;
        estimateCancellation?.Cancel(); estimateTimer.Stop();
        try { loopbackCapture?.StopRecording(); } catch { }
        try { loopbackCapture?.Dispose(); } catch { }
        try { loopbackWriter?.Dispose(); } catch { }
        loopbackCapture = null; loopbackWriter = null; loopbackStopped = null;
        DeleteInBackground(tempPath); DeleteInBackground(audioTempPath);
        tempPath = null; audioTempPath = null;
    }

    // 前回の終了時に消せなかった一時ファイル(1時間以上前のもの)を裏で片付ける
    private static void SweepOldTempFiles() => _ = Task.Run(() =>
    {
        try
        {
            foreach (var pattern in new[] { "usk-clip-*.mp4", "usk-preview-*.mp4", "usk-audio-*.wav", "discord-clip-*.mp4", "discord-preview-*.mp4", "discord-audio-*.wav", "dcr-*" })
                foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), pattern))
                    try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1)) File.Delete(file); } catch { }
        }
        catch { }
    });

    // 再生に使っていた動画の削除は数秒かかることがあり、UIスレッドで行うと画面が固まる。裏で、削除できるまで繰り返す
    private static void DeleteInBackground(string? path)
    {
        if (path is null) return;
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try { if (File.Exists(path)) File.Delete(path); return; }
                catch { await Task.Delay(500); }
            }
        });
    }

    private void OnClosing(object? sender, FormClosingEventArgs e) { UnregisterHotKey(Handle, HotkeyId); previewTimer.Stop(); media.Stop(); if (recorder is { HasExited: false }) try { recorder.Kill(true); } catch { } CleanupTemp(); }

    private enum CaptureKind { Monitor, Window }
    private enum CropDragMode { None, Create, Move, ResizeTopLeft, ResizeTopRight, ResizeBottomLeft, ResizeBottomRight }

    private sealed class CaptureSource
    {
        public required string DisplayName { get; init; }
        public required string Key { get; init; }
        public required CaptureKind Kind { get; init; }
        public Rectangle Bounds { get; init; }
        public string? DeviceName { get; init; } // モニターの名前(\\.\DISPLAY1 など)
        public IntPtr WindowHandle { get; init; }
        public static CaptureSource ForMonitor(string displayName, Rectangle bounds, string? deviceName) => new() { DisplayName = displayName, Key = $"monitor:{bounds.X}:{bounds.Y}:{bounds.Width}:{bounds.Height}", Kind = CaptureKind.Monitor, Bounds = bounds, DeviceName = deviceName };
        public static CaptureSource ForWindow(string displayName, IntPtr handle) => new() { DisplayName = displayName, Key = $"window:{handle}", Kind = CaptureKind.Window, WindowHandle = handle };
        public override string ToString() => DisplayName;
    }
}
