//==================================================
// Controls
// 画面で使う自前の部品。トグル・トリミングスライダー・「？」ヘルプ、
// 禁則処理つきの折り返し、ヘルプ札(ツールチップ)の計測と描画をまとめる。
//==================================================

using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;

namespace uskCompressionRecorder;

//--------------
// Motion
// 状態が切り替わるときの動き(0.12〜0.3秒・終わり際をゆっくり)を共通にする。
// OSのアニメーション効果がオフのときは動かさず、すぐ切り替える。
//--------------
internal static class Motion
{
    public const int DefaultMilliseconds = 180;
    public static bool Enabled => SystemInformation.UIEffectsEnabled;
    public static float EaseOut(float t) => 1 - (float)Math.Pow(1 - Math.Clamp(t, 0f, 1f), 3);
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static Color Lerp(Color a, Color b, float t) =>
        Color.FromArgb((int)Lerp(a.A, b.A, t), (int)Lerp(a.R, b.R, t), (int)Lerp(a.G, b.G, t), (int)Lerp(a.B, b.B, t));
}

//--------------
// ColorFader
// 色を今の色から新しい色へなめらかに切り替える。途中で向きが変わっても、今見えている色から動かす。
//--------------
internal sealed class ColorFader
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    private readonly Action<Color> apply;
    private readonly int milliseconds;
    private Color from, to;
    private long startedAt;

    public Color Current { get; private set; }

    public ColorFader(Color initial, Action<Color> apply, int milliseconds = Motion.DefaultMilliseconds)
    {
        Current = from = to = initial; this.apply = apply; this.milliseconds = milliseconds;
        timer.Tick += (_, _) =>
        {
            var t = (Environment.TickCount64 - startedAt) / (float)this.milliseconds;
            Current = Motion.Lerp(from, to, Motion.EaseOut(t));
            this.apply(Current);
            if (t >= 1) timer.Stop();
        };
    }

    public void FadeTo(Color target, bool animate = true)
    {
        if (target == to && (!timer.Enabled)) return;
        to = target;
        if (!animate || !Motion.Enabled) { timer.Stop(); Current = from = target; apply(target); return; }
        from = Current; startedAt = Environment.TickCount64; timer.Start();
    }
}

//--------------
// FloatFader
// 0〜1の値を目標へなめらかに動かす(ホバーや開閉など)。途中で向きが変わっても今の値から動かす。
//--------------
internal sealed class FloatFader
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    private readonly Action<float> apply;
    private readonly int milliseconds;
    private float from, to;
    private long startedAt;

    public float Current { get; private set; }

    public FloatFader(Action<float> apply, int milliseconds = 140)
    {
        this.apply = apply; this.milliseconds = milliseconds;
        timer.Tick += (_, _) =>
        {
            var t = (Environment.TickCount64 - startedAt) / (float)this.milliseconds;
            Current = Motion.Lerp(from, to, Motion.EaseOut(t));
            this.apply(Current);
            if (t >= 1) timer.Stop();
        };
    }

    public void FadeTo(float target)
    {
        if (target == to && (Current == to || timer.Enabled)) return;
        to = target;
        if (!Motion.Enabled) { timer.Stop(); Current = from = target; apply(target); return; }
        from = Current; startedAt = Environment.TickCount64; timer.Start();
    }
}

//--------------
// ToggleSwitch
// オン・オフでつまみが滑り、色がなめらかに変わる。つまみは動いている間だけ少し横に伸びる。
//--------------
internal sealed class ToggleSwitch : CheckBox
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    private readonly FloatFader hoverFader;
    private float hover;
    private float progress;    // 0:オフ 1:オン(描画に使う、今の位置)
    private float startProgress;
    private long startedAt;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        TabStop = true;
        BackColor = Color.Transparent;
        timer.Tick += (_, _) =>
        {
            var t = (Environment.TickCount64 - startedAt) / (float)Motion.DefaultMilliseconds;
            progress = Motion.Lerp(startProgress, Checked ? 1f : 0f, Motion.EaseOut(t));
            Invalidate();
            if (t >= 1) { timer.Stop(); progress = Checked ? 1f : 0f; }
        };
        progress = Checked ? 1f : 0f;
        hoverFader = new FloatFader(v => { hover = v; Invalidate(); });
    }

    protected override void OnMouseEnter(EventArgs e) { hoverFader.FadeTo(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hoverFader.FadeTo(0); base.OnMouseLeave(e); }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        if (!IsHandleCreated || !Visible || !Motion.Enabled) { timer.Stop(); progress = Checked ? 1f : 0f; Invalidate(); return; }
        startProgress = progress; startedAt = Environment.TickCount64; timer.Start();
    }

    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Theme.SurfaceElevated);
        var track = new Rectangle(1, 2, Width - 3, Height - 5);
        var radius = track.Height / 2;
        using var path = RoundedRect(track, radius);
        var p = Enabled ? progress : (Checked ? 1f : 0f);
        using var fill = new SolidBrush(Motion.Lerp(Motion.Lerp(Theme.SurfaceSubtle, Theme.Accent, Enabled ? p : 0f), Color.White, Enabled ? 0.08f * hover : 0f));
        using var border = new Pen(Motion.Lerp(Theme.BorderStrong, Theme.AccentHover, Enabled ? p : 0f));
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
        var knobSize = track.Height - 6;
        var stretch = 1f + 0.3f * (float)Math.Sin(Math.PI * p); // 動いている間だけ横に伸びる
        var knobWidth = knobSize * stretch;
        var knobX = Motion.Lerp(track.Left + 3, track.Right - knobSize - 3, p) - (knobWidth - knobSize) * p;
        var knobColor = Enabled ? Motion.Lerp(Theme.Text, Theme.TextOnAccent, p) : Theme.Muted;
        using var knob = new SolidBrush(knobColor);
        using var knobPath = RoundedRect(new Rectangle((int)knobX, track.Top + 3, (int)knobWidth, knobSize), knobSize / 2);
        e.Graphics.FillPath(knob, knobPath);
        // 枠は、キーボードで操作しているときだけ出す(マウスで押した後に残さない)
        if (Focused && ShowFocusCues)
        {
            using var ring = RoundedRect(new Rectangle(0, 1, Width - 1, Height - 3), (Height - 3) / 2);
            using var focusPen = new Pen(Theme.Focus, 1.5f);
            e.Graphics.DrawPath(focusPen, ring);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 90, 180);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 180);
        path.CloseFigure();
        return path;
    }
}

