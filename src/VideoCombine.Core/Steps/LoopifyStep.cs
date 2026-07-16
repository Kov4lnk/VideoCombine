namespace VideoCombine.Core.Steps;

using VideoCombine.Core.External;
using VideoCombine.Core.Pipeline;

/// <summary>
/// ОПЦІЙНИЙ крок зациклення (розділ 3.x). Бере FinalOutputPath і переробляє
/// фінальне відео так, щоб воно безшовно зациклювалось при програванні.
/// Стоїть у хвості, після SubtitleStep (потребує FinalOutputPath).
///
/// Три режими:
///   • crossfade   — плавне накладання кінця на початок (xfade + acrossfade).
///                   Файл коротший на FadeDuration. Надійний для будь-якого контенту.
///   • reverse     — відео + реверс = "boomerang". Ідеальний луп завжди, але
///                   рух іде вперед-назад. Тривалість ×2. Для ambient/абстракцій.
///   • smart-trim  — знайти в кінці кадр, схожий на початок (SSIM), обрізати там.
///                   Файл коротший. ЕКСПЕРИМЕНТАЛЬНИЙ: на нарративному контенті
///                   підходящої точки може не знайтись (SSIM впаде на дефолтний trim).
///
/// Патерн опційності — як апскейл/субтитри: вимкнено → крок не вставляється взагалі.
/// MaxConcurrency=1 — один фінальний файл.
/// </summary>
public sealed class LoopifyStep : IStep
{
    private readonly ProcessRunner _runner;
    private readonly LoopifySettings _settings;

    public LoopifyStep(ProcessRunner runner, LoopifySettings settings)
    {
        _runner = runner;
        _settings = settings;
    }

    public string TypeId => "loopify";
    public string DisplayName => "Зациклення";
    public int MaxConcurrency => 1;

    public IReadOnlyList<StepRequirement> GetRequirements(PipelineContext context)
    {
        var reqs = new List<StepRequirement>();
        if (string.IsNullOrWhiteSpace(context.FinalOutputPath))
            reqs.Add(new StepRequirement(
                "Немає фінального відео для зациклення (крок має стояти після Склейки)",
                IsBlocking: true));
        return reqs;
    }

    public async Task ExecuteAsync(PipelineContext context, IProgressReporter progress, CancellationToken token)
    {
        var videoPath = context.FinalOutputPath
            ?? throw new InvalidOperationException("Немає фінального відео для зациклення");

        var dir = Path.GetDirectoryName(videoPath) ?? context.OutputDirectory;
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var ext = Path.GetExtension(videoPath);

        var outputPath = Path.Combine(dir, $"{baseName}_loop{ext}");

        progress.Log(LogLevel.Info, $"Зациклення ({_settings.Mode}): {Path.GetFileName(videoPath)}");

        switch (_settings.Mode.ToLowerInvariant())
        {
            case "reverse":
                await RunReverseAsync(videoPath, outputPath, context, progress, token);
                break;

            case "smart-trim":
                await RunSmartTrimAsync(videoPath, outputPath, context, progress, token);
                break;

            default: // "crossfade" і будь-що невідоме → безпечний дефолт
                await RunCrossfadeAsync(videoPath, outputPath, context, progress, token);
                break;
        }

        if (!File.Exists(outputPath))
            throw new InvalidOperationException($"Зациклення не створило файл: {outputPath}");

        context.FinalOutputPath = outputPath;
        progress.Log(LogLevel.Success, $"Зациклено: {outputPath}");
        progress.ReportStepProgress("готово", 1.0);
    }

    // ─── Crossfade ────────────────────────────────────────────────────────────

    private async Task RunCrossfadeAsync(
        string input, string output, PipelineContext context,
        IProgressReporter progress, CancellationToken token)
    {
        progress.ReportStepProgress("crossfade (xfade + acrossfade)", 0.1);
        var duration = await GetDurationAsync(input, context.Settings.FfprobePath, token);

        var args = BuildCrossfadeArgs(input, output, duration, _settings.FadeDuration);
        var code = await _runner.RunAsync(context.Settings.FfmpegPath, args,
            line => ReportFfmpegLine(line, progress), token);

        if (code != 0)
            throw new InvalidOperationException($"ffmpeg crossfade код {code}");
    }

