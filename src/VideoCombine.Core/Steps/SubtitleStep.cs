namespace VideoCombine.Core.Steps;

using VideoCombine.Core.External;
using VideoCombine.Core.Pipeline;

/// <summary>
/// РОБОЧИЙ крок субтитрів (whisper.cpp). Транскрибує ФІНАЛЬНЕ відео (не оригінали:
/// у режимі переробки порядок сегментів інший, субтитри з оригіналів не збіглися б
/// із тим, що чути) і кладе поряд .srt. Опційно "випалює" субтитри в кадр (burn-in)
/// і переставляє FinalOutputPath на відео з субтитрами.
///
/// Whisper — це STT (звук → текст), поряд із TTS у категорії "Звук і мовлення", але
/// протилежний напрям. Стоїть у ХВОСТІ, після Склейки (потребує FinalOutputPath).
///
/// Двигун — whisper.cpp: один самодостатній exe (як ffmpeg/yt-dlp), смикаємо через
/// ProcessRunner. Потрібні шлях до exe (WhisperPath) і файл моделі (WhisperModelPath,
/// напр. ggml-base.bin). Мова — автовизначення (whisper слухає й розпізнає сам).
///
/// Політика опційності — як апскейл: вимкнено → крок не вставляється в ланцюг узагалі,
/// тому Enabled тут не перевіряємо. MaxConcurrency=1 — один вихідний файл.
/// </summary>
public sealed class SubtitleStep : IStep
{
    private readonly ProcessRunner _runner;
    private readonly SubtitleSettings _settings;

    public SubtitleStep(ProcessRunner runner, SubtitleSettings settings)
    {
        _runner = runner;
        _settings = settings;
    }

    public string TypeId => "subtitle";
    public string DisplayName => "Субтитри";

    // Один фінальний файл — паралелити нічого.
    public int MaxConcurrency => 1;

    public IReadOnlyList<StepRequirement> GetRequirements(PipelineContext context)
    {
        var reqs = new List<StepRequirement>();
        if (string.IsNullOrWhiteSpace(context.FinalOutputPath))
            reqs.Add(new StepRequirement("Немає фінального відео для субтитрів (крок має стояти після Склейки)", IsBlocking: true));
        if (string.IsNullOrWhiteSpace(context.Settings.WhisperPath))
            reqs.Add(new StepRequirement("Не вказано шлях до whisper (таб «Налаштування»)", IsBlocking: true));
        if (string.IsNullOrWhiteSpace(context.Settings.WhisperModelPath))
            reqs.Add(new StepRequirement("Не вказано модель Whisper (таб «Налаштування»)", IsBlocking: true));
        // burn-in вимагає ffmpeg — але той і так потрібен усій відеогілці, тож окремо не чіпаємо.
        return reqs;
    }

    public async Task ExecuteAsync(PipelineContext context, IProgressReporter progress, CancellationToken token)
    {
        // Захист на випадок обходу перевірки передумов: без відео/whisper/моделі — стоп.
        // (GetRequirements уже блокує це, але тут робимо шляхи non-null для компілятора й чесності.)
        var videoPath = context.FinalOutputPath
            ?? throw new InvalidOperationException("Нема фінального відео для субтитрів");
        var whisperPath = string.IsNullOrWhiteSpace(context.Settings.WhisperPath)
            ? throw new InvalidOperationException("Не вказано шлях до whisper")
            : context.Settings.WhisperPath;
        var modelPath = string.IsNullOrWhiteSpace(context.Settings.WhisperModelPath)
            ? throw new InvalidOperationException("Не вказано модель Whisper")
            : context.Settings.WhisperModelPath;

        var dir = Path.GetDirectoryName(videoPath) ?? context.OutputDirectory;
        var baseName = Path.GetFileNameWithoutExtension(videoPath);

        // 1) Витягуємо аудіо у форматі, який любить whisper.cpp: 16 kHz, mono, PCM wav.
        //    whisper.cpp приймає лише wav 16 kHz — годувати його mp4 напряму не можна.
        var wavPath = Path.Combine(context.WorkingDirectory, $"{baseName}_whisper.wav");
        progress.ReportStepProgress("витягування аудіо", 0.1);
        var extractCode = await _runner.RunAsync(
            context.Settings.FfmpegPath,
            BuildExtractWavArgs(videoPath, wavPath),
            null, token);
        if (extractCode != 0 || !File.Exists(wavPath))
            throw new InvalidOperationException($"ffmpeg не витяг аудіо для субтитрів (код {extractCode})");

        // 2) whisper.cpp транскрибує wav → SRT. Він сам додає розширення .srt до
        //    значення -of, тож передаємо шлях БЕЗ розширення.
        var srtBase = Path.Combine(dir, baseName);           // whisper допише .srt
        var srtPath = srtBase + ".srt";
        progress.ReportStepProgress("розпізнавання мовлення (whisper)", 0.3);
        var whisperCode = await _runner.RunAsync(
            whisperPath,
            BuildWhisperArgs(wavPath, srtBase, modelPath),
            line => ReportWhisperLine(line, progress),
            token);
        if (whisperCode != 0 || !File.Exists(srtPath))
            throw new InvalidOperationException($"whisper не створив субтитри (код {whisperCode})");

        progress.Log(LogLevel.Success, $"Субтитри: {srtPath}");

        // Прибираємо тимчасовий wav — він більше не потрібен.
        TryDelete(wavPath);

        // 3) Burn-in (опційно): вписати субтитри в кадр і зробити фінальним це відео.
        if (!_settings.BurnIn)
        {
            progress.ReportStepProgress("готово (SRT поряд із відео)", 1.0);
            return;
        }

        progress.ReportStepProgress("вписування субтитрів у кадр", 0.7);
        var ext = Path.GetExtension(videoPath);
        var burnedPath = Path.Combine(dir, $"{baseName}_sub{ext}");
        var burnCode = await _runner.RunAsync(
            context.Settings.FfmpegPath,
            BuildBurnInArgs(videoPath, srtPath, burnedPath),
            line => ReportFfmpegLine(line, progress),
            token);
        if (burnCode != 0 || !File.Exists(burnedPath))
            throw new InvalidOperationException($"ffmpeg не вписав субтитри (код {burnCode})");

        // Фінальним стає відео з субтитрами; оригінал і SRT лишаємо поряд.
        context.FinalOutputPath = burnedPath;
        progress.Log(LogLevel.Success, $"Відео з субтитрами: {burnedPath}");
        progress.ReportStepProgress("готово (субтитри в кадрі)", 1.0);
    }