//--------------
// TrimRangeSlider
// 切り取る時間の範囲。つまみで始点・終点を、つまみの間をドラッグすると長さを保ったまま範囲をずらす。
// カーソルが近いつまみ・動かしているつまみだけを大きくする。
//--------------
internal sealed class TrimRangeSlider : Control
{
    private const decimal MinGap = 0.2M;
    private bool draggingStart;
    private bool draggingEnd;
    private bool draggingRange;
    private decimal rangeGrabOffset; // 範囲をずらすとき、つかんだ位置と始点の差
    private readonly FloatFader startFader;
    private readonly FloatFader endFader;
    private readonly FloatFader rangeFader;
    private float startEmphasis, endEmphasis, rangeEmphasis; // 0:通常 0.5:カーソルが近い 1:動かしている

    public decimal Minimum { get; private set; }
    public decimal Maximum { get; private set; } = 30;
    public decimal StartValue { get; private set; }
    public decimal EndValue { get; private set; } = 30;

    public event EventHandler? RangeChanged;
    public event EventHandler<decimal>? DragPositionChanged;
    public event EventHandler? DragEnded;

    public TrimRangeSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        startFader = new FloatFader(v => { startEmphasis = v; Invalidate(); });
        endFader = new FloatFader(v => { endEmphasis = v; Invalidate(); });
        rangeFader = new FloatFader(v => { rangeEmphasis = v; Invalidate(); });
    }

    protected override void OnMouseLeave(EventArgs e) { if (!draggingStart && !draggingEnd && !draggingRange) UpdateEmphasis(null); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { UpdateEmphasis(null); base.OnEnabledChanged(e); }

    private float Scaled(float value) => value * DeviceDpi / 96f;
    private float HandleRadius => Scaled(Theme.SliderHandleRadius);

    // 押したときに何を動かすか: 近いつまみ、つまみから離れた範囲の内側なら範囲ごと
    private (bool Start, bool End, bool Range) HitTest(int x)
    {
        var startX = ValueToX(StartValue); var endX = ValueToX(EndValue);
        var inside = x > startX + HandleRadius * 1.5f && x < endX - HandleRadius * 1.5f;
        if (inside && Maximum - Minimum > EndValue - StartValue) return (false, false, true);
        return Math.Abs(x - startX) <= Math.Abs(x - endX) ? (true, false, false) : (false, true, false);
    }

    private void UpdateEmphasis(int? x)
    {
        (bool Start, bool End, bool Range) hit = x is int px && Enabled ? HitTest(px) : default;
        startFader.FadeTo(draggingStart ? 1f : hit.Start ? 0.5f : 0f);
        endFader.FadeTo(draggingEnd ? 1f : hit.End ? 0.5f : 0f);
        rangeFader.FadeTo(draggingRange ? 1f : hit.Range ? 0.5f : 0f);
        Cursor = hit.Range || draggingRange ? Cursors.SizeWE : Cursors.Hand;
    }

    public void SetRange(decimal minimum, decimal maximum, decimal start, decimal end)
    {
        Minimum = minimum; Maximum = Math.Max(maximum, minimum + 0.1M);
        StartValue = Math.Clamp(start, Minimum, Maximum);
        EndValue = Math.Clamp(end, Minimum, Maximum);
        Invalidate();
    }

    // 始点・終点だけを変える(範囲の端は変えない)。短すぎる範囲にはしない
    public void SetStart(decimal value) { StartValue = Math.Clamp(Math.Round(value, 1), Minimum, EndValue - MinGap); Invalidate(); RangeChanged?.Invoke(this, EventArgs.Empty); }
    public void SetEnd(decimal value) { EndValue = Math.Clamp(Math.Round(value, 1), StartValue + MinGap, Maximum); Invalidate(); RangeChanged?.Invoke(this, EventArgs.Empty); }

    public float PositionForValue(decimal value) => ValueToX(value);

    private float ValueToX(decimal value)
    {
        var radius = HandleRadius;
        if (Maximum <= Minimum || Width <= radius * 2) return radius;
        var ratio = (float)((value - Minimum) / (Maximum - Minimum));
        return radius + ratio * (Width - radius * 2);
    }

    private decimal XToValue(int x)
    {
        var radius = HandleRadius;
        if (Width <= radius * 2) return Minimum;
        var ratio = Math.Clamp((x - radius) / (Width - radius * 2), 0f, 1f);
        return Minimum + (decimal)ratio * (Maximum - Minimum);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        (draggingStart, draggingEnd, draggingRange) = HitTest(e.X);
        rangeGrabOffset = XToValue(e.X) - StartValue;
        UpdateEmphasis(e.X);
        if (!draggingRange) UpdateDrag(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (draggingStart || draggingEnd || draggingRange) UpdateDrag(e.X);
        else UpdateEmphasis(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!draggingStart && !draggingEnd && !draggingRange) return;
        draggingStart = draggingEnd = draggingRange = false;
        UpdateEmphasis(ClientRectangle.Contains(e.Location) ? e.X : null);
        DragEnded?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDrag(int x)
    {
        var raw = Math.Round(XToValue(x), 1);
        if (draggingStart) StartValue = Math.Clamp(raw, Minimum, EndValue - MinGap);
        else if (draggingEnd) EndValue = Math.Clamp(raw, StartValue + MinGap, Maximum);
        else if (draggingRange)
        {
            var length = EndValue - StartValue;
            StartValue = Math.Clamp(Math.Round(XToValue(x) - rangeGrabOffset, 1), Minimum, Maximum - length);
            EndValue = StartValue + length;
        }
        Invalidate();
        RangeChanged?.Invoke(this, EventArgs.Empty);
        DragPositionChanged?.Invoke(this, draggingEnd ? EndValue : StartValue);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Theme.SurfaceElevated);
        var radius = HandleRadius;
        var trackY = Height / 2f;
        using var trackPen = new Pen(Theme.Border, Scaled(Theme.TrackThickness));
        e.Graphics.DrawLine(trackPen, radius, trackY, Width - radius, trackY);
        var startX = ValueToX(StartValue); var endX = ValueToX(EndValue);
        // 範囲ごとずらすときは、範囲の線を太くして動かす対象を示す
        using var rangePen = new Pen(Enabled ? Motion.Lerp(Theme.Accent, Theme.AccentHover, rangeEmphasis) : Theme.BorderStrong, Scaled(Theme.TrackThickness) * (1f + 0.75f * rangeEmphasis));
        e.Graphics.DrawLine(rangePen, startX, trackY, endX, trackY);
        DrawHandle(e.Graphics, startX, trackY, radius, startEmphasis);
        DrawHandle(e.Graphics, endX, trackY, radius, endEmphasis);
    }

    // 近づく・つかむほど少し大きくし、周りに薄い輪を出す
    private void DrawHandle(Graphics g, float x, float y, float baseRadius, float emphasis)
    {
        var radius = baseRadius * (1f + 0.25f * emphasis);
        if (Enabled && emphasis > 0)
        {
            var halo = radius * 1.8f;
            using var haloBrush = new SolidBrush(Color.FromArgb((int)(60 * emphasis), Theme.Accent));
            g.FillEllipse(haloBrush, x - halo, y - halo, halo * 2, halo * 2);
        }
        using var fill = new SolidBrush(Enabled ? Theme.Text : Theme.Muted);
        using var border = new Pen(Enabled ? Theme.Accent : Theme.BorderStrong, Scaled(Theme.AccentBorderWidth));
        g.FillEllipse(fill, x - radius, y - radius, radius * 2, radius * 2);
        g.DrawEllipse(border, x - radius, y - radius, radius * 2, radius * 2);
    }
}

//--------------
// SeekBar
// 再生位置を動かすバー。標準のTrackBarは明るい配色になるため、他の部品と同じ見た目で自前に描く。
//--------------
internal sealed class SeekBar : Control
{
    private int value;
    private bool dragging;
    private readonly FloatFader hoverFader;
    private readonly FloatFader pressFader;
    private float hover, press;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Minimum { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum { get; set; } = 300;
    public event EventHandler? Scroll; // 利用者が動かしたときだけ起きる(Valueをコードから変えても起きない)
    public event EventHandler? DragStarted;
    public event EventHandler? DragEnded;

    public SeekBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        hoverFader = new FloatFader(v => { hover = v; Invalidate(); });
        pressFader = new FloatFader(v => { press = v; Invalidate(); });
    }

    protected override void OnMouseEnter(EventArgs e) { hoverFader.FadeTo(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hoverFader.FadeTo(0); base.OnMouseLeave(e); }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum));
            if (clamped == this.value) return;
            this.value = clamped;
            Invalidate();
        }
    }

    private float HandleRadius => Theme.SeekHandleRadius * DeviceDpi / 96f;

    private float ValueToX()
    {
        var radius = HandleRadius;
        if (Maximum <= Minimum || Width <= radius * 2) return radius;
        return radius + (value - Minimum) / (float)(Maximum - Minimum) * (Width - radius * 2);
    }

    private void SetFromX(int x)
    {
        var radius = HandleRadius;
        if (Width <= radius * 2) return;
        var ratio = Math.Clamp((x - radius) / (Width - radius * 2), 0f, 1f);
        value = Minimum + (int)Math.Round(ratio * (Maximum - Minimum));
        Invalidate();
        Scroll?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        dragging = true; pressFader.FadeTo(1);
        DragStarted?.Invoke(this, EventArgs.Empty);
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e) { if (dragging) SetFromX(e.X); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging) return;
        dragging = false; pressFader.FadeTo(0);
        DragEnded?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Theme.SurfaceElevated);
        var radius = HandleRadius;
        var y = Height / 2f;
        var thickness = Theme.TrackThickness * DeviceDpi / 96f;
        var handleRadius = radius * (1f + 0.15f * hover + 0.2f * press);
        using var trackPen = new Pen(Theme.Border, thickness);
        e.Graphics.DrawLine(trackPen, radius, y, Width - radius, y);
        var x = ValueToX();
        // 再生済みの部分はアクセント色にし、どこまで進んだかを読み取りやすくする
        using var playedPen = new Pen(Enabled ? Theme.Accent : Theme.BorderStrong, thickness);
        e.Graphics.DrawLine(playedPen, radius, y, x, y);
        using var fill = new SolidBrush(Enabled ? Theme.Text : Theme.Muted);
        using var border = new Pen(Enabled ? Motion.Lerp(Theme.Muted, Theme.Accent, Math.Max(hover, press)) : Theme.BorderStrong, Theme.BorderWidth * 2 * DeviceDpi / 96f);
        e.Graphics.FillEllipse(fill, x - handleRadius, y - handleRadius, handleRadius * 2, handleRadius * 2);
        e.Graphics.DrawEllipse(border, x - handleRadius, y - handleRadius, handleRadius * 2, handleRadius * 2);
    }
}

