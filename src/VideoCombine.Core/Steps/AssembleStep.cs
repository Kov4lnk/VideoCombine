namespace VideoCombine.Core.Steps;

using System.Text;
using VideoCombine.Core.External;
using VideoCombine.Core.Models;
using VideoCombine.Core.Pipeline;

/// <summary>
/// Крок склейки (FFmpeg concat). Зшиває сегменти з контексту в один файл,
/// виставляє context.FinalOutputPath (розділ 3.8).
///
/// Два режими (розділ 3.8, "Нюанс продуктивності"):
///   • copy   — concat demuxer без перекодування. Миттєвий, АЛЕ вимагає
///              однакових кодек/роздільність/fps у всіх сегментах.
///              Безпечний лише для сегментів з ОДНОГО джерела.
///   • encode — concat filter із перекодуванням. Повільніший, але НАДІЙНИЙ:
///              нормалізує різні джерела в один формат. ДЕФОЛТ для MVP,
///              бо мета — готове відео без ручної доробки.
///
/// Порядок сегментів: sequential (як є) або random (перемішати).
/// </summary>
public sealed class AssembleStep : IStep
{
    private readonly ProcessRunner _runner;
    private readonly AssembleSettings _settings;

    public AssembleStep(ProcessRunner runner, AssembleSettings settings)
    {
        _runner = runner;
        _settings = settings;
    }

    public string TypeId => "assemble";
    public string DisplayName => "Склейка";

    // Один вихідний рендер — паралелити нічого.
    public int MaxConcurrency => 1;

    public IReadOnlyList<StepRequirement> GetRequirements(PipelineContext context)
    {
        var reqs = new List<StepRequirement>();
        if (context.Segments.Count == 0)
            reqs.Add(new StepRequirement("Немає сегментів для склейки", IsBlocking: true));
        if (string.IsNullOrWhiteSpace(context.Settings.FfmpegPath))
            reqs.Add(new StepRequirement("Не вказано шлях до ffmpeg", IsBlocking: true));
        return reqs;
    }

    public async Task ExecuteAsync(PipelineContext context, IProgressReporter progress, CancellationToken token)
    {
        // Порядок сегментів (розділ 3.8). Random — детермінований seed із налаштувань,
        // щоб повторний прогон того ж рецепта дав той самий результат.
        var ordered = OrderSegments(context.Segments, _settings.Order, _settings.RandomSeed);

        var outputPath = Path.Combine(context.OutputDirectory, _settings.OutputFileName);
        Directory.CreateDirectory(context.OutputDirectory);

        progress.ReportStepProgress($"склейка {ordered.Count} сегментів ({_settings.Mode})", 0);

        if (_settings.Mode.Equals("copy", StringComparison.OrdinalIgnoreCase))
            await ConcatCopyAsync(ordered, context, outputPath, progress, token);
        else
            await ConcatEncodeAsync(ordered, context, outputPath, progress, token);

        if (!File.Exists(outputPath))
            throw new InvalidOperationException("Склейка не створила вихідний файл");

        context.FinalOutputPath = outputPath;
        progress.ReportStepProgress("готово", 1.0);
        progress.Log(LogLevel.Success, $"Зібрано: {outputPath}");
    }

    /// <summary>Швидкий шлях: concat demuxer + -c copy. Потрібен список-файл.</summary>
    private async Task ConcatCopyAsync(
        IReadOnlyList<Segment> segments, PipelineContext context,
        string outputPath, IProgressReporter progress, CancellationToken token)
    {
        var listPath = Path.Combine(context.WorkingDirectory, "concat_list.txt");
        await File.WriteAllTextAsync(listPath, BuildConcatList(segments), new UTF8Encoding(false), token);

        var args = new[]
        {
            "-f", "concat", "-safe", "0",
            "-i", listPath,
            "-c", "copy",
            "-y", outputPath
        };

        var code = await _runner.RunAsync(context.Settings.FfmpegPath, args,
            line => ReportFfmpegLine(line, progress), token);

        if (code != 0)
            throw new InvalidOperationException(
                $"ffmpeg concat-copy код {code} (можливо, сегменти різного формату — спробуйте режим 'encode')");
    }