    /// <summary>
    /// Crossfade-луп: два входи — той самий файл двічі. xfade накладає кінець першого
    /// на початок другого; acrossfade робить те саме для аудіо. Результат при зациклюванні
    /// дає непомітний стик. Offset = duration - fade: xfade стартує за fadeSec до кінця.
    /// FadeSec обрізається до 40% тривалості — щоб не з'їсти більше половини відео.
    /// Vinесено публічно — для тесту (можна перевірити аргументи без ffmpeg).
    /// </summary>
    public static IReadOnlyList<string> BuildCrossfadeArgs(
        string input, string output, double durationSec, double fadeSec)
    {
        // Обрізання всередині методу — щоб метод завжди повертав безпечні аргументи,
        // незалежно від того, де він викликається (RunCrossfadeAsync або тест).
        var fade = Math.Min(fadeSec, durationSec * 0.4);
        var offset = Math.Max(0, durationSec - fade);
        return new[]
        {
            "-i", input, "-i", input,
            "-filter_complex",
            $"[0:v][1:v]xfade=transition=fade:duration={fade:0.###}:offset={offset:0.###}[outv];" +
            $"[0:a][1:a]acrossfade=d={fade:0.###}[outa]",
            "-map", "[outv]", "-map", "[outa]",
            "-c:v", "libx264", "-preset", "medium", "-crf", "20",
            "-c:a", "aac",
            "-y", output
        };
    }

    // ─── Reverse (boomerang) ──────────────────────────────────────────────────

    private async Task RunReverseAsync(
        string input, string output, PipelineContext context,
        IProgressReporter progress, CancellationToken token)
    {
        progress.ReportStepProgress("reverse + concat (boomerang)", 0.1);
        var args = BuildReverseArgs(input, output);
        var code = await _runner.RunAsync(context.Settings.FfmpegPath, args,
            line => ReportFfmpegLine(line, progress), token);

        if (code != 0)
            throw new InvalidOperationException($"ffmpeg reverse код {code}");
    }

    /// <summary>
    /// Boomerang-луп: reverse фільтр відзеркалює відео/аудіо, concat зшиває оригінал
    /// із реверсом. Перехід ідеальний (кінець оригіналу = початок реверсу), тривалість ×2.
    /// Увага: reverse буферизує ВЕСЬ відеопоток у пам'яті — на довгих відео може
    /// споживати багато RAM. Для фонових кліпів (5-30 с) це нормально.
    /// Винесено публічно — для тесту.
    /// </summary>
    public static IReadOnlyList<string> BuildReverseArgs(string input, string output) => new[]
    {
        "-i", input,
        "-filter_complex",
        "[0:v]reverse[rv];[0:a]areverse[ra];" +
        "[0:v][0:a][rv][ra]concat=n=2:v=1:a=1[outv][outa]",
        "-map", "[outv]", "-map", "[outa]",
        "-c:v", "libx264", "-preset", "medium", "-crf", "20",
        "-c:a", "aac",
        "-y", output
    };

    // ─── Smart trim ───────────────────────────────────────────────────────────

