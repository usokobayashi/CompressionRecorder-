//==================================================
// AppSettings
// 保存先・容量上限・容量制限の有無を %LOCALAPPDATA%\uskCompressionRecorder\settings.json へ保存する。
//==================================================

using System.IO;
using System.Text.Json;

namespace uskCompressionRecorder;

internal sealed class AppSettings
{
    public const decimal MinSizeMb = 2;
    public const decimal MaxSizeMb = 500;
    public const decimal DefaultSizeMb = 10;

    public string? SaveDirectory { get; set; }
    public decimal SizeLimitMb { get; set; } = DefaultSizeMb;
    public bool SizeLimitEnabled { get; set; } = true;

    private static string Directory_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "uskCompressionRecorder");
    private static string FilePath => Path.Combine(Directory_, "settings.json");
    // 旧名の設定。新しい保存先に設定がないときだけ、新しい旧名から順に探して引き継ぐ
    private static readonly string[] LegacyDirectories = ["CompressionRecorder", "DiscordClipRecorder"];
    private static IEnumerable<string> LegacyPaths(string name) =>
        LegacyDirectories.Select(directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), directory, name));

    //--------------
    // Load
    //--------------
    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            var source = File.Exists(FilePath) ? FilePath : LegacyPaths("settings.json").FirstOrDefault(File.Exists);
            if (source is not null)
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(source)) ?? settings;
            }
            else
            {
                settings.SaveDirectory = ReadLegacyText("save-directory.txt");
                if (decimal.TryParse(ReadLegacyText("max-size-mb.txt"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var legacySize))
                    settings.SizeLimitMb = legacySize;
            }
        }
        catch { }
        settings.SizeLimitMb = Math.Clamp(Math.Round(settings.SizeLimitMb, MidpointRounding.AwayFromZero), MinSizeMb, MaxSizeMb); // 容量上限は整数MB
        if (string.IsNullOrWhiteSpace(settings.SaveDirectory))
            settings.SaveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "uskCompressionRecorder");
        return settings;
    }

    //--------------
    // Save
    //--------------
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Directory_);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // 旧版(1.6.0以前)は設定ごとに別のテキストファイルへ保存していたため、初回だけ読み込む
    private static string? ReadLegacyText(string name)
    {
        try
        {
            var path = LegacyPaths(name).Prepend(Path.Combine(Directory_, name)).FirstOrDefault(File.Exists);
            return path is null ? null : File.ReadAllText(path).Trim();
        }
        catch { return null; }
    }
}
