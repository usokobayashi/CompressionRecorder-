//==================================================
// ClipEncoder
// 録画クリップをMP4・GIF・アニメーションWebPへ書き出す。容量上限があるときは、
// 長さと画面の大きさから目標を決め、細かく圧縮して上限ぎりぎりへ合わせる。
//==================================================

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace uskCompressionRecorder;

internal enum ClipFormat { Mp4, Gif, Webp }

internal sealed class ClipRequest
{
    public required string Input { get; init; }
    public required string Output { get; init; }
    public required double Start { get; init; }
    public required double Duration { get; init; }
    public required ClipFormat Format { get; init; }
    public bool IncludeAudio { get; init; }
    public long? ByteLimit { get; init; } // null は容量制限なし
    public double CropX { get; init; }
    public double CropY { get; init; }
    public double CropWidth { get; init; } = 1;
    public double CropHeight { get; init; } = 1;
}

internal sealed record ClipResult(long Bytes, int Width, int Height, double Fps, int Attempts);

internal sealed class ClipEncoder
{
    public const int MaxDimension = 4096;

    private const double TargetFill = 0.985;      // 目標は上限のこの割合
    private const double GifTargetFill = 0.975;
    private const double AcceptFill = 0.945;      // この割合以上なら「ぎりぎり」とみなして終える
    private const int FullFps = 30;
    private const int ReducedFps = 24;              // 画質を下げても収まらないときの次の手段(30→24fps)
    private const double Mp4ReferenceCrf = 20;      // 基準の画質
    private const double Mp4HalfQualityCrf = 26;    // 基準の容量の約半分になる画質値。段階1・2で下げてよい限度
    private const double Mp4MinBitsPerPixel = 0.05; // 段階3で画面の大きさを決めるときの、1画素あたりの最低ビット数
    private const double GifBytesPerPixel = 0.22;   // 初回の見積り。実測で補正する
    private const double GifMinPixels = 230400;     // 640×360相当
    private const double WebpBytesPerPixel = 0.06;  // 初回の見積り(品質75)。実測で補正する
    private const double WebpMinPixels = 300000;
    private static readonly int[] GifTiers = [25, 20, 15, 12, 10, 8];
    private const double WebpReferenceQuality = 85; // 基準の画質
    private const double WebpHalfQuality = 76;      // 基準の容量の約半分になる品質。段階1・2で下げてよい限度
    private const double WebpAnchorQuality = 62;    // 品質を大きく下げるときの目安(大きさを決め直すときの基準)
    private const double WebpDefaultSlope = 0.08;   // 品質が1上がると容量が約8%増える(高品質ほど急になる)
    private const double WebpMaxQuality = 90;  // これを超えると容量が不連続に跳ねるため、足りない分は画面の大きさで調整する
    private const double WebpMinQuality = 45;
    private const int AnimatedUnlimitedLongEdge = 1920; // 容量制限なしのGIF・WebPは処理が重いため長辺をここまでにする
    private const int AnimatedLimitedLongEdge = 2560;   // 容量制限ありのGIF・WebPが探す大きさの上限(4Kまで広げても時間がかかるだけで見た目の差が小さい)
    private const string TooLargeMessage = "指定容量に収まりませんでした。容量上限を上げるか、切り取り範囲を短くしてください。";

    private readonly string ffmpegPath;
    private readonly Action<string> report;
    private readonly List<string> temporaryFiles = [];
    private int attempts;
    private CancellationToken encodeCancellation; // 書き出し全体の取り消し。実行中のffmpegを止める

    public ClipEncoder(string ffmpegPath, Action<string> report)
    {
        this.ffmpegPath = ffmpegPath;
        this.report = report;
    }

    //--------------
    // EncodeAsync
    //--------------
    public async Task<ClipResult> EncodeAsync(ClipRequest request, CancellationToken cancellation = default)
    {
        attempts = 0;
        encodeCancellation = cancellation;
        try
        {
            var (sourceWidth, sourceHeight, sourceDuration) = await ProbeAsync(request.Input);
            // 切り取った範囲の元の録画の容量の見積り(録画は容量の大きい低圧縮のため、これが上限以下なら書き出しは必ず収まる)
            var clipBytes = sourceDuration > 0 ? (long)(new FileInfo(request.Input).Length * Math.Min(1.0, request.Duration / sourceDuration)) : long.MaxValue;
            var plan = FramePlan.Create(request, sourceWidth, sourceHeight, clipBytes);
            return request.Format switch
            {
                ClipFormat.Mp4 => await EncodeMp4Async(request, plan),
                ClipFormat.Gif => await EncodeGifAsync(request, plan),
                _ => await EncodeWebpAsync(request, plan),
            };
        }
        catch (OperationCanceledException)
        {
            TryDelete(request.Output); // 途中までの出力は残さない
            throw;
        }
        finally
        {
            foreach (var file in temporaryFiles) TryDelete(file);
            temporaryFiles.Clear();
        }
    }

