namespace VideoCombine.Core.Steps;

using VideoCombine.Core.External;
using VideoCombine.Core.Models;
using VideoCombine.Core.Pipeline;

/// <summary>
/// ПРИКЛАД ПОВНОЦІННОГО КРОКУ. Ріже кожне джерело на сегменти.
/// Демонструє: звітування прогресу, паралельність УСЕРЕДИНІ кроку з лімітом,
/// пропуск дрібних збоїв без падіння всього прогону.
/// </summary>
public sealed class CutStep : IStep
{
    private readonly ProcessRunner _runner;
    private readonly CutSettings _settings;

    public CutStep(ProcessRunner runner, CutSettings settings)
    {
        _runner = runner;
        _settings = settings;
    }

    public string TypeId => "cut";
    public string DisplayName => "Нарізка";

    // CPU-операція FFmpeg — можна багато паралельно (розділ 8.3).
    public int MaxConcurrency => 8;

    public IReadOnlyList<StepRequirement> GetRequirements(PipelineContext context)
    {
        var reqs = new List<StepRequirement>();
        if (context.Sources.Count == 0)
            reqs.Add(new StepRequirement("Немає джерел для нарізки", IsBlocking: true));
        return reqs;
    }

    public async Task ExecuteAsync(PipelineContext context, IProgressReporter progress, CancellationToken token)
    {
        var limit = Math.Min(MaxConcurrency, context.Settings.MaxFfmpegConcurrency);
        using var gate = new SemaphoreSlim(limit);

        int done = 0;
        var total = context.Sources.Count;
        var rng = new Random();
        var lockObj = new object();

        // Паралельна обробка незалежних джерел із лімітом (розділ 8.2).
        var tasks = context.Sources.Select(async source =>
        {
            await gate.WaitAsync(token);
            try
            {
                var produced = await CutOneAsync(source, context, rng, token);
                lock (lockObj) context.Segments.AddRange(produced);
                progress.Log(LogLevel.Success, $"{source.Title ?? source.Id}: {produced.Count} сегментів");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Дрібний збій одного джерела — пропускаємо, не валимо прогон (розділ 7.1).
                progress.Log(LogLevel.Warning, $"{source.Id}: пропущено ({ex.Message})");
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

    private async Task<List<Segment>> CutOneAsync(
        SourceVideo source, PipelineContext context, Random rng, CancellationToken token)
    {
        var result = new List<Segment>();
        if (source.Duration is null)
            throw new InvalidOperationException("невідома тривалість (потрібен аналіз)");

        var pos = TimeSpan.Zero;
        var end = source.Duration.Value;
        int idx = 0;

        while (pos < end)
        {
            token.ThrowIfCancellationRequested();
            var len = _settings.PickLength(rng);
            if (pos + len > end) len = end - pos;

            var outPath = Path.Combine(context.WorkingDirectory, $"{source.Id}_seg{idx:D4}.mp4");
            var args = new[]
            {
                "-ss", pos.TotalSeconds.ToString("0.###"),
                "-i", source.FilePath,
                "-t", len.TotalSeconds.ToString("0.###"),
                "-c", "copy", "-y", outPath
            };

            var code = await _runner.RunAsync(context.Settings.FfmpegPath, args, null, token);
            if (code == 0)
            {
                result.Add(new Segment
                {
                    Id = $"{source.Id}_seg{idx:D4}",
                    FilePath = outPath,
                    SourceId = source.Id,
                    Start = pos,
                    Duration = len
                });
            }

            pos += len;
            idx++;
        }
        return result;
    }
}

/// <summary>Налаштування нарізки (серіалізуються в JSON-рецепт).</summary>
public sealed class CutSettings
{
    public string Mode { get; set; } = "random"; // random | fixed
    public double MinSec { get; set; } = 3;
    public double MaxSec { get; set; } = 8;

    public TimeSpan PickLength(Random rng) =>
        Mode == "fixed"
            ? TimeSpan.FromSeconds(MinSec)
            : TimeSpan.FromSeconds(MinSec + rng.NextDouble() * (MaxSec - MinSec));
}
