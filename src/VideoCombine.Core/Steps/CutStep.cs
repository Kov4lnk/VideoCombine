namespace VideoCombine.Core.Steps;

using VideoCombine.Core.External;
using VideoCombine.Core.Models;
using VideoCombine.Core.Pipeline;

/// <summary>
/// ПРИКЛАД ПОВНОЦІННОГО КРОКУ. Ріже джерела на сегменти.
/// Демонструє: звітування прогресу, паралельність УСЕРЕДИНІ кроку з лімітом,
/// пропуск дрібних збоїв без падіння всього прогону.
///
/// Паралельність (розділ 8.2): планування і виконання РОЗДІЛЕНІ. Спершу
/// PlanCuts однопотоково рахує ВСІ відрізки всіх джерел у плаский список
/// (тут єдиний Random — тож без гонки). Потім КОЖЕН відрізок — незалежна
/// ffmpeg-задача, і всі вони йдуть через один спільний семафор. Так повний
/// паралелізм є і на 30 джерелах×1 сегмент, і на 1 джерелі×30 сегментів
/// (головний сценарій переробки) — раніше друге різалось послідовно.
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
        // Джерела без відомої тривалості нарізати не можна (потрібен Analyze) —
        // попереджаємо й пропускаємо, решту плануємо.
        foreach (var s in context.Sources.Where(s => s.Duration is null))
            progress.Log(LogLevel.Warning, $"{s.Id}: пропущено (невідома тривалість — потрібен аналіз)");

        // Планування однопотокове: єдиний Random не в гонці (був латентний баг —
        // спільний Random смикався з кількох потоків при паралельних джерелах).
        var jobs = PlanCuts(context.Sources, _settings, context.WorkingDirectory, new Random());
        if (jobs.Count == 0)
            return;

        var limit = Math.Min(MaxConcurrency, Math.Max(1, context.Settings.MaxFfmpegConcurrency));
        using var gate = new SemaphoreSlim(limit);

        int done = 0;
        var total = jobs.Count;
        var produced = new List<Segment>(total);
        var lockObj = new object();

        // ВСІ відрізки — незалежні задачі під спільним лімітом (розділ 8.2).
        var tasks = jobs.Select(async job =>
        {
            await gate.WaitAsync(token);
            try
            {
                var code = await _runner.RunAsync(
                    context.Settings.FfmpegPath, BuildCutArgs(job), null, token);
                if (code == 0)
                {
                    var seg = new Segment
                    {
                        Id = job.SegmentId,
                        FilePath = job.OutputPath,
                        SourceId = job.SourceId,
                        Start = job.Start,
                        Duration = job.Length,
                        HasAudio = job.HasAudio
                    };
                    lock (lockObj) produced.Add(seg);
                }
                else
                {
                    progress.Log(LogLevel.Warning, $"{job.SegmentId}: пропущено (ffmpeg код {code})");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Дрібний збій одного відрізка — пропускаємо, не валимо прогон (розділ 7.1).
                progress.Log(LogLevel.Warning, $"{job.SegmentId}: пропущено ({ex.Message})");
            }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref done);
                progress.ReportStepProgress($"{n}/{total} сегментів", (double)n / total);
            }
        });

        await Task.WhenAll(tasks);

        // Паралельні задачі завершуються врозбій — відновлюємо детермінований
        // порядок (як у джерелах, усередині — за індексом відрізка), інакше
        // склейка отримала б перемішані сегменти. Id = "{sourceId}_seg{idx:D4}":
        // zero-padding робить лексичне порівняння коректним за індексом.
        var sourceOrder = new Dictionary<string, int>();
        for (int i = 0; i < context.Sources.Count; i++)
            sourceOrder[context.Sources[i].Id] = i;

        produced.Sort((a, b) =>
        {
            var c = sourceOrder.GetValueOrDefault(a.SourceId).CompareTo(sourceOrder.GetValueOrDefault(b.SourceId));
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });

        context.Segments.AddRange(produced);
        progress.Log(LogLevel.Success, $"Нарізано {produced.Count} сегментів із {context.Sources.Count} джерел");
    }

    /// <summary>
    /// Аргументи ffmpeg для одного відрізка: -ss (звідки) перед -i — швидкий пошук,
    /// -t (скільки), -c copy — без перекодування. Час форматуємо через FfmpegFormat
    /// (завжди з крапкою): на локалі з комою "-ss 5,123" ffmpeg відкидав, і майже
    /// всі сегменти мовчки пропускались (docs/CHANGELOG.md, запис 003).
    /// Винесено окремо й публічно — під тести.
    /// </summary>
    public static IReadOnlyList<string> BuildCutArgs(CutJob job) => new[]
    {
        "-ss", FfmpegFormat.Seconds(job.Start),
        "-i", job.InputPath,
        "-t", FfmpegFormat.Seconds(job.Length),
        "-c", "copy", "-y", job.OutputPath
    };

    /// <summary>
    /// Планує всі відрізки всіх джерел у плаский список — ЧИСТА, однопотокова,
    /// тестована функція. Джерела без Duration пропускає. Random передається
    /// ззовні: у прогоні — new Random() (нарізка випадкова щоразу), у тестах —
    /// сідований, для передбачуваності. Винесено окремо й публічно для тесту.
    /// </summary>
    public static List<CutJob> PlanCuts(
        IReadOnlyList<SourceVideo> sources, CutSettings settings, string workingDir, Random rng)
    {
        var jobs = new List<CutJob>();
        foreach (var source in sources)
        {
            if (source.Duration is null) continue;

            var pos = TimeSpan.Zero;
            var end = source.Duration.Value;
            int idx = 0;

            while (pos < end)
            {
                var len = settings.PickLength(rng);
                if (pos + len > end) len = end - pos;

                var id = $"{source.Id}_seg{idx:D4}";
                jobs.Add(new CutJob(
                    SourceId: source.Id,
                    SegmentId: id,
                    InputPath: source.FilePath,
                    OutputPath: Path.Combine(workingDir, id + ".mp4"),
                    Start: pos,
                    Length: len,
                    // null (не аналізували) → вважаємо, що звук є, як було раніше.
                    HasAudio: source.HasAudio ?? true));

                pos += len;
                idx++;
            }
        }
        return jobs;
    }
}

/// <summary>Спланований відрізок нарізки — незалежна одиниця роботи (див. PlanCuts).</summary>
public readonly record struct CutJob(
    string SourceId,
    string SegmentId,
    string InputPath,
    string OutputPath,
    TimeSpan Start,
    TimeSpan Length,
    bool HasAudio = true);

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