//--------------
// PlayPauseButton
// プレビューの再生・一時停止。三角と2本線を、縮めながら入れ替えて切り替える。
//--------------
internal sealed class PlayPauseButton : Control
{
    private readonly FloatFader hoverFader;
    private readonly FloatFader stateFader;
    private float hover;
    private float state; // 0:再生の三角 1:一時停止の2本線(描画に使う、今の形)
    private bool playing;

    public PlayPauseButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = "再生";
        hoverFader = new FloatFader(v => { hover = v; Invalidate(); });
        stateFader = new FloatFader(v => { state = v; Invalidate(); }, Motion.DefaultMilliseconds);
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Playing
    {
        get => playing;
        set
        {
            if (playing == value) return;
            playing = value;
            AccessibleName = value ? "一時停止" : "再生";
            stateFader.FadeTo(value ? 1 : 0);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { hoverFader.FadeTo(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hoverFader.FadeTo(0); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; } base.OnKeyDown(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.SurfaceElevated);
        var zoom = DeviceDpi / 96f;
        var circle = new RectangleF(1, 1, Width - 3, Height - 3);
        var hot = Enabled ? hover : 0f;
        using (var back = new SolidBrush(Enabled ? Motion.Lerp(Theme.Accent, Theme.AccentHover, hot) : Theme.SurfaceSubtle))
            g.FillEllipse(back, circle);
        // 枠は、キーボードで操作しているときだけ出す
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Theme.Focus, 1.5f);
            g.DrawEllipse(ring, circle);
        }
        var color = Enabled ? Theme.TextOnAccent : Theme.Muted;
        var cx = Width / 2f; var cy = Height / 2f; var size = 6.5f * zoom;
        // 形が入れ替わる間は、古い形を縮めてから新しい形を広げる
        var playScale = Math.Max(0f, 1f - state * 2f);
        var pauseScale = Math.Max(0f, state * 2f - 1f);
        using var brush = new SolidBrush(color);
        if (playScale > 0)
        {
            var s = size * playScale;
            g.FillPolygon(brush, new[] { new PointF(cx - s * 0.75f, cy - s), new PointF(cx - s * 0.75f, cy + s), new PointF(cx + s * 1.05f, cy) });
        }
        if (pauseScale > 0)
        {
            var h = size * pauseScale; var w = 2.6f * zoom * pauseScale; var gap = 2.4f * zoom;
            g.FillRectangle(brush, cx - gap - w, cy - h, w, h * 2);
            g.FillRectangle(brush, cx + gap, cy - h, w, h * 2);
        }
    }
}

