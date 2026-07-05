namespace VideoCombine.Core.Steps;

using VideoCombine.Core.External;
using VideoCombine.Core.Pipeline;
using VideoCombine.Core.Providers;

/// <summary>
/// ОПЦІЙНИЙ крок апскейлу (розділ 3.5). Бере сегменти з контексту, проганяє
/// кожен через обраний IUpscaleProvider і підміняє шлях сегмента на збільшений.
/// Стоїть між Нарізкою і Склейкою: апскейлимо короткі кліпи, а профіль склейки
/// потім зводить їх до цільового формату.
///
/// Перший крок, що НЕ обов'язковий: якщо вимкнено в Конструкторі — не вставляється
/// в ланцюг узагалі (бачить лише увімкненим). Тому Enabled тут не перевіряємо.
///
/// Паралельність помірна (MaxConcurrency=2): під FFmpeg-scale можна було б більше,
/// але закладаємось під майбутній Real-ESRGAN на GPU, де паралелити сильно не можна.
/// </summary>
public sealed class UpscaleStep : IStep
{
    private readonly IUpscaleProvider _provider;
    private readonly UpscaleSettings _settings;

    public UpscaleStep(IUpscaleProvider provider, UpscaleSettings settings)
    {
        _provider = provider;
        _settings = settings;
    }

    public string TypeId => "upscale";
    public string DisplayName => "Апскейл";

    // Закладка під GPU-бекенди: 2 одночасно. FFmpeg-scale стерпів би й більше.
    public int MaxConcurrency => 2;

    public IReadOnlyList<StepRequirement> GetRequirements(PipelineContext context)
    {
        var reqs = new List<StepRequirement>();
        if (context.Segments.Count == 0)
            reqs.Add(new StepRequirement("Немає сегментів для апскейлу", IsBlocking: true));
        return reqs;
    }

    public async Task ExecuteAsync(PipelineContext context, IProgressReporter progress, CancellationToken token)
    {
        var options = new UpscaleOptions(_settings.Scale, TargetResolution: null);

        var limit = Math.Min(MaxConcurrency, Math.Max(1, context.Settings.MaxFfmpegConcurrency));
        using var gate = new SemaphoreSlim(limit);

        int done = 0;
        var total = context.Segments.Count;
        var lockObj = new object();

        progress.Log(LogLevel.Info, $"Апскейл {total} сегментів ×{_settings.Scale} ({_provider.DisplayName})");

        // Незалежні сегменти обробляємо паралельно з лімітом (розділ 8.2), як у Нарізці.
        var tasks = context.Segments.Select(async seg =>
        {
            await gate.WaitAsync(token);
            try
            {
                var upscaled = await _provider.UpscaleAsync(seg.FilePath, options, progress, token);
                // Підміняємо шлях на місці: наступні кроки бачать уже збільшений файл.
                lock (lockObj) seg.FilePath = upscaled;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Дрібний збій одного сегмента — лишаємо оригінал, прогон не валимо
                // (розділ 7.1). Склейка нормалізує його разом з рештою.
                progress.Log(LogLevel.Warning, $"{seg.Id}: апскейл пропущено ({ex.Message})");
            }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref done);
                progress.ReportStepProgress($"{n}/{total} сегментів", (double)n / total);
            }
        });

        await Task.WhenAll(tasks);
    }
}