    private async Task RunSmartTrimAsync(
        string input, string output, PipelineContext context,
        IProgressReporter progress, CancellationToken token)
    {
        progress.ReportStepProgress("smart-trim: аналіз кадрів (SSIM)", 0.1);

        var duration = await GetDurationAsync(input, context.Settings.FfprobePath, token);
        // Шукаємо схожий кадр лише в останніх 30% відео, щоб не сканувати все.
        var searchStart = duration * 0.7;

        // 1) Витягуємо перший кадр як еталон.
        var refFrame = Path.Combine(context.WorkingDirectory, "loopify_ref.png");
        var extractCode = await _runner.RunAsync(
            context.Settings.FfmpegPath,
            BuildExtractFirstFrameArgs(input, refFrame),
            null, token);

        if (extractCode != 0 || !File.Exists(refFrame))
        {
            progress.Log(LogLevel.Warning,
                "smart-trim: не вдалось витягти еталонний кадр — відкат на простий trim 90%");
            await RunSimpleTrimFallbackAsync(input, output, duration * 0.9, context, progress, token);
            TryDelete(refFrame);
            return;
        }

        // 2) SSIM-порівняння еталону з кожним кадром останніх 30% відео.
        var ssimLog = Path.Combine(context.WorkingDirectory, "loopify_ssim.log");
        var ssimCode = await _runner.RunAsync(
            context.Settings.FfmpegPath,
            BuildSsimArgs(input, refFrame, ssimLog, searchStart),
            null, token);

        TryDelete(refFrame);

        double trimPoint = duration * 0.9; // дефолт на випадок провалу
        if (ssimCode == 0 && File.Exists(ssimLog))
        {
            var best = FindBestTrimPoint(File.ReadAllText(ssimLog), searchStart);
            if (best.HasValue)
            {
                trimPoint = best.Value;
                progress.Log(LogLevel.Info,
                    $"smart-trim: найкраща точка зрізу {trimPoint:0.##}s (SSIM)");
            }
            else
            {
                progress.Log(LogLevel.Warning,
                    $"smart-trim: SSIM не знайшов схожого кадру — відкат на {trimPoint:0.##}s (90%)");
            }
        }
        else
        {
            progress.Log(LogLevel.Warning,
                "smart-trim: SSIM-аналіз не вдався — відкат на trim 90%");
        }

        TryDelete(ssimLog);

        progress.ReportStepProgress($"trim до {trimPoint:0.##}s", 0.7);
        await RunSimpleTrimFallbackAsync(input, output, trimPoint, context, progress, token);
    }

    private async Task RunSimpleTrimFallbackAsync(
        string input, string output, double trimTo,
        PipelineContext context, IProgressReporter progress, CancellationToken token)
    {
        var code = await _runner.RunAsync(
            context.Settings.FfmpegPath,
            BuildTrimArgs(input, output, trimTo),
            line => ReportFfmpegLine(line, progress), token);

        if (code != 0)
            throw new InvalidOperationException($"ffmpeg trim код {code}");
    }

    /// <summary>
    /// Витягнути перший кадр відео як PNG (еталон для SSIM-порівняння).
    /// -vframes 1 — рівно один кадр; -q:v 2 — висока якість PNG.
    /// Винесено публічно — для тесту.
    /// </summary>
    public static IReadOnlyList<string> BuildExtractFirstFrameArgs(string input, string outputPng) => new[]
    {
        "-i", input,
        "-vframes", "1", "-q:v", "2",
        "-y", outputPng
    };

    /// <summary>
    /// SSIM-порівняння: перший кадр (PNG, -loop 1) як еталон проти відрізку відео
    /// (-ss searchStart). Виводить статистику в ssimLog — один рядок на кадр із полем
    /// "All:&lt;ssim&gt;" та "n:&lt;номер_кадру&gt;" і "pts_time:&lt;секунди&gt;".
    /// Формат: "n:42 Y:0.95 U:0.97 V:0.96 All:0.96 (13.35)"
    /// Увага: -loop 1 тримає PNG як нескінченне відео — ffmpeg зупиняється, коли
    /// вхід 1 (відрізок) вичерпується (shortest).
    /// Винесено публічно — для тесту (парсинг ssimLog перевіряється без ffmpeg).
    /// </summary>
    public static IReadOnlyList<string> BuildSsimArgs(
        string input, string refPng, string ssimLog, double searchStart) => new[]
    {
        "-loop", "1", "-i", refPng,               // еталон — перший кадр (нескінченний)
        "-ss", searchStart.ToString("0.###"),
        "-i", input,                               // відрізок для пошуку
        "-lavfi", $"ssim=stats_file={ssimLog}",
        "-shortest", "-f", "null", "-"
    };

