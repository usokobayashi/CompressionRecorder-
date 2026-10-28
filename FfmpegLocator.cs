//==================================================
// FfmpegLocator
// exe単体で動くよう、FFmpegをexeの中(圧縮した埋め込みリソース)に同梱している。
// 初回の起動で %LOCALAPPDATA%\uskCompressionRecorder\ffmpeg\ へ展開し、2回目以降はそれを使う。
// exeの隣の tools\ffmpeg.exe があればそちらを優先する(開発用)。
//==================================================

using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace uskCompressionRecorder;

internal static class FfmpegLocator
{
    private const string ResourceName = "ffmpeg.exe.gz";
    private static readonly Lazy<string> Resolved = new(Resolve);

    public static string Path => Resolved.Value;

    private static string Resolve()
    {
        try
        {
            var besideExecutable = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "tools", "ffmpeg.exe");
            if (File.Exists(besideExecutable)) return besideExecutable;

            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (resource is null) return "ffmpeg.exe"; // 同梱がないビルドはPATH上のffmpegを使う

            // 展開先はリソースの大きさで分け、FFmpegを入れ替えた版が古いものと混ざらないようにする
            var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "uskCompressionRecorder", "ffmpeg", resource.Length.ToString());
            var target = System.IO.Path.Combine(directory, "ffmpeg.exe");
            if (File.Exists(target)) return target;

            Directory.CreateDirectory(directory);
            var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var gzip = new GZipStream(resource, CompressionMode.Decompress))
            using (var output = File.Create(temporary))
                gzip.CopyTo(output);
            try { File.Move(temporary, target, true); }
            catch (IOException) when (File.Exists(target)) { File.Delete(temporary); } // 別のプロセスが先に展開した
            return target;
        }
        catch
        {
            return "ffmpeg.exe";
        }
    }
}
