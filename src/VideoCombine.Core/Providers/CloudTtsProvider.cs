namespace VideoCombine.Core.Providers;

using VideoCombine.Core.Pipeline;

/// <summary>
/// СКЕЛЕТ (розділ 3.6). Хмарний TTS (ElevenLabs/OpenAI) — фінальна якість голосу.
/// IsCloud=true: усередині надсилає текст у HTTP-API й тягне назад аудіо (частина
/// провайдерів — асинхронні: відправ → poll job-id, розділ 3.5).
///
/// Поки ЗАГЛУШКА з ДВОХ причин (див. розмову A→Б):
///   1) нема тексту на вході (гілка "сценарій" — Фаза E);
///   2) нема захищеного сховища API-ключів (окремий слайс; ключі — окремо від
///      рецептів, розділ 9). Мати ключ ≠ мати куди його безпечно покласти.
/// Форма класу фінальна; при переході наповнюємо HTTP-викликом + читаємо ключ зі
/// сховища. Параметри голосу (Stability/Similarity/Style/Speaker Boost — розділ 3.6)
/// різні в кожного провайдера, тож ляжуть окремим розширенням TtsOptions при
/// переході, а не в спільний інтерфейс (там лише мінімум: голос/швидкість/вихід).
/// </summary>
public sealed class CloudTtsProvider : ITtsProvider
{
    private readonly string _providerKey;

    /// <param name="providerKey">Який хмарний сервіс: "elevenlabs" | "openai" | …</param>
    public CloudTtsProvider(string providerKey = "elevenlabs")
    {
        _providerKey = providerKey;
    }

    public string Id => $"cloud:{_providerKey}";
    public string DisplayName => "Хмарний TTS (ElevenLabs / OpenAI)";
    public bool IsCloud => true;

    public Task<string> SynthesizeAsync(
        string text, TtsOptions options, IProgressReporter progress, CancellationToken token)
    {
        // TODO (перехід A→Б): HTTP до API провайдера з ключем зі сховища ОС.
        // Асинхронні провайдери: submit → poll job-id (як заплановано для хмарного
        // апскейлу). Аудіо зберегти в options.OutputPath, повернути шлях.
        throw new NotImplementedException(
            "Хмарний TTS ще не реалізовано — потрібні джерело тексту (Фаза E) і сховище ключів.");
    }
}