    /// <summary>
    /// Простий trim: обрізати відео до trimToSec секунд. -c copy — без перекодування,
    /// ffmpeg сам знайде найближчий ключовий кадр. Винесено публічно — для тесту.
    /// </summary>
    public static IReadOnlyList<string> BuildTrimArgs(string input, string output, double trimToSec) => new[]
    {
        "-i", input,
        "-t", trimToSec.ToString("0.###"),
        "-c", "copy",
        "-y", output
    };

    /// <summary>
    /// Парсить лог SSIM і повертає pts_time кадру з найвищим SSIM (All:...).
    /// Формат рядка: "n:42 Y:0.95 U:0.97 V:0.96 All:0.96 (13.35)"
    /// де (13.35) — pts_time у секундах. Повертає null якщо нічого не знайшло
    /// або найкращий SSIM нижче мінімального порогу (0.85 — емпірично).
    /// Винесено публічно — для тесту.
    /// </summary>
    public static double? FindBestTrimPoint(string ssimLogContent, double searchOffsetSec)
    {
        const double MinAcceptableSsim = 0.85;

        double bestSsim = -1;
        double bestTime = -1;

        foreach (var line in ssimLogContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "All:0.9612" — шукаємо цей токен
            var allIdx = line.IndexOf("All:", StringComparison.Ordinal);
            if (allIdx < 0) continue;

            // Значення після "All:" до пробілу або кінця
            var allSpan = line.AsSpan(allIdx + 4);
            var spaceIdx = allSpan.IndexOf(' ');
            var allStr = spaceIdx >= 0 ? allSpan[..spaceIdx].ToString() : allSpan.ToString();
            if (!double.TryParse(allStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var ssim)) continue;

            // pts_time у дужках: "(13.35)"
            var openParen = line.LastIndexOf('(');
            var closeParen = line.LastIndexOf(')');
            if (openParen < 0 || closeParen <= openParen) continue;

            var timeStr = line[(openParen + 1)..closeParen];
            if (!double.TryParse(timeStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pts)) continue;

            // pts_time у лозі відносний до searchStart; конвертуємо в абсолютний.
            var absoluteTime = searchOffsetSec + pts;

            if (ssim > bestSsim)
            {
                bestSsim = ssim;
                bestTime = absoluteTime;
            }
        }

        return bestSsim >= MinAcceptableSsim ? bestTime : null;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<double> GetDurationAsync(
        string path, string ffprobePath, CancellationToken token)
    {
        // ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1
        double result = 0;
        var code = await _runner.RunAsync(
            ffprobePath,
            new[]
            {
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                path
            },
            line =>
            {
                if (double.TryParse(line.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                    result = d;
            },
            token);

        if (code != 0 || result <= 0)
            throw new InvalidOperationException(
                $"Не вдалось отримати тривалість відео ({path}) — ffprobe код {code}");

        return result;
    }

    private static void ReportFfmpegLine(string line, IProgressReporter progress)
    {
        if (line.Contains("time=", StringComparison.OrdinalIgnoreCase))
            progress.ReportStepProgress(line.Trim(), 0.5);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* тимчасовий файл — не критично */ }
    }
}

/// <summary>Налаштування зациклення (серіалізуються в JSON-рецепт).</summary>
public sealed class LoopifySettings
{
    /// <summary>Чи вставляти крок у ланцюг. Вимкнено → крок не додається.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Режим зациклення:
    ///   "crossfade"  — плавне накладання (xfade + acrossfade). Дефолт.
    ///   "reverse"    — boomerang (відео + реверс). Завжди ідеальний перехід.
    ///   "smart-trim" — SSIM-пошук схожого кадру + trim. Експериментальний.
    /// </summary>
    public string Mode { get; set; } = "crossfade";

    /// <summary>
    /// Тривалість crossfade у секундах (лише для режиму "crossfade").
    /// Менше = різкіший перехід; більше = плавніший, але файл коротший.
    /// Обрізається до 40% від тривалості відео, якщо занадто велике.
    /// </summary>
    public double FadeDuration { get; set; } = 1.0;
}