//--------------
// DropdownSelect
// 録画対象のプルダウン。標準のComboBoxは枠と矢印が明るい配色になるため、同じ見た目で自前に描く。
//--------------
internal sealed class DropdownSelect : Control
{
    private int selectedIndex = -1;
    private readonly FloatFader hoverFader;
    private readonly FloatFader openFader;
    private float hover;
    private float openAmount;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<object> Items { get; } = [];
    public event EventHandler? SelectedIndexChanged;
    public event EventHandler? Opening; // 一覧を開く直前(内容の更新用)

    public DropdownSelect()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.ComboBox;
        hoverFader = new FloatFader(v => { hover = v; Invalidate(); });
        openFader = new FloatFader(v => { openAmount = v; Invalidate(); }, 200);
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            var clamped = Math.Clamp(value, -1, Items.Count - 1);
            if (clamped == selectedIndex) return;
            selectedIndex = clamped;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? SelectedItem => selectedIndex >= 0 && selectedIndex < Items.Count ? Items[selectedIndex] : null;

    // 一覧の入れ替えと選択を、途中の状態を見せずに一度で行う
    public void SetItems(IEnumerable<object> items, int index)
    {
        Items.Clear(); Items.AddRange(items);
        selectedIndex = Math.Clamp(index, -1, Items.Count - 1);
        Invalidate();
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseEnter(EventArgs e) { hoverFader.FadeTo(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hoverFader.FadeTo(0); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); if (e.Button == MouseButtons.Left) ShowMenu(); base.OnMouseDown(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Down or Keys.Up or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Down) { ShowMenu(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    private void ShowMenu()
    {
        if (!Enabled) return;
        Opening?.Invoke(this, EventArgs.Empty);
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = false,
            Font = Theme.Font(Theme.FontSize.Body), BackColor = Theme.Surface, ForeColor = Theme.Text,
            MinimumSize = new Size(Width, 0), MaximumSize = new Size(0, (int)(420 * DeviceDpi / 96.0)),
        };
        for (var i = 0; i < Items.Count; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem(Items[i].ToString()) { ForeColor = i == selectedIndex ? Theme.Accent : Theme.Text, Padding = new Padding(6, 6, 6, 6) };
            item.Click += (_, _) => SelectedIndex = index;
            menu.Items.Add(item);
        }
        openFader.FadeTo(1);
        menu.Closed += (_, _) => { openFader.FadeTo(0); BeginInvoke(menu.Dispose); };
        menu.Show(this, new Point(0, Height + (int)(2 * DeviceDpi / 96.0)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.SurfaceElevated);
        var scale = DeviceDpi / 96f;
        using var path = RoundedPath(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.RadiusControl * scale);
        using (var fill = new SolidBrush(Motion.Lerp(Theme.SurfaceElevated, Theme.SurfaceSubtle, Math.Max(hover, openAmount)))) g.FillPath(fill, path);
        // 枠の色は、開いている間と、キーボードで操作している間だけ変える(クリックの後に残さない)
        var keyboardFocus = Focused && ShowFocusCues;
        using (var border = new Pen(Motion.Lerp(keyboardFocus ? Theme.Accent : Theme.Border, Theme.Accent, openAmount), Theme.BorderWidth * scale)) g.DrawPath(border, path);

        var chevronWidth = (int)(40 * scale);
        var textRect = new Rectangle((int)(14 * scale), 0, Width - chevronWidth - (int)(14 * scale), Height);
        TextRenderer.DrawText(g, SelectedItem?.ToString() ?? string.Empty, Theme.Font(Theme.FontSize.Body), textRect, Enabled ? Theme.Text : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
        var cx = Width - chevronWidth / 2f; var cy = Height / 2f; var arm = 4.5f * scale;
        using var chevron = new Pen(Enabled ? Theme.Muted : Theme.Border, 1.8f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var direction = Motion.Lerp(1f, -1f, openAmount); // 開くと矢印が上向きに反転する
        g.DrawLines(chevron, [new PointF(cx - arm, cy - direction * arm / 2), new PointF(cx, cy + direction * arm / 2), new PointF(cx + arm, cy - direction * arm / 2)]);
    }

    internal static GraphicsPath RoundedPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, radius * 2, radius * 2, 180, 90);
        path.AddArc(rect.Right - radius * 2, rect.Top, radius * 2, radius * 2, 270, 90);
        path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.ForeColor; base.OnRenderItemText(e); }
}

internal sealed class DarkMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Surface;
    public override Color MenuBorder => Theme.Accent;
    public override Color MenuItemBorder => Theme.SurfaceSubtle;
    public override Color MenuItemSelected => Theme.SurfaceSubtle;
    public override Color MenuItemSelectedGradientBegin => Theme.SurfaceSubtle;
    public override Color MenuItemSelectedGradientEnd => Theme.SurfaceSubtle;
    public override Color ImageMarginGradientBegin => Theme.Surface;
    public override Color ImageMarginGradientMiddle => Theme.Surface;
    public override Color ImageMarginGradientEnd => Theme.Surface;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;
}

//--------------
// DecimalInput
// 容量上限の入力欄。標準のNumericUpDownは矢印部分が明るい配色になるため、同じ見た目で自前に作る。
// 入力欄と「−」「＋」。キーボードの↑↓・ホイール・直接入力にも対応する。
//--------------
internal sealed class DecimalInput : Control
{
    private readonly TextBox editor = new() { BorderStyle = BorderStyle.None, BackColor = Theme.SurfaceSubtle, ForeColor = Theme.Text, Font = Theme.Font(Theme.FontSize.Body) };
    private decimal value;
    private int hotButton; // 0:なし 1:「−」 2:「＋」
    private readonly FloatFader minusHover;
    private readonly FloatFader plusHover;
    private float minusAmount, plusAmount;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public decimal Minimum { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public decimal Maximum { get; set; } = 100;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public decimal Increment { get; set; } = 1;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int DecimalPlaces { get; set; } = 1;
    public event EventHandler? ValueChanged;

    public DecimalInput()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Controls.Add(editor);
        editor.Leave += (_, _) => CommitText();
        editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { CommitText(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Up) { Step(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { Step(-1); e.Handled = true; }
        };
        editor.MouseWheel += (_, e) => { Step(e.Delta > 0 ? 1 : -1); if (e is HandledMouseEventArgs handled) handled.Handled = true; };
        editor.MaxLength = 8;
        editor.GotFocus += (_, _) => { Invalidate(); BeginInvoke(() => editor.SelectAll()); }; // 入力しやすいよう、フォーカスしたら全選択
        editor.LostFocus += (_, _) => Invalidate();
        AccessibleRole = AccessibleRole.SpinButton;
        minusHover = new FloatFader(v => { minusAmount = v; Invalidate(); });
        plusHover = new FloatFader(v => { plusAmount = v; Invalidate(); });
        ShowValue();
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal Value
    {
        get => value;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, DecimalPlaces, MidpointRounding.AwayFromZero), Minimum, Maximum);
            var changed = clamped != this.value;
            this.value = clamped;
            ShowValue();
            if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // 入力欄のスクリーンリーダー向けの名前
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EditorName { set => editor.AccessibleName = value; }

    private float Zoom => DeviceDpi / 96f;
    private int ButtonWidth => (int)(26 * Zoom);
    private Rectangle MinusRect => new(Width - ButtonWidth * 2 - (int)(4 * Zoom), (int)(4 * Zoom), ButtonWidth, Height - (int)(8 * Zoom));
    private Rectangle PlusRect => new(Width - ButtonWidth - (int)(4 * Zoom), (int)(4 * Zoom), ButtonWidth, Height - (int)(8 * Zoom));

    private void ShowValue() => editor.Text = value.ToString("F" + DecimalPlaces, System.Globalization.CultureInfo.InvariantCulture);

    // 入力中は何を打ってもよい。確定(Enter・フォーカスを外す・＋－)のときに数値を取り出し、小数桁数へ丸めて範囲に収める。
    // 全角の数字や「，」「．」も受け付け、数字以外は読み飛ばす。数値が読めなければ元の値へ戻す。
    private void CommitText()
    {
        var normalized = editor.Text.Normalize(System.Text.NormalizationForm.FormKC).Replace(',', '.');
        var digits = new string(normalized.Where(c => char.IsAsciiDigit(c) || c == '.').ToArray());
        if (decimal.TryParse(digits, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) Value = parsed;
        ShowValue();
    }

    private void Step(int direction) { CommitText(); Value = value + direction * Increment; }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var left = (int)(12 * Zoom);
        editor.Width = Math.Max(10, MinusRect.Left - left - (int)(4 * Zoom));
        editor.Location = new Point(left, (Height - editor.Height) / 2);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        editor.Enabled = Enabled; editor.ForeColor = Enabled ? Theme.Text : Theme.Muted;
        Invalidate(); base.OnEnabledChanged(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = MinusRect.Contains(e.Location) ? 1 : PlusRect.Contains(e.Location) ? 2 : 0;
        if (hit != hotButton) { hotButton = hit; Cursor = hit == 0 ? Cursors.Default : Cursors.Hand; minusHover.FadeTo(hit == 1 ? 1 : 0); plusHover.FadeTo(hit == 2 ? 1 : 0); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hotButton = 0; minusHover.FadeTo(0); plusHover.FadeTo(0); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left)
        {
            if (MinusRect.Contains(e.Location)) Step(-1);
            else if (PlusRect.Contains(e.Location)) Step(1);
            else editor.Focus();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e) { if (Enabled) Step(e.Delta > 0 ? 1 : -1); base.OnMouseWheel(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.SurfaceElevated);
        using var path = DropdownSelect.RoundedPath(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.RadiusControl * Zoom);
        using (var fill = new SolidBrush(Theme.SurfaceSubtle)) g.FillPath(fill, path);
        using (var border = new Pen(editor.Focused ? Theme.Accent : Theme.Border, Theme.BorderWidth * Zoom)) g.DrawPath(border, path);
        DrawStepButton(g, MinusRect, minusAmount, plus: false);
        DrawStepButton(g, PlusRect, plusAmount, plus: true);
    }

    private void DrawStepButton(Graphics g, Rectangle rect, float hot, bool plus)
    {
        if (hot > 0.01f && Enabled) using (var hover = new SolidBrush(Color.FromArgb((int)(255 * hot), Theme.Border))) g.FillRectangle(hover, rect);
        using var pen = new Pen(Enabled ? Theme.Text : Theme.Muted, 1.8f * Zoom) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var cx = rect.Left + rect.Width / 2f; var cy = rect.Top + rect.Height / 2f; var arm = 4.5f * Zoom;
        g.DrawLine(pen, cx - arm, cy, cx + arm, cy);
        if (plus) g.DrawLine(pen, cx, cy - arm, cx, cy + arm);
    }
}

//--------------
// SegmentedSelect
// 並んだ選択肢から1つ選ぶ部品(出力形式)。選んだ項目の色の面が、隣の項目へ滑って移る。
// ホバーした項目は色が移り、文字色は面が重なる量に合わせてなめらかに変わる。
//--------------
internal sealed class SegmentedSelect : Control
{
    private readonly List<string> items = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    private float[] hover = [];
    private int selectedIndex;
    private int hoverIndex = -1;
    private float pillPosition;   // 今見えている面の位置(0〜項目数-1。途中は小数)
    private float pillFrom;
    private long pillStartedAt;

    public event EventHandler? SelectedIndexChanged;

    public SegmentedSelect()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.Grouping;
        timer.Tick += (_, _) => Step();
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<string> Items => items;

    public void SetItems(params string[] texts)
    {
        items.Clear(); items.AddRange(texts);
        hover = new float[texts.Length];
        selectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, texts.Length - 1));
        pillPosition = selectedIndex;
        Invalidate();
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, items.Count - 1));
            if (clamped == selectedIndex) return;
            selectedIndex = clamped;
            if (IsHandleCreated && Visible && Motion.Enabled) { pillFrom = pillPosition; pillStartedAt = Environment.TickCount64; timer.Start(); }
            else { pillPosition = selectedIndex; Invalidate(); }
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private float Zoom => DeviceDpi / 96f;
    private float Gap => 8 * Zoom;
    private float SegmentWidth => items.Count == 0 ? 0 : (Width - 1 - Gap * (items.Count - 1)) / items.Count;
    private RectangleF SegmentRect(float index) => new(index * (SegmentWidth + Gap) + 0.5f, 0.5f, SegmentWidth, Height - 1.5f);

    private void Step()
    {
        var t = (Environment.TickCount64 - pillStartedAt) / 220f;
        pillPosition = Motion.Lerp(pillFrom, selectedIndex, Motion.EaseOut(t));
        var settled = t >= 1;
        if (settled) pillPosition = selectedIndex;
        for (var i = 0; i < hover.Length; i++)
        {
            var target = i == hoverIndex && Enabled ? 1f : 0f;
            hover[i] += (target - hover[i]) * 0.3f;
            if (Math.Abs(target - hover[i]) < 0.01f) hover[i] = target; else settled = false;
        }
        Invalidate();
        if (settled) timer.Stop();
    }

    private int IndexAt(Point point)
    {
        for (var i = 0; i < items.Count; i++) if (SegmentRect(i).Contains(point)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var index = IndexAt(e.Location);
        if (index != hoverIndex) { hoverIndex = index; if (Motion.Enabled) timer.Start(); else Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; if (Motion.Enabled) timer.Start(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left) { var index = IndexAt(e.Location); if (index >= 0) SelectedIndex = index; }
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) { SelectedIndex = selectedIndex - 1; e.Handled = true; }
        else if (e.KeyCode == Keys.Right) { SelectedIndex = selectedIndex + 1; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.SurfaceElevated);
        if (items.Count == 0) return;
        var radius = Theme.RadiusControl * Zoom;

        for (var i = 0; i < items.Count; i++)
        {
            using var path = DropdownSelect.RoundedPath(SegmentRect(i), radius);
            using var fill = new SolidBrush(Motion.Lerp(Theme.SurfaceElevated, Theme.SurfaceSubtle, hover[i]));
            using var border = new Pen(Theme.Border, Theme.BorderWidth * Zoom);
            g.FillPath(fill, path); g.DrawPath(border, path);
        }

        // 選ばれた項目を示す面。項目の間を滑って移る
        var pill = SegmentRect(pillPosition);
        using (var pillPath = DropdownSelect.RoundedPath(pill, radius))
        using (var pillFill = new SolidBrush(Enabled ? Theme.Accent : Color.FromArgb(120, Theme.Accent)))
            g.FillPath(pillFill, pillPath);

        for (var i = 0; i < items.Count; i++)
        {
            var overlap = Math.Clamp(1 - Math.Abs(pillPosition - i), 0f, 1f);
            var color = Enabled ? Motion.Lerp(Theme.Text, Theme.TextOnAccent, overlap) : Motion.Lerp(Theme.Muted, Theme.TextOnAccent, overlap);
            TextRenderer.DrawText(g, items[i], Theme.Font(Theme.FontSize.Button, FontStyle.Bold), Rectangle.Round(SegmentRect(i)), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        // 枠は、キーボードで操作しているときだけ出す
        if (Focused && ShowFocusCues)
        {
            var ring = SegmentRect(pillPosition); ring.Inflate(-1.5f * Zoom, -1.5f * Zoom);
            using var ringPath = DropdownSelect.RoundedPath(ring, radius);
            using var ringPen = new Pen(Theme.Focus, 1.5f);
            g.DrawPath(ringPen, ringPath);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SegmentedAccessibleObject(this);

    private sealed class SegmentedAccessibleObject(SegmentedSelect owner) : ControlAccessibleObject(owner)
    {
        public override int GetChildCount() => owner.items.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < owner.items.Count ? new ItemAccessibleObject(owner, this, index) : null;
    }

    private sealed class ItemAccessibleObject(SegmentedSelect owner, AccessibleObject parent, int index) : AccessibleObject
    {
        public override string? Name { get => owner.items[index]; set { } }
        public override AccessibleRole Role => AccessibleRole.RadioButton;
        public override AccessibleStates State => AccessibleStates.Focusable | AccessibleStates.Selectable | (owner.selectedIndex == index ? AccessibleStates.Selected | AccessibleStates.Checked : 0);
        public override string DefaultAction => "選択";
        public override AccessibleObject Parent => parent;
        public override Rectangle Bounds => owner.RectangleToScreen(Rectangle.Round(owner.SegmentRect(index)));
        public override void DoDefaultAction() => owner.SelectedIndex = index;
    }
}

//--------------
// HelpMark
// 項目名の右に置く「？」。0.3秒乗せると説明札が出る(札は MainForm の ToolTip が描く)。
//--------------
internal sealed class HelpMark : Control
{
    private readonly FloatFader hoverFader;
    private float hover;

    public HelpMark()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Help;
        TabStop = false;
        BackColor = Color.Transparent;
        Size = new Size(18, 18);
        AccessibleRole = AccessibleRole.PushButton;
        hoverFader = new FloatFader(v => { hover = v; Invalidate(); });
    }

    protected override void OnMouseEnter(EventArgs e) { hoverFader.FadeTo(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hoverFader.FadeTo(0); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Theme.SurfaceElevated);
        var color = Motion.Lerp(Theme.Muted, Theme.Accent, hover);
        var inset = Theme.BorderWidth;
        using var pen = new Pen(color, Theme.BorderWidth);
        e.Graphics.DrawEllipse(pen, inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
        TextRenderer.DrawText(e.Graphics, "?", Theme.Font(Theme.FontSize.Caption, FontStyle.Bold), new Rectangle(0, 0, Width, Height), color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}

//--------------
// TextWrap
// 禁則処理つきの折り返し。計測と描画で同じ結果を使うため、折り返しはここ1か所で行う。
// 行頭禁則は直前の文字ごと次の行へ送り(追い出し)、行末禁則(開きかっこ)は次の行へ送る。
//--------------
internal static class TextWrap
{
    private const string HeadProhibited = "、。，．,.）」』】〕〉》］｝)]}”’！？!?：;；・…‥％%ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶー";
    private const string TailProhibited = "（「『【〔〈《［｛([{“‘";
    private const string NumberUnits = "秒倍個回枚件度分°";
    private static readonly Regex AlnumToken = new(@"^[A-Za-z0-9][A-Za-z0-9.,%×+\-_/]*", RegexOptions.Compiled);
    private const TextFormatFlags MeasureFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.Left | TextFormatFlags.NoPrefix;

    public static int Measure(string text, Font font) =>
        text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), MeasureFlags).Width;

    public static List<string> Wrap(string text, Font font, int maxWidth)
    {
        var widths = new Dictionary<string, int>();
        int W(string unit)
        {
            if (widths.TryGetValue(unit, out var cached)) return cached;
            var width = unit == " " ? Measure("a a", font) - Measure("aa", font) : Measure(unit, font);
            return widths[unit] = width;
        }

        var units = new List<string>();
        foreach (var unit in Tokenize(text))
        {
            // 枠より長い1語は、はみ出さないよう文字ごとに分けて折り返す
            if (W(unit) > maxWidth && unit.Length > 1) units.AddRange(unit.Select(c => c.ToString()));
            else units.Add(unit);
        }

        var lines = new List<string>();
        var current = new List<string>();
        var width = 0;
        foreach (var unit in units)
        {
            var unitWidth = W(unit);
            if (current.Count > 0 && width + unitWidth > maxWidth)
            {
                var carry = new List<string>();
                if (unit == " ") { unitWidth = 0; }
                else
                {
                    // 行頭禁則の文字は、直前の文字ごと次の行へ送る(送る文字も行頭禁則なら、さらに前の文字も送る)
                    if (HeadProhibited.Contains(unit[0]))
                    {
                        do { carry.Insert(0, current[^1]); current.RemoveAt(current.Count - 1); }
                        while (current.Count > 1 && HeadProhibited.Contains(carry[0][0]));
                    }
                    while (current.Count > 1 && TailProhibited.Contains(current[^1][0]))
                    {
                        carry.Insert(0, current[^1]); current.RemoveAt(current.Count - 1);
                    }
                }
                if (current.Count > 0) lines.Add(string.Concat(current).TrimEnd());
                current = carry;
                width = carry.Sum(W);
                if (unit == " ") continue;
            }
            if (current.Count == 0 && unit == " ") continue;
            current.Add(unit); width += unitWidth;
        }
        if (current.Count > 0) lines.Add(string.Concat(current).TrimEnd());
        if (lines.Count == 0) lines.Add(string.Empty);
        return lines;
    }

    // 英数字の連なり・数字+単位・「……」「――」は分割しない1つのまとまりにする
    private static IEnumerable<string> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var rest = text[i..];
            var match = AlnumToken.Match(rest);
            if (match.Success)
            {
                var length = match.Length;
                if (i + length < text.Length && NumberUnits.Contains(text[i + length]) && char.IsDigit(text[i + length - 1])) length++;
                yield return text.Substring(i, length);
                i += length;
            }
            else if ((text[i] == '…' || text[i] == '―' || text[i] == '‥') && i + 1 < text.Length && text[i + 1] == text[i])
            {
                yield return text.Substring(i, 2);
                i += 2;
            }
            else
            {
                yield return text[i].ToString();
                i++;
            }
        }
    }
}

//--------------
// HintPopup
// 「操作：内容」の行は操作を太字・アクセント色にして内容の列をそろえる。普通の文は折り返して表示する。
//--------------
internal static class HintPopup
{
    private const int LogicalMaxWidth = 320;

    private sealed record Row(string Operation, List<string> Lines);
    private sealed record Layout(List<Row> Rows, int OperationWidth, int ContentX, Size Size, int LineHeight, int Padding, int RowGap);

    public static Size Measure(Control owner, string text) => Build(owner, text).Size;

    public static void Draw(Graphics graphics, Rectangle bounds, Control owner, string text)
    {
        var layout = Build(owner, text);
        using (var background = new SolidBrush(Theme.Surface)) graphics.FillRectangle(background, bounds);
        using (var border = new Pen(Theme.Accent, Theme.BorderWidth)) graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix;
        var y = bounds.Y + layout.Padding;
        foreach (var row in layout.Rows)
        {
            if (row.Operation.Length > 0)
                TextRenderer.DrawText(graphics, row.Operation, Theme.Font(Theme.FontSize.Small, FontStyle.Bold), new Point(bounds.X + layout.Padding, y), Theme.Accent, flags);
            var x = bounds.X + (row.Operation.Length > 0 ? layout.ContentX : layout.Padding);
            var lineY = y;
            foreach (var line in row.Lines)
            {
                TextRenderer.DrawText(graphics, line, Theme.Font(Theme.FontSize.Small), new Point(x, lineY), Theme.Text, flags);
                lineY += layout.LineHeight;
            }
            y += row.Lines.Count * layout.LineHeight + layout.RowGap;
        }
    }

    private static Layout Build(Control owner, string text)
    {
        var scale = owner.DeviceDpi / 96.0;
        var padding = (int)Math.Round(10 * scale);
        var gap = (int)Math.Round(10 * scale);
        var rowGap = (int)Math.Round(4 * scale);
        var innerWidth = (int)Math.Round(LogicalMaxWidth * scale) - padding * 2;
        var font = Theme.Font(Theme.FontSize.Small);
        var bold = Theme.Font(Theme.FontSize.Small, FontStyle.Bold);
        var lineHeight = font.Height + (int)Math.Round(2 * scale);

        var parsed = text.Split('\n').Select(line =>
        {
            var index = line.IndexOf('：');
            return index > 0 ? (Operation: line[..index], Body: line[(index + 1)..]) : (Operation: string.Empty, Body: line);
        }).ToList();
        var operationWidth = parsed.Where(p => p.Operation.Length > 0).Select(p => TextWrap.Measure(p.Operation, bold)).DefaultIfEmpty(0).Max();
        var contentX = operationWidth > 0 ? padding + operationWidth + gap : padding;

        var rows = new List<Row>();
        var widest = 0;
        foreach (var (operation, body) in parsed)
        {
            var available = operation.Length > 0 ? innerWidth + padding - contentX : innerWidth;
            var lines = TextWrap.Wrap(body, font, available);
            rows.Add(new Row(operation, lines));
            var start = operation.Length > 0 ? contentX : padding;
            widest = Math.Max(widest, start + lines.Max(l => TextWrap.Measure(l, font)));
        }
        var height = padding * 2 + rows.Sum(r => r.Lines.Count * lineHeight) + rowGap * (rows.Count - 1);
        return new Layout(rows, operationWidth, contentX, new Size(widest + padding, height), lineHeight, padding, rowGap);
    }
}
