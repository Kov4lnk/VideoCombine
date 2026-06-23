namespace VideoCombine.Core.Steps;

using System.Globalization;
using System.Text;
using System.Text.Json;
using VideoCombine.Core.External;
using VideoCombine.Core.Models;
using VideoCombine.Core.Pipeline;

/// <summary>
/// Крок аналізу (ffprobe). Заповнює метадані кожного джерела:
/// тривалість, роздільність, fps. Дешевий — робиться майже завжди (розділ 3.2).
///
/// КРИТИЧНО для нарізки: CutStep вимагає Duration. Тому в типовому ланцюзі
/// Analyze стоїть одразу після Download.
///
/// Лінива логіка (розділ 3.2): джерела, що ВЖЕ мають Duration, пропускаємо —
/// аналіз не робиться двічі.
/// </summary>
public sealed class AnalyzeStep : IStep
{
    private readonly ProcessRunner _runner;

    public AnalyzeStep(ProcessRunner runner)
    {
        _runner = runner;
    }

    public string TypeId => "analyze";
    public string DisplayName => "Аналіз";

    // ffprobe дешевий і CPU-незатратний — можна багато паралельно.
    public int MaxConcurrency => 8;

    public IReadOnlyList<StepRequirement> GetRequirements(PipelineContext context)
    {
        var reqs = new List<StepRequirement>();
        if (context.Sources.Count == 0)
            reqs.Add(new StepRequirement("Немає джерел для аналізу", IsBlocking: true));
        if (string.IsNullOrWhiteSpace(context.Settings.FfprobePath))
            reqs.Add(new StepRequirement("Не вказано шлях до ffprobe", IsBlocking: true));
        return reqs;
    }

    public async Task ExecuteAsync(PipelineContext context, IProgressReporter progress, CancellationToken token)
    {
        var limit = Math.Min(MaxConcurrency, context.Settings.MaxFfmpegConcurrency);
        using var gate = new SemaphoreSlim(limit);

        int done = 0;
        var total = context.Sources.Count;

        var tasks = context.Sources.Select(async source =>
        {
            await gate.WaitAsync(token);
            try
            {
                // Лінивість: вже проаналізоване не чіпаємо.
                if (source.Duration is not null)
                {
                    progress.Log(LogLevel.Info, $"{source.Title ?? source.Id}: вже проаналізовано");
                    return;
                }

                await AnalyzeOneAsync(source, context, token);
                progress.Log(LogLevel.Success,
                    $"{source.Title ?? source.Id}: {Describe(source)}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Дрібний збій одного файлу — пропускаємо (розділ 7.1).
                progress.Log(LogLevel.Warning, $"{source.Id}: аналіз пропущено ({ex.Message})");
            }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref done);
                progress.ReportStepProgress($"{n}/{total} джерел", (double)n / total);
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task AnalyzeOneAsync(SourceVideo source, PipelineContext context, CancellationToken token)
    {
        if (!File.Exists(source.FilePath))
            throw new FileNotFoundException("файл не знайдено", source.FilePath);

        var args = new[]
        {
            "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            source.FilePath
        };

        var sb = new StringBuilder();
        var code = await _runner.RunAsync(context.Settings.FfprobePath, args,
            line => sb.AppendLine(line), token);

        if (code != 0)
            throw new InvalidOperationException($"ffprobe код {code}");

        var meta = ParseProbe(sb.ToString());
        source.Duration = meta.Duration;
        source.Width = meta.Width;
        source.Height = meta.Height;
        source.Fps = meta.Fps;

        if (source.Duration is null)
            throw new InvalidOperationException("ffprobe не повернув тривалість");
    }

    private static string Describe(SourceVideo s)
    {
        var dur = s.Duration is { } d ? $"{d.TotalSeconds:0.#}с" : "?";
        var res = s.Width is { } w && s.Height is { } h ? $"{w}×{h}" : "?";
        var fps = s.Fps is { } f ? $"{f:0.##}fps" : "?";
        return $"{dur}, {res}, {fps}";
    }

    /// <summary>
    /// Парсить JSON-вивід ffprobe (-show_format -show_streams) у метадані.
    /// Винесено окремо й публічно, щоб тестувати без реального відео.
    /// Тривалість береться з format, а як запас — з відео-потоку.
    /// </summary>
    public static ProbeResult ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        TimeSpan? duration = null;
        int? width = null, height = null;
        double? fps = null;

        // Тривалість із секції format (найнадійніше).
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var fmtDur) &&
            TryParseSeconds(fmtDur, out var seconds))
        {
            duration = TimeSpan.FromSeconds(seconds);
        }

        // Перший відео-потік — розмір, fps, і тривалість як запасний варіант.
        if (root.TryGetProperty("streams", out var streams) &&
            streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var ct) ||
                    ct.GetString() != "video")
                    continue;

                if (stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi))
                    width = wi;
                if (stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var hi))
                    height = hi;
                if (stream.TryGetProperty("r_frame_rate", out var rfr))
                    fps = ParseFrameRate(rfr.GetString());

                if (duration is null &&
                    stream.TryGetProperty("duration", out var sDur) &&
                    TryParseSeconds(sDur, out var sSeconds))
                {
                    duration = TimeSpan.FromSeconds(sSeconds);
                }

                break; // перший відео-потік
            }
        }

        return new ProbeResult(duration, width, height, fps);
    }

    private static bool TryParseSeconds(JsonElement el, out double seconds)
    {
        seconds = 0;
        var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
        return s is not null &&
               double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    /// <summary>ffprobe дає fps дробом, напр. "30000/1001" → 29.97.</summary>
    private static double? ParseFrameRate(string? rfr)
    {
        if (string.IsNullOrWhiteSpace(rfr)) return null;
        var parts = rfr.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            den != 0)
        {
            return Math.Round(num / den, 3);
        }
        return null;
    }
}

/// <summary>Результат розбору ffprobe.</summary>
public sealed record ProbeResult(TimeSpan? Duration, int? Width, int? Height, double? Fps);
