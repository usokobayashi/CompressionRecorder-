//==================================================
// Theme
// 色・角丸・線の太さ・文字の共通定義。画面ごとに別の値を作らず、ここから取る。
//==================================================

using System.Collections.Concurrent;

namespace CompressionRecorder;

internal static class Theme
{
    // 色
    public static readonly Color Surface = Color.FromArgb(27, 30, 36);
    public static readonly Color SurfaceElevated = Color.FromArgb(36, 40, 50);
    public static readonly Color SurfaceSubtle = Color.FromArgb(44, 49, 61);
    public static readonly Color Border = Color.FromArgb(51, 56, 68);
    public static readonly Color BorderStrong = Color.FromArgb(74, 80, 92);
    public static readonly Color Muted = Color.FromArgb(154, 161, 174);
    public static readonly Color Text = Color.FromArgb(242, 243, 245);
    public static readonly Color TextOnAccent = Color.FromArgb(12, 44, 71);
    public static readonly Color Accent = Color.FromArgb(95, 176, 240);
    public static readonly Color AccentHover = Color.FromArgb(126, 195, 245);
    public static readonly Color Success = Color.FromArgb(87, 183, 122);
    public static readonly Color Danger = Color.FromArgb(231, 107, 102);
    public static readonly Color Focus = Color.FromArgb(143, 199, 255);

    // 角丸
    public const int RadiusCard = 12;
    public const int RadiusControl = 8;
    public const int RadiusSmall = 6;
    public const int RadiusTag = 4;

    // 線の太さ・部品の寸法
    public const float BorderWidth = 1f;
    public const float AccentBorderWidth = 2f;
    public const float TrackThickness = 4f;
    public const int SliderHandleRadius = 9;
    public const int SeekHandleRadius = 7;
    public const int CropHandleSize = 10;

    // 文字
    public const string FontFamily = "Yu Gothic UI";
    public const string MonoFontFamily = "Consolas";

    public static class FontSize
    {
        public const float Title = 18f;
        public const float Hero = 15f;
        public const float Heading = 11f;
        public const float Body = 10f;
        public const float Button = 9.5f;
        public const float Small = 9f;
        public const float Caption = 8.5f;
    }

    private static readonly ConcurrentDictionary<(string, float, FontStyle), Font> Fonts = new();

    public static Font Font(float size, FontStyle style = FontStyle.Regular) =>
        Fonts.GetOrAdd((FontFamily, size, style), key => new Font(key.Item1, key.Item2, key.Item3));

    public static Font MonoFont(float size, FontStyle style = FontStyle.Regular) =>
        Fonts.GetOrAdd((MonoFontFamily, size, style), key => new Font(key.Item1, key.Item2, key.Item3));

    // 録画プレビュー(WPF)で同じ色を使うための変換
    public static System.Windows.Media.Color ToWpf(Color color, byte alpha = 255) =>
        System.Windows.Media.Color.FromArgb(alpha, color.R, color.G, color.B);
}