    /// <summary>Надійний шлях: нормалізація кожного входу + concat filter.</summary>
    private async Task ConcatEncodeAsync(
        IReadOnlyList<Segment> segments, PipelineContext context,
        string outputPath, IProgressReporter progress, CancellationToken token)
    {
        var args = new List<string>();
        foreach (var seg in segments)
        {
            args.Add("-i");
            args.Add(seg.FilePath);
        }

        // Кожен вхід приводимо до спільного формату, ІНАКШЕ concat падає (код -22),
        // коли сегменти різної роздільності/fps (напр. 1080p + 720p джерела).
        var filter = BuildConcatFilter(
            segments.Count, _settings.TargetWidth, _settings.TargetHeight, _settings.TargetFps);

        args.Add("-filter_complex");
        args.Add(filter);
        args.Add("-map"); args.Add("[outv]");
        args.Add("-map"); args.Add("[outa]");
        args.Add("-c:v"); args.Add("libx264");
        args.Add("-preset"); args.Add(_settings.EncodePreset);
        args.Add("-crf"); args.Add(_settings.Crf.ToString());
        args.Add("-c:a"); args.Add("aac");
        args.Add("-y"); args.Add(outputPath);

        var code = await _runner.RunAsync(context.Settings.FfmpegPath, args,
            line => ReportFfmpegLine(line, progress), token);

        if (code != 0)
            throw new InvalidOperationException($"ffmpeg concat-encode код {code}");
    }

    private static void ReportFfmpegLine(string line, IProgressReporter progress)
    {
        // ffmpeg сипле прогрес у stderr рядками "frame=... time=...".
        if (line.Contains("time=", StringComparison.OrdinalIgnoreCase))
            progress.ReportStepProgress(line.Trim(), 0.5);
    }

    /// <summary>
    /// Будує filter_complex: нормалізує КОЖЕН вхід до спільного формату,
    /// потім зшиває concat'ом. Винесено окремо й публічно — для тесту.
    ///
    /// Нормалізація відео на вхід i:
    ///   scale (вписати у WxH, зберегти пропорції) → pad (letterbox до WxH,
    ///   центрувати) → setsar=1 (квадратний піксель) → fps (єдина частота).
    /// Без цього concat падає (код -22) на сегментах різної роздільності/fps.
    /// Аудіо: aresample — щоб частоти дискретизації теж збіглися.
    /// </summary>
    public static string BuildConcatFilter(int n, int width, int height, int fps)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            sb.Append($"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease,")
              .Append($"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps}[v{i}];")
              .Append($"[{i}:a:0]aresample=async=1[a{i}];");
        }
        for (int i = 0; i < n; i++)
            sb.Append($"[v{i}][a{i}]");
        sb.Append($"concat=n={n}:v=1:a=1[outv][outa]");
        return sb.ToString();
    }

    /// <summary>
    /// Формує вміст списку-файлу для concat demuxer. Винесено окремо для тесту.
    /// Кожен рядок: file '/шлях'. Одинарні лапки в шляху екрануються як '\''
    /// (вимога синтаксису concat demuxer).
    /// </summary>
    public static string BuildConcatList(IEnumerable<Segment> segments)
    {
        var sb = new StringBuilder();
        foreach (var seg in segments)
        {
            var escaped = seg.FilePath.Replace("'", "'\\''");
            sb.Append("file '").Append(escaped).Append('\'').Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Упорядковує сегменти. sequential — як є; random — детерміноване
    /// перемішування за seed (повторюваність прогону). Винесено окремо для тесту.
    /// </summary>
    public static IReadOnlyList<Segment> OrderSegments(
        IReadOnlyList<Segment> segments, string order, int seed)
    {
        if (!order.Equals("random", StringComparison.OrdinalIgnoreCase))
            return segments;

        var arr = segments.ToArray();
        var rng = new Random(seed);
        // Фішер-Йейтс.
        for (int i = arr.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }
        return arr;
    }
}

/// <summary>Налаштування склейки (серіалізуються в JSON-рецепт).</summary>
public sealed class AssembleSettings
{
    /// <summary>"encode" (надійно, дефолт) | "copy" (швидко, лише однорідні сегменти).</summary>
    public string Mode { get; set; } = "encode";

    /// <summary>"sequential" (як є) | "random" (перемішати).</summary>
    public string Order { get; set; } = "sequential";

    /// <summary>Seed для random-порядку — повторюваність прогону.</summary>
    public int RandomSeed { get; set; } = 12345;

    public string OutputFileName { get; set; } = "output.mp4";

    // Параметри перекодування (режим encode).
    public string EncodePreset { get; set; } = "medium";
    public int Crf { get; set; } = 20;

    // Спільний формат, до якого нормалізуються сегменти перед склейкою
    // (режим encode). Дефолт 1080p/30fps (розділ 7.5). Потрібно, бо concat
    // вимагає однакових роздільності/SAR/fps у всіх входів — інакше код -22.
    public int TargetWidth { get; set; } = 1920;
    public int TargetHeight { get; set; } = 1080;
    public int TargetFps { get; set; } = 30;
}
