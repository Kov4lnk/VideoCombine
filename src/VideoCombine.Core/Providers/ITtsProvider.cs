namespace VideoCombine.Core.Providers;

using VideoCombine.Core.Pipeline;

/// <summary>
/// ПАТЕРН ПРОВАЙДЕРА (розділ 3.6). Спільний інтерфейс "дай текст — поверну аудіо".
/// Реалізації: PiperTtsProvider (локальний, синхронний), CloudTtsProvider
/// (ElevenLabs/OpenAI — хмарний, асинхронний poll).
/// Той самий патерн, що IUpscaleProvider.
///
/// СВІДОМО спроєктовано так, ніби текст і ключі ВЖЕ Є (крок передає готовий текст,
/// провайдер приймає налаштування голосу). Зараз джерела цих даних порожні —
/// текст народжується в гілці "створення з нуля" (Фаза E), а сховище ключів ще не
/// збудоване. Тому реалізації поки заглушки. Коли з'явиться текст + ключі, перехід
/// = "під'єднати труби", а не переписати інтерфейс (див. HANDOFF, розмова про A→Б).
/// </summary>
public interface ITtsProvider
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>true — хмарний (потрібен API-ключ, робить poll job-id); false — локальний.</summary>
    bool IsCloud { get; }

    /// <summary>
    /// Озвучити текст у аудіофайл. Повертає шлях до згенерованого аудіо.
    /// Хмарні реалізації всередині роблять poll job-id (розділ 3.5/3.6).
    /// </summary>
    Task<string> SynthesizeAsync(
        string text,
        TtsOptions options,
        IProgressReporter progress,
        CancellationToken token);
}

/// <summary>
/// Спільні параметри озвучки. Специфічні для провайдера деталі (ElevenLabs:
/// Stability/Similarity/Style/Speaker Boost — розділ 3.6) лягають у розширення
/// пізніше, бо різні провайдери мають різні набори. Тут — спільний мінімум.
/// </summary>
public sealed record TtsOptions(
    string VoiceId,
    string OutputPath,
    double Speed = 1.0);