    //--------------
    // ProbeSizeAsync
    //--------------
    public async Task<(int Width, int Height, double Duration)> ProbeAsync(string input)
    {
        var text = await RunAsync($"-hide_banner -i \"{input}\"", allowFailure: true);
        var match = Regex.Match(text, @"Video:.*?,\s*(\d{2,5})x(\d{2,5})");
        if (!match.Success) throw new InvalidOperationException("動画の大きさを取得できませんでした。");
        var time = Regex.Match(text, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        var duration = time.Success
            ? int.Parse(time.Groups[1].Value) * 3600 + int.Parse(time.Groups[2].Value) * 60 + double.Parse(time.Groups[3].Value, CultureInfo.InvariantCulture)
            : 0;
        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), duration);
    }

    //--------------
    // MP4
    //--------------
    private async Task<ClipResult> EncodeMp4Async(ClipRequest r, FramePlan plan)
    {
        var audioArgs = r.IncludeAudio ? "-map 0:a? -c:a aac -b:a 80k" : "-an";
        var audioKbps = r.IncludeAudio ? 80 : 0;
        var seek = Inv($"-ss {r.Start:F3} -t {r.Duration:F3} -i \"{r.Input}\"");
        var common = "-hide_banner -loglevel error";

        if (r.ByteLimit is not long limit)
        {
            report($"元の画質で書き出し中… {plan.CapWidth}×{plan.CapHeight}");
            await RunAsync($"{common} {seek} -map 0:v:0 {audioArgs} -vf \"{VideoFilter(plan, plan.CapWidth, FullFps)}\" -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart -y \"{r.Output}\"");
            return Finish(r.Output, plan.CapWidth, plan.CapHeight, FullFps);
        }

        var target = limit * TargetFill;
        var budgetKbps = Math.Max(30, target * 8 / 1000 / r.Duration - audioKbps);
        var best = new BestFit();

        // 圧縮の優先順位(上から順に試し、収まった時点で終える)
        //   1. 画質: 元の大きさ・30fpsのまま、画質値だけを基準(CRF20)の容量の約半分(CRF26)まで下げる
        //   2. コマ数: 24fpsに落とし、同じ範囲で画質値を調整する
        //   3. それでも収まらないときだけ、さらに画質と画面の大きさを下げる
        // 予算に余裕があるときは、基準の画質で収まるかを先に試して、収まれば終える。
        CrfProbe? seed = null;
        var capBitsPerPixel = budgetKbps * 1000 / (FullFps * (double)plan.CapWidth * plan.CapHeight);
        if (capBitsPerPixel >= 0.09 || plan.SourceClipBytes <= limit)
        {
            report($"元の画質で試しています… {plan.CapWidth}×{plan.CapHeight}");
            var trial = NewTemp("mp4");
            await RunAsync($"{common} {seek} -map 0:v:0 {audioArgs} -vf \"{VideoFilter(plan, plan.CapWidth, FullFps)}\" -c:v libx264 -preset veryfast -crf {Mp4ReferenceCrf} -pix_fmt yuv420p -movflags +faststart -y \"{trial}\"");
            attempts++;
            var trialSize = new FileInfo(trial).Length;
            if (trialSize <= limit) { Move(trial, r.Output); return Finish(r.Output, plan.CapWidth, plan.CapHeight, FullFps); }
            TryDelete(trial);
            seed = new CrfProbe(Mp4ReferenceCrf, trialSize);
        }
        foreach (var stageFps in new[] { FullFps, ReducedFps })
        {
            var stageSeed = seed is null ? null : new CrfProbe(seed.Crf, (long)(seed.Size * stageFps / (double)FullFps));
            var probe = await CrfSearchAsync(plan, seek, audioArgs, stageFps, plan.CapWidth, plan.CapHeight, limit, target, best,
                startCrf: Mp4HalfQualityCrf, minCrf: Mp4ReferenceCrf, maxCrf: Mp4HalfQualityCrf, stageSeed);
            if (best.Path is not null) return Finish(best, r.Output);
            if (probe is not null) seed = new CrfProbe(probe.Crf, (long)(probe.Size * FullFps / (double)stageFps));
        }

        // 長さから決めたビットレートで画面の大きさを選び、2パスで上限へ近づける
        var fps = ReducedFps;
        var scale = Math.Clamp(Math.Sqrt(budgetKbps * 1000 / (fps * Mp4MinBitsPerPixel) / ((double)plan.CapWidth * plan.CapHeight)), 0.1, 1.0);
        var passLog = NewTemp("passlog");
        for (var level = 0; level < 10; level++)
        {
            var width = Math.Min(plan.CapWidth, Math.Max(64, EvenFloor(plan.CapWidth * scale)));
            var height = plan.HeightFor(width);
            var filter = VideoFilter(plan, width, fps);
            var kbps = budgetKbps;
            var analyzedKbps = 0.0;
            for (var round = 0; round < 5; round++)
            {
                // 解析(1パス目)の結果は、ビットレートが大きく変わると当てにならないため、2割5分以上動いたら取り直す
                if (analyzedKbps == 0 || Math.Abs(kbps / analyzedKbps - 1) > 0.25)
                {
                    report($"解析中… {width}×{height}");
                    await RunAsync($"{common} {seek} -map 0:v:0 -an -vf \"{filter}\" -c:v libx264 -preset veryfast -b:v {kbps:F0}k -pix_fmt yuv420p -pass 1 -passlogfile \"{passLog}\" -f null NUL");
                    analyzedKbps = kbps;
                }
                var candidate = NewTemp("mp4");
                report($"容量に合わせて圧縮中… {width}×{height} / {kbps:F0}kbps ({round + 1}回目)");
                await RunAsync($"{common} {seek} -map 0:v:0 {audioArgs} -vf \"{filter}\" -c:v libx264 -preset veryfast -b:v {kbps:F0}k -pix_fmt yuv420p -pass 2 -passlogfile \"{passLog}\" -movflags +faststart -y \"{candidate}\"");
                attempts++;
                var size = new FileInfo(candidate).Length;
                ReportSize(width, height, size, limit);
                best.Offer(candidate, size, limit, width, height, fps);
                if (size <= limit && size >= limit * AcceptFill) return Finish(best, r.Output);
                kbps *= Math.Clamp(target / size, 0.6, 1.6);
                if (size > limit && kbps * 1000 / (fps * (double)width * height) < 0.015) break;
            }
            // 同じ大きさでも容量が大きく外れる映像(細かいノイズが多い)では、ビットレート指定が当てにならない。品質値(CRF)で探し直す
            // 2パスの結果が上限よりかなり小さいとき(静止に近い画面など)は、元の大きさへ戻して品質値で詰める。
            // 上限を超えて収まらないときは、同じ大きさのまま品質値で探し直す。
            var underfilled = best.Path is not null && best.Size < limit * 0.8;
            if (best.Path is null || (underfilled && width >= plan.CapWidth))
                await CrfSearchAsync(plan, seek, audioArgs, fps, width, height, limit, target, best, startCrf: 26, minCrf: 12, maxCrf: 44);
            else if (underfilled)
                await CrfSearchAsync(plan, seek, audioArgs, fps, plan.CapWidth, plan.HeightFor(plan.CapWidth), limit, target, best, startCrf: 26, minCrf: 12, maxCrf: 32);
            if (best.Path is not null) return Finish(best, r.Output);
            scale *= 0.88;
        }
        throw new InvalidOperationException(TooLargeMessage);
    }

    // 品質値(CRF)と容量の関係を実測し、上限に収まる最小のCRFを挟み込みながら探す
    // seed があるときは、その1点から最初のCRFを見積もる。見積りが範囲を大きく超えるときは何も試さず null を返す。
    // 戻り値は最後に試した条件(次の段階の見積りに使う)。
    private async Task<CrfProbe?> CrfSearchAsync(FramePlan plan, string seek, string audioArgs, int fps, int width, int height, long limit, double target, BestFit best,
        double startCrf, double minCrf, double maxCrf, CrfProbe? seed = null)
    {
        var filter = VideoFilter(plan, width, fps);
        double? tooBigCrf = null;   // 上限を超えた最大のCRF
        double? fittingCrf = null;  // 上限に収まった最小のCRF
        double? previousCrf = null; long previousSize = 0;
        CrfProbe? last = null;
        var crf = startCrf;
        if (seed is not null)
        {
            var guess = seed.Crf + Math.Log(seed.Size / target) / 0.115;
            if (guess > maxCrf + 3) return null;
            crf = Math.Round(Math.Clamp(guess, minCrf, maxCrf), 1);
        }
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var candidate = NewTemp("mp4");
            report($"品質値で調整中… {width}×{height} / CRF{crf:F1} ({iteration + 1}回目)");
            await RunAsync(Inv($"-hide_banner -loglevel error {seek} -map 0:v:0 {audioArgs} -vf \"{filter}\" -c:v libx264 -preset veryfast -crf {crf:F1} -pix_fmt yuv420p -movflags +faststart -y \"{candidate}\""));
            attempts++;
            var size = new FileInfo(candidate).Length;
            ReportSize(width, height, size, limit);
            best.Offer(candidate, size, limit, width, height, fps);
            last = new CrfProbe(crf, size);
            if (size <= limit)
            {
                fittingCrf = fittingCrf is double f ? Math.Min(f, crf) : crf;
                if (size >= limit * AcceptFill) return last;
            }
            else tooBigCrf = tooBigCrf is double t ? Math.Max(t, crf) : crf;

            var slope = 0.115; // CRFが6上がると容量はおよそ半分
            if (previousCrf is double pc && Math.Abs(crf - pc) >= 0.05 && previousSize > 0)
                slope = Math.Clamp(Math.Log((double)previousSize / size) / (crf - pc), 0.03, 0.3);
            var next = crf + Math.Log(size / target) / slope;
            if (tooBigCrf is double over && fittingCrf is double fit)
            {
                if (fit - over < 0.15) return last;
                next = Math.Clamp(next, over + (fit - over) * 0.15, fit - (fit - over) * 0.15);
            }
            next = Math.Round(Math.Clamp(next, minCrf, maxCrf), 1);
            if (Math.Abs(next - crf) < 0.05) return last;
            previousCrf = crf; previousSize = size; crf = next;
        }
        return last;
    }

    private sealed record CrfProbe(double Crf, long Size);

    //--------------
    // GIF
    //--------------
    private async Task<ClipResult> EncodeGifAsync(ClipRequest r, FramePlan plan)
    {
        if (r.ByteLimit is not long limit)
        {
            var width = AnimatedWidth(plan, AnimatedUnlimitedLongEdge);
            report($"GIFで書き出し中… {width}×{plan.HeightFor(width)}");
            await GifOnceAsync(r, plan, 20, width, r.Output);
            return Finish(r.Output, width, plan.HeightFor(width), 20);
        }

        var target = limit * GifTargetFill;
        int[] tiers = GifTiers;
        var capWidth = AnimatedWidth(plan, AnimatedLimitedLongEdge);
        var capPixels = (double)capWidth * plan.HeightFor(capWidth);

        // 長いクリップほどコマ数を減らし、画面の大きさが小さくなりすぎないようにする
        var firstTier = FirstTier(tiers, target, r.Duration, GifBytesPerPixel, GifMinPixels);

        var best = new BestFit();
        for (var tier = firstTier; tier < tiers.Length; tier++)
        {
            var fps = tiers[tier];
            var scale = InitialScale(target, r.Duration, fps, GifBytesPerPixel, capPixels);
            double? previousScale = null; long previousSize = 0;
            for (var iteration = 0; iteration < 7; iteration++)
            {
                var width = Math.Min(capWidth, Math.Max(48, EvenFloor(capWidth * scale)));
                var height = plan.HeightFor(width);
                var candidate = NewTemp("gif");
                report($"容量に合わせてGIFを調整中… {width}×{height} / {fps}fps ({iteration + 1}回目)");
                var size = await GifOnceAsync(r, plan, fps, width, candidate);
                ReportSize(width, height, size, limit);
                best.Offer(candidate, size, limit, width, height, fps);
                if (size <= limit && size >= limit * AcceptFill) return Finish(best, r.Output);
                if (size <= limit && width >= capWidth) break;

                // 画素数と容量の関係(指数)を2点の実測から求め、次の大きさを決める
                var gamma = 1.7;
                if (previousScale is double ps && Math.Abs(Math.Log(scale / ps)) > 1e-6 && previousSize > 0)
                    gamma = Math.Clamp(Math.Log((double)size / previousSize) / Math.Log(scale / ps), 1.0, 2.5);
                var next = Math.Clamp(scale * Math.Pow(target / size, 1 / gamma), 0.08, 1.0);
                if (Math.Abs(next - scale) < 0.004)
                {
                    if (size > limit) next = scale * 0.97; else break;
                }
                previousScale = scale; previousSize = size; scale = next;
            }
            if (best.Path is not null) return Finish(best, r.Output);
        }
        throw new InvalidOperationException(TooLargeMessage);
    }

    private async Task<long> GifOnceAsync(ClipRequest r, FramePlan plan, int fps, int width, string output)
    {
        var palette = NewTemp("png");
        var chain = VideoFilter(plan, width, fps);
        var seek = Inv($"-ss {r.Start:F3} -t {r.Duration:F3} -i \"{r.Input}\"");
        await RunAsync($"-hide_banner -loglevel error {seek} -vf \"{chain},palettegen=stats_mode=diff\" -y \"{palette}\"");
        await RunAsync($"-hide_banner -loglevel error {seek} -i \"{palette}\" -filter_complex \"[0:v]{chain}[x];[x][1:v]paletteuse=dither=sierra2_4a\" -y \"{output}\"");
        TryDelete(palette);
        attempts++;
        return new FileInfo(output).Length;
    }

    //--------------
    // アニメーションWebP(libwebp_anim)
    //--------------
    private async Task<ClipResult> EncodeWebpAsync(ClipRequest r, FramePlan plan)
    {
        if (r.ByteLimit is not long limit)
        {
            var width = AnimatedWidth(plan, AnimatedUnlimitedLongEdge);
            report($"アニメーションWebPで書き出し中… {width}×{plan.HeightFor(width)}");
            await WebpOnceAsync(r, plan, 30, width, 85, r.Output);
            return Finish(r.Output, width, plan.HeightFor(width), 30);
        }

        var target = limit * TargetFill;
        var capWidth = AnimatedWidth(plan, AnimatedLimitedLongEdge);
        var capHeight = plan.HeightFor(capWidth);
        var capPixels = (double)capWidth * capHeight;
        var best = new BestFit();

        // 圧縮の優先順位: 1. 大きさと30fpsのまま品質だけを下げる(基準品質の容量の約半分まで)
        //                 2. 24fpsに落として同じ範囲で品質を調整する  3. それでも収まらないときだけ、さらに品質と大きさを下げる
        double? seedQuality = null; long seedSize = 0; // 30fps換算
        foreach (var stageFps in new[] { FullFps, ReducedFps })
        {
            var startQuality = WebpReferenceQuality;
            if (seedQuality is double sq)
            {
                var guess = sq + Math.Log(target / (seedSize * stageFps / (double)FullFps)) / WebpDefaultSlope;
                if (guess < WebpHalfQuality - 3) continue; // 見積りでは範囲に収まらない
                startQuality = Math.Round(Math.Clamp(guess, WebpHalfQuality, WebpMaxQuality) * 2) / 2;
            }
            var probe = await WebpQualitySearchAsync(r, plan, stageFps, capWidth, capHeight, limit, target, best, startQuality, WebpHalfQuality);
            if (best.Path is not null) return Finish(best, r.Output);
            if (probe is not null) { seedQuality = probe.Quality; seedSize = (long)(probe.Size * FullFps / (double)stageFps); }
        }

        int[] tiers = [ReducedFps, 20, 15, 12, 10];
        var firstTier = FirstTier(tiers, target, r.Duration, WebpBytesPerPixel, WebpMinPixels);
        for (var tier = firstTier; tier < tiers.Length; tier++)
        {
            var fps = tiers[tier];
            var scale = InitialScale(target, r.Duration, fps, WebpBytesPerPixel, capPixels);
            double quality = 75;
            double? previousQuality = null; long previousSize = 0;
            var tried = new HashSet<(int Width, double Quality)>();
            for (var iteration = 0; iteration < 10; iteration++)
            {
                var width = Math.Min(capWidth, Math.Max(48, EvenFloor(capWidth * scale)));
                var height = plan.HeightFor(width);
                if (!tried.Add((width, quality))) break; // 同じ条件を2度試す往復になったら、ここまでの最良を使う
                var candidate = NewTemp("webp");
                report($"容量に合わせてWebPを調整中… {width}×{height} / {fps}fps / 品質{quality:F1} ({iteration + 1}回目)");
                var size = await WebpOnceAsync(r, plan, fps, width, quality, candidate);
                ReportSize(width, height, size, limit);
                best.Offer(candidate, size, limit, width, height, fps);
                if (size <= limit && size >= limit * AcceptFill) return Finish(best, r.Output);

                // 品質と容量の関係(指数)を実測から求め、次の品質を決める
                var slope = 0.08; // 品質が1上がると容量が約8%増える(高品質ほど急になる)
                if (previousQuality is double pq && Math.Abs(quality - pq) >= 0.5 && previousSize > 0)
                    slope = Math.Clamp(Math.Log((double)size / previousSize) / (quality - pq), 0.02, 0.3);
                var nextQuality = quality + Math.Log(target / size) / slope;
                if (nextQuality > WebpMaxQuality || nextQuality < WebpMinQuality)
                {
                    // 品質では吸収できない。画面の大きさを、容量が大きさのおよそ1.8乗に比例する関係から一度に決め直す
                    var anchorQuality = nextQuality > WebpMaxQuality ? 84.0 : WebpAnchorQuality;
                    var sizeAtAnchor = size * Math.Exp(slope * (anchorQuality - quality));
                    var newScale = Math.Clamp(scale * Math.Pow(target / sizeAtAnchor, 1 / 1.8), 0.08, 1.0);
                    if (Math.Abs(newScale - scale) < 0.01 && nextQuality > WebpMaxQuality)
                    {
                        if (size <= limit) break; // これ以上大きくできない
                        newScale = scale * 0.95;
                    }
                    scale = newScale; quality = anchorQuality; previousQuality = null; previousSize = 0;
                    continue;
                }
                var rounded = Math.Round(nextQuality * 2) / 2;
                if (rounded == quality)
                {
                    if (size > limit) rounded = quality - 0.5; else break;
                }
                previousQuality = quality; previousSize = size; quality = rounded;
            }
            if (best.Path is not null) return Finish(best, r.Output);
        }
        throw new InvalidOperationException(TooLargeMessage);
    }

    // 大きさとコマ数を固定し、品質だけを minQuality 〜 WebpMaxQuality の範囲で動かして上限へ合わせる
    private async Task<WebpProbe?> WebpQualitySearchAsync(ClipRequest r, FramePlan plan, int fps, int width, int height, long limit, double target, BestFit best, double startQuality, double minQuality)
    {
        var quality = startQuality;
        double? previousQuality = null; long previousSize = 0;
        WebpProbe? last = null;
        var tried = new HashSet<double>();
        for (var iteration = 0; iteration < 6; iteration++)
        {
            if (!tried.Add(quality)) break;
            var candidate = NewTemp("webp");
            report($"品質を調整中… {width}×{height} / {fps}fps / 品質{quality:F1} ({iteration + 1}回目)");
            var size = await WebpOnceAsync(r, plan, fps, width, quality, candidate);
            ReportSize(width, height, size, limit);
            best.Offer(candidate, size, limit, width, height, fps);
            last = new WebpProbe(quality, size);
            if (size <= limit && size >= limit * AcceptFill) return last;

            var slope = WebpDefaultSlope;
            if (previousQuality is double pq && Math.Abs(quality - pq) >= 0.5 && previousSize > 0)
                slope = Math.Clamp(Math.Log((double)size / previousSize) / (quality - pq), 0.02, 0.3);
            var next = quality + Math.Log(target / size) / slope;
            if (next < minQuality - 2) return last;                         // この範囲では収まらない
            if (next > WebpMaxQuality && quality >= WebpMaxQuality) return last; // これ以上画質を上げられない
            var rounded = Math.Round(Math.Clamp(next, minQuality, WebpMaxQuality) * 2) / 2;
            if (rounded == quality)
            {
                if (size > limit && quality > minQuality) rounded = quality - 0.5; else return last;
            }
            previousQuality = quality; previousSize = size; quality = rounded;
        }
        return last;
    }

    private sealed record WebpProbe(double Quality, long Size);

    private async Task<long> WebpOnceAsync(ClipRequest r, FramePlan plan, int fps, int width, double quality, string output)
    {
        var seek = Inv($"-ss {r.Start:F3} -t {r.Duration:F3} -i \"{r.Input}\"");
        await RunAsync(Inv($"-hide_banner -loglevel error {seek} -an -vf \"{VideoFilter(plan, width, fps)}\" -c:v libwebp_anim -lossless 0 -quality {quality:F1} -compression_level 3 -loop 0 -y \"{output}\""));
        attempts++;
        return new FileInfo(output).Length;
    }

    //--------------
    // 共通部品
    //--------------
    private sealed record FramePlan(string CropFilter, int CroppedWidth, int CroppedHeight, int CapWidth, int CapHeight, long SourceClipBytes)
    {
        public int HeightFor(int width) => EvenFloor(CroppedHeight * (double)width / CroppedWidth);

        public static FramePlan Create(ClipRequest r, int sourceWidth, int sourceHeight, long sourceClipBytes)
        {
            var hasCrop = r.CropWidth < 0.999 || r.CropHeight < 0.999;
            var w = Math.Round(Math.Clamp(r.CropWidth, 0.05, 1.0), 4);
            var h = Math.Round(Math.Clamp(r.CropHeight, 0.05, 1.0), 4);
            var x = Math.Round(Math.Clamp(r.CropX, 0.0, 1.0 - w), 4);
            var y = Math.Round(Math.Clamp(r.CropY, 0.0, 1.0 - h), 4);
            var filter = hasCrop ? Inv($"crop=trunc(iw*{w:F4}/2)*2:trunc(ih*{h:F4}/2)*2:trunc(iw*{x:F4}/2)*2:trunc(ih*{y:F4}/2)*2") : string.Empty;
            var croppedWidth = hasCrop ? EvenFloor(sourceWidth * w) : EvenFloor(sourceWidth);
            var croppedHeight = hasCrop ? EvenFloor(sourceHeight * h) : EvenFloor(sourceHeight);
            var shrink = Math.Min(1.0, Math.Min(MaxDimension / (double)croppedWidth, MaxDimension / (double)croppedHeight));
            return new FramePlan(filter, croppedWidth, croppedHeight, EvenFloor(croppedWidth * shrink), EvenFloor(croppedHeight * shrink), sourceClipBytes);
        }
    }

    // 1画素・1コマあたりの容量の見積りから、最初に使うコマ数の段(画面が小さくなりすぎない最も高い段)を選ぶ
    private static int FirstTier(int[] tiers, double target, double duration, double bytesPerPixel, double minPixels)
    {
        for (var i = 0; i < tiers.Length; i++)
        {
            if (target / (duration * tiers[i]) / bytesPerPixel >= minPixels) return i;
        }
        return tiers.Length - 1;
    }

    private static double InitialScale(double target, double duration, int fps, double bytesPerPixel, double capPixels) =>
        Math.Clamp(Math.Sqrt(target / (duration * fps) / bytesPerPixel / capPixels), 0.08, 1.0);

    //--------------
    // 書き出し後の解像度の予想
    // 切り取り範囲・長さ・容量上限・形式から決まる出力の大きさとコマ数を見積もる。
    // EstimateOutput は式だけで求める(映像の細かさは分からないため、容量に余裕のある映像では実際より小さく出る)。
    // EstimateOutputAsync は映像の一部を試しに圧縮して、段階1・2(画質・コマ数)で収まるかを実測で見積もる。
    //--------------
    public async Task<(int Width, int Height, double Fps)> EstimateOutputAsync(ClipRequest r, int sourceWidth, int sourceHeight, CancellationToken cancellation)
    {
        if (r.ByteLimit is not long limit || r.Format == ClipFormat.Gif)
            return EstimateOutput(r.Format, sourceWidth, sourceHeight, r.CropWidth, r.CropHeight, r.Duration, r.ByteLimit, r.IncludeAudio);

        var plan = FramePlan.Create(r, sourceWidth, sourceHeight, long.MaxValue);
        var isMp4 = r.Format == ClipFormat.Mp4;
        var width = isMp4 ? plan.CapWidth : AnimatedWidth(plan, AnimatedLimitedLongEdge);
        var audioBytes = isMp4 && r.IncludeAudio ? 80 * 1000 / 8.0 * r.Duration : 0;

        // 試しに圧縮する長さ。短いクリップは全体、長いクリップは3か所から取り出して全体の容量を見積もる
        var sampleTotal = isMp4 ? 8.0 : 3.0;
        var whole = r.Duration <= sampleTotal * 1.25;
        var sampleLength = whole ? r.Duration : sampleTotal;
        var period = r.Duration / 3;
        var window = sampleTotal / 3;
        var seek = Inv($"-ss {r.Start:F3} -t {r.Duration:F3} -i \"{r.Input}\"");
        var codec = isMp4
            ? Inv($"-c:v libx264 -preset veryfast -crf {Mp4HalfQualityCrf} -pix_fmt yuv420p")
            : Inv($"-c:v libwebp_anim -lossless 0 -quality {WebpHalfQuality} -compression_level 3 -loop 0");
        var measuredBytes = 0.0;
        foreach (var fps in new[] { FullFps, ReducedFps })
        {
            var chain = VideoFilter(plan, width, fps);
            if (!whole) chain += Inv($",select='between(mod(t,{period:F3}),{period / 2 - window / 2:F3},{period / 2 + window / 2:F3})',setpts=N/({fps}*TB)");
            var sample = NewTemp(isMp4 ? "mp4" : "webp");
            try
            {
                await RunAsync(Inv($"-hide_banner -loglevel error {seek} -map 0:v:0 -an -vf \"{chain}\" {codec} -y \"{sample}\""), cancellation: cancellation);
                var videoBytes = new FileInfo(sample).Length * (r.Duration / sampleLength);
                if (videoBytes + audioBytes <= limit) return (width, plan.HeightFor(width), fps);
                measuredBytes = videoBytes;
            }
            finally { TryDelete(sample); }
        }
        if (!isMp4 && measuredBytes > 0)
        {
            // WebPは、画質を下げても収まらないとき、高い品質のまま画面を小さくして収める傾向がある。
            // 基準品質での容量が上限に収まる大きさを求める(容量は大きさのおよそ1.8乗、品質1あたり約8%で変わる)
            var atReference = measuredBytes * Math.Exp(WebpDefaultSlope * (WebpReferenceQuality - WebpHalfQuality));
            var scale = Math.Clamp(Math.Pow(limit * TargetFill / atReference, 1 / 1.8), 0.08, 1.0);
            var scaledWidth = Math.Min(width, Math.Max(48, EvenFloor(width * scale)));
            return (scaledWidth, plan.HeightFor(scaledWidth), ReducedFps);
        }
        return EstimateOutput(r.Format, sourceWidth, sourceHeight, r.CropWidth, r.CropHeight, r.Duration, r.ByteLimit, r.IncludeAudio, afterQualityAndFps: true);
    }

    public static (int Width, int Height, double Fps) EstimateOutput(ClipFormat format, int sourceWidth, int sourceHeight,
        double cropWidth, double cropHeight, double duration, long? byteLimit, bool includeAudio, bool afterQualityAndFps = false)
    {
        var plan = FramePlan.Create(new ClipRequest
        {
            Input = string.Empty, Output = string.Empty, Start = 0, Duration = duration, Format = format,
            CropWidth = cropWidth, CropHeight = cropHeight,
        }, sourceWidth, sourceHeight, long.MaxValue);
        duration = Math.Max(0.1, duration);

        if (format == ClipFormat.Mp4)
        {
            if (byteLimit is not long mp4Limit) return (plan.CapWidth, plan.CapHeight, FullFps);
            var capPixels = (double)plan.CapWidth * plan.CapHeight;
            var budgetKbps = Math.Max(30, mp4Limit * TargetFill * 8 / 1000 / duration - (includeAudio ? 80 : 0));
            if (!afterQualityAndFps && budgetKbps * 1000 / (FullFps * capPixels) >= Mp4MinBitsPerPixel) return (plan.CapWidth, plan.CapHeight, FullFps);
            if (!afterQualityAndFps && budgetKbps * 1000 / (ReducedFps * capPixels) >= Mp4MinBitsPerPixel) return (plan.CapWidth, plan.CapHeight, ReducedFps);
            var scale = Math.Clamp(Math.Sqrt(budgetKbps * 1000 / (ReducedFps * Mp4MinBitsPerPixel) / capPixels), 0.1, 1.0);
            var width = Math.Min(plan.CapWidth, Math.Max(64, EvenFloor(plan.CapWidth * scale)));
            return (width, plan.HeightFor(width), ReducedFps);
        }

        var isGif = format == ClipFormat.Gif;
        if (byteLimit is not long limit)
        {
            var width = AnimatedWidth(plan, AnimatedUnlimitedLongEdge);
            return (width, plan.HeightFor(width), isGif ? 20 : FullFps);
        }

        var capWidth = AnimatedWidth(plan, AnimatedLimitedLongEdge);
        var animatedPixels = (double)capWidth * plan.HeightFor(capWidth);
        var bytesPerPixel = isGif ? GifBytesPerPixel : WebpBytesPerPixel;
        var target = limit * (isGif ? GifTargetFill : TargetFill);
        if (!isGif && !afterQualityAndFps)
        {
            // WebPは、基準品質の半分の容量(約0.06バイト/画素)に収まる間は大きさを保ち、30→24fpsの順に落とす
            foreach (var fps in new[] { FullFps, ReducedFps })
            {
                if (target / (duration * fps) / animatedPixels >= bytesPerPixel) return (capWidth, plan.HeightFor(capWidth), fps);
            }
        }
        int[] tiers = isGif ? GifTiers : [ReducedFps, 20, 15, 12, 10];
        var tierFps = tiers[FirstTier(tiers, target, duration, bytesPerPixel, isGif ? GifMinPixels : WebpMinPixels)];
        var tierScale = InitialScale(target, duration, tierFps, bytesPerPixel, animatedPixels);
        var tierWidth = Math.Min(capWidth, Math.Max(48, EvenFloor(capWidth * tierScale)));
        return (tierWidth, plan.HeightFor(tierWidth), tierFps);
    }

    private static string VideoFilter(FramePlan plan, int width, int fps)
    {
        var parts = new List<string>();
        if (plan.CropFilter.Length > 0) parts.Add(plan.CropFilter);
        if (width != plan.CroppedWidth) parts.Add($"scale={width}:-2:flags=lanczos");
        parts.Add($"fps={fps}");
        return string.Join(",", parts);
    }

    private static int AnimatedWidth(FramePlan plan, int longEdge)
    {
        var shrink = Math.Min(1.0, longEdge / (double)Math.Max(plan.CapWidth, plan.CapHeight));
        return Math.Min(plan.CapWidth, EvenFloor(plan.CapWidth * shrink));
    }

    private void ReportSize(int width, int height, long size, long limit) =>
        report($"{width}×{height} → {size / 1048576.0:F2}MB（上限{limit / 1048576.0:F1}MB の{(double)size / limit * 100:F0}%）");

    // 上限以下で最も大きい候補だけを残し、それ以外は削除する
    private sealed class BestFit
    {
        public string? Path { get; private set; }
        public long Size { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public double Fps { get; private set; }

        public void Offer(string path, long size, long limit, int width, int height, double fps)
        {
            if (size <= limit && size > Size)
            {
                if (Path is not null) TryDelete(Path);
                Path = path; Size = size; Width = width; Height = height; Fps = fps;
            }
            else TryDelete(path);
        }
    }

    private ClipResult Finish(BestFit best, string output)
    {
        Move(best.Path!, output);
        return Finish(output, best.Width, best.Height, best.Fps);
    }

    private ClipResult Finish(string output, int width, int height, double fps) =>
        new(new FileInfo(output).Length, width, height, fps, attempts);

    private string NewTemp(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dcr-{Guid.NewGuid():N}.{extension}");
        temporaryFiles.Add(path);
        if (extension == "passlog") { temporaryFiles.Add(path + "-0.log"); temporaryFiles.Add(path + "-0.log.mbtree"); }
        return path;
    }

    private static void Move(string from, string to) => File.Move(from, to, true);
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static int EvenFloor(double value) => Math.Max(2, (int)Math.Floor(value / 2) * 2);
    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private async Task<string> RunAsync(string args, bool allowFailure = false, CancellationToken cancellation = default)
    {
        if (!cancellation.CanBeCanceled) cancellation = encodeCancellation;
        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo(ffmpegPath, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })
            ?? throw new InvalidOperationException("エンコードを開始できませんでした。");
        using var killOnCancel = cancellation.Register(() => _ = Task.Run(() => { try { process.Kill(true); } catch { } })); // 取り消しを呼んだ画面(UI)の処理を止めない
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None);
        cancellation.ThrowIfCancellationRequested();
        var error = await errorTask;
        if (process.ExitCode != 0 && !allowFailure) throw new InvalidOperationException("エンコードに失敗しました。\n" + error);
        return error;
    }
}