    /// <summary>
    /// Аргументи витягування аудіо для whisper.cpp: 16 kHz, mono, PCM s16 wav.
    /// -vn прибирає відео, -ar 16000 -ac 1 — саме те, що whisper.cpp очікує.
    /// Винесено окремо й публічно — під тести.
    /// </summary>
    public static IReadOnlyList<string> BuildExtractWavArgs(string inputPath, string wavPath) => new[]
    {
        "-i", inputPath,
        "-vn", "-ar", "16000", "-ac", "1",
        "-c:a", "pcm_s16le",
        "-y", wavPath
    };

    /// <summary>
    /// Аргументи whisper.cpp: модель, вхідний wav, вивід SRT, автовизначення мови.
    /// -l auto — whisper сам розпізнає мову. -osrt — формат субтитрів SRT.
    /// -of приймає шлях БЕЗ розширення (whisper.cpp сам додасть .srt).
    /// Винесено окремо й публічно — під тести.
    /// </summary>
    public static IReadOnlyList<string> BuildWhisperArgs(string wavPath, string outputBase, string modelPath) => new[]
    {
        "-m", modelPath,
        "-f", wavPath,
        "-l", "auto",       // автовизначення мови (укр/англ/…)
        "-osrt",            // вивід у SRT
        "-of", outputBase   // без розширення — whisper.cpp допише .srt
    };

    /// <summary>
    /// Аргументи burn-in: фільтр subtitles= вписує SRT у кадр. Відео перекодовуємо
    /// (libx264, crf 20), аудіо копіюємо. Шлях до SRT ЕКРАНУЄМО (див. EscapeSubtitlesPath):
    /// у filter-синтаксисі ffmpeg двокрапка після літери диска Windows (C:) і зворотні
    /// слеші мають особливе значення — без екранування фільтр падає. Винесено публічно — під тести.
    /// </summary>
    public static IReadOnlyList<string> BuildBurnInArgs(string inputPath, string srtPath, string outputPath) => new[]
    {
        "-i", inputPath,
        "-vf", $"subtitles='{EscapeSubtitlesPath(srtPath)}'",
        "-c:v", "libx264", "-preset", "medium", "-crf", "20",
        "-c:a", "copy",
        "-y", outputPath
    };

    /// <summary>
    /// Екранує шлях для фільтра subtitles= ffmpeg. На Windows:
    ///   • зворотний слеш \ → / (ffmpeg-фільтр трактує \ як escape);
    ///   • двокрапку диска C: → C\: (двокрапка розділяє опції фільтра).
    /// Напр. C:\out\a.srt → C\:/out/a.srt. Винесено публічно — під тести.
    /// </summary>
    public static string EscapeSubtitlesPath(string path) =>
        path.Replace('\\', '/').Replace(":", "\\:");

    private static void ReportWhisperLine(string line, IProgressReporter progress)
    {
        // whisper.cpp сипле рядки з таймкодами "[00:00:01.000 --> 00:00:03.000] текст".
        var t = line.Trim();
        if (t.StartsWith('[') && t.Contains("-->"))
            progress.ReportStepProgress(t, 0.5);
    }

    private static void ReportFfmpegLine(string line, IProgressReporter progress)
    {
        if (line.Contains("time=", StringComparison.OrdinalIgnoreCase))
            progress.ReportStepProgress(line.Trim(), 0.85);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* тимчасовий файл — не критично, лишиться в робочій папці */ }
    }
}

/// <summary>Налаштування субтитрів (серіалізуються в JSON-рецепт).</summary>
public sealed class SubtitleSettings
{
    /// <summary>
    /// Чи вставляти крок у ланцюг. Опційний (як апскейл): вимкнено — крок не додається.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// true — вписати субтитри в кадр (burn-in), зробити це відео фінальним;
    /// false (дефолт) — лишити лише .srt поряд із відео (м'якші субтитри, плеєр вмикає сам).
    /// </summary>
    public bool BurnIn { get; set; }
}
