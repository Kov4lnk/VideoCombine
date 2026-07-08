namespace VideoCombine.Core.Providers;

using System.Text;
using System.Text.Json;

/// <summary>
/// Портативна реалізація ISecretStore: секрети лежать у одному JSON-файлі
/// (ім'я ключа → base64 ЗАШИФРОВАНОГО значення). Самі значення на диску ніколи
/// не у відкритому вигляді — шифрування делеговане ISecretProtector (платформна
/// специфіка там, не тут). Тому цей клас — чистий net8.0, живе в Core і тестується
/// без GUI: у тестах підсовуємо фейковий/AES-протектор, у App — DPAPI.
///
/// Формат файлу свідомо простий і стійкий: { "elevenlabs": "&lt;base64&gt;", ... }.
/// Битий/відсутній файл не валить застосунок — читається як порожнє сховище.
///
/// Async-сигнатури (див. ISecretStore): тут робота синхронна (файл+крипто),
/// тож повертаємо Task.FromResult — веб-реалізація буде по-справжньому async,
/// але виклики не міняються. Простий lock захищає файл від паралельних записів
/// (кілька полів UI можуть зберігатись поспіль).
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();

    public FileSecretStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    public Task<string?> GetAsync(string key, CancellationToken token = default)
    {
        lock (_gate)
        {
            var map = Load();
            if (!map.TryGetValue(key, out var b64))
                return Task.FromResult<string?>(null);

            try
            {
                var cipher = Convert.FromBase64String(b64);
                var plain = _protector.Unprotect(cipher);
                return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
            }
            catch
            {
                // Значення зашифроване на іншій машині/юзері (DPAPI-прив'язка) або
                // пошкоджене — трактуємо як "ключа нема", хай користувач введе заново.
                return Task.FromResult<string?>(null);
            }
        }
    }

    public Task SetAsync(string key, string value, CancellationToken token = default)
    {
        lock (_gate)
        {
            var map = Load();
            var cipher = _protector.Protect(Encoding.UTF8.GetBytes(value));
            map[key] = Convert.ToBase64String(cipher);
            Save(map);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken token = default)
    {
        lock (_gate)
        {
            var map = Load();
            if (map.Remove(key))
                Save(map);
        }
        return Task.CompletedTask;
    }

    public Task<bool> HasAsync(string key, CancellationToken token = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Load().ContainsKey(key));
        }
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken token = default)
    {
        lock (_gate)
        {
            IReadOnlyList<string> keys = Load().Keys.ToList();
            return Task.FromResult(keys);
        }
    }

    /// <summary>Читає конверт з диска. Нема файлу / битий — порожнє сховище (не падіння).</summary>
    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void Save(Dictionary<string, string> map)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(map, Options));
    }
}
