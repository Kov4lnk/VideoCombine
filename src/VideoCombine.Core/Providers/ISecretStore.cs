namespace VideoCombine.Core.Providers;

/// <summary>
/// Захищене сховище API-ключів (розділ 9 / 4.3). Ключі НІКОЛИ не лежать у
/// відкритому вигляді на диску й НЕ потрапляють у JSON-рецепти — щоб рецепт
/// можна було копіювати/класти в Git без витоку.
///
/// ПАТЕРН ПРОВАЙДЕРА (як ITtsProvider/IUpscaleProvider): це інтерфейс, а не
/// реалізація. Поточна реалізація — файл на машині користувача (FileSecretStore
/// + DPAPI-протектор із App). Двері свідомо відчинені під майбутнє БЕЗ переробки
/// викликів:
///   • macOS/Linux — інша реалізація протектора (Keychain / AES-пароль);
///   • веб — зовсім інша реалізація ISecretStore (секрети живуть на СЕРВЕРІ:
///     Key Vault / Secrets Manager / env; клієнт ключів не бачить взагалі).
///
/// Тому сигнатури АСИНХРОННІ: локальний файл читається миттєво (Task.FromResult),
/// але серверний секрет-менеджер — це мережевий виклик. Async зараз = веб-перехід
/// не ламає жоден виклик пізніше.
///
/// Значення віддається назовні лише за явним GetAsync(ключ) — сховище не має
/// методу «дай усе», щоб секрети не текли гуртом у логи/UI.
/// </summary>
public interface ISecretStore
{
    /// <summary>Значення секрету або null, якщо ключа нема.</summary>
    Task<string?> GetAsync(string key, CancellationToken token = default);

    /// <summary>Записати/оновити секрет під ключем.</summary>
    Task SetAsync(string key, string value, CancellationToken token = default);

    /// <summary>Видалити секрет. Ідемпотентно: нема ключа — просто нічого.</summary>
    Task DeleteAsync(string key, CancellationToken token = default);

    /// <summary>Чи є значення під ключем (для UI-статусу «встановлено», без розкриття значення).</summary>
    Task<bool> HasAsync(string key, CancellationToken token = default);

    /// <summary>Імена наявних ключів (НЕ значення) — для показу в UI, чого бракує.</summary>
    Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken token = default);
}
