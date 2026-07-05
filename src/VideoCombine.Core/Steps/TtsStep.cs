namespace VideoCombine.Core.Steps;

using VideoCombine.Core.Models;
using VideoCombine.Core.Pipeline;
using VideoCombine.Core.Providers;

/// <summary>
/// ОПЦІЙНИЙ крок озвучки (розділ 3.6). Бере текст із context.VoiceoverText,
/// проганяє через обраний ITtsProvider, кладе згенероване аудіо в context.AudioTracks
/// (звідки склейка згодом підмішає голос — це вже перехід A→Б).
///
/// КАРКАС (див. розмову A→Б). Форма кроку, місце в ланцюгу, звітування — фінальні.
/// Але виконати його поки НЕ можна: провайдери — заглушки, а головне — нема тексту
/// на вході (гілка "сценарій" — Фаза E) і сховища ключів для хмари. GetRequirements
/// чесно повертає ці причини як блокуючі, щоб UI світив стан "треба увага" ще в
/// Конструкторі, а не падав посеред прогону (розділ 4.2 / 7.1). Тому в BuildSteps
/// його поки НЕ вставляють — секція в UI видима, але заглушена.
/// </summary>
public sealed class TtsStep : IStep
{
    private readonly ITtsProvider _provider;
    private readonly TtsSettings _settings;

    public TtsStep(ITtsProvider provider, TtsSettings settings)
    {
        _provider = provider;
        _settings = settings;
    }

    public string TypeId => "tts";
    public string DisplayName => "Озвучка";

    // Хмарні API мають ліміт паралельності провайдера; локальний Piper — CPU.
    // Консервативно 1, уточнимо при переході на робочий TTS.
    public int MaxConcurrency => 1;

    public IReadOnlyList<StepRequirement> GetRequirements(PipelineContext context)
    {
        var reqs = new List<StepRequirement>();

        // Головна причина, чому крок поки не виконуваний: нема що озвучувати.
        if (string.IsNullOrWhiteSpace(context.VoiceoverText))
            reqs.Add(new StepRequirement(
                "Нема тексту для озвучки (з'явиться з гілкою сценарію — Фаза E)", IsBlocking: true));

        // Хмара потребує API-ключа зі сховища ОС (окремий слайс, розділ 9).
        if (_provider.IsCloud)
            reqs.Add(new StepRequirement(
                "Хмарний TTS потребує API-ключа (сховище ключів ще не збудоване)", IsBlocking: true));

        return reqs;
    }

    public async Task ExecuteAsync(PipelineContext context, IProgressReporter progress, CancellationToken token)
    {
        // Захист на випадок обходу перевірки: без тексту робити нічого.
        if (string.IsNullOrWhiteSpace(context.VoiceoverText))
            throw new InvalidOperationException("Нема тексту для озвучки");

        var outputPath = System.IO.Path.Combine(context.WorkingDirectory, "voiceover.wav");
        var options = new TtsOptions(_settings.VoiceId, outputPath, _settings.Speed);

        progress.ReportStepProgress($"озвучка ({_provider.DisplayName})", 0);

        // Провайдери поки заглушки (NotImplementedException) — це очікувано до
        // переходу A→Б. Реальний виклик з'явиться без зміни цього кроку.
        var audioPath = await _provider.SynthesizeAsync(
            context.VoiceoverText, options, progress, token);

        context.AudioTracks.Add(new AudioTrack { FilePath = audioPath, Kind = AudioKind.Voiceover });
        progress.ReportStepProgress("готово", 1.0);
        progress.Log(LogLevel.Success, $"Озвучка: {audioPath}");
    }
}

/// <summary>Налаштування озвучки (серіалізуються в JSON-рецепт).</summary>
public sealed class TtsSettings
{
    /// <summary>Чи вставляти крок у ланцюг. Дефолт false — опційний, як апскейл.</summary>
    public bool Enabled { get; set; }

    /// <summary>Який бекенд: "piper" (локально) | "cloud" (ElevenLabs/OpenAI).</summary>
    public string Provider { get; set; } = "piper";

    /// <summary>Ідентифікатор голосу в межах провайдера.</summary>
    public string VoiceId { get; set; } = "";

    /// <summary>Швидкість мовлення (1.0 = звичайна).</summary>
    public double Speed { get; set; } = 1.0;
}
