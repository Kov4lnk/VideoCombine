namespace VideoCombine.Core.Models;

using System.Text.Json;

/// <summary>
/// Завантажує/зберігає глобальні AppSettings як JSON (розділ 4.3). Єдина точка,
/// що знає, ЗВІДКИ беруться шляхи. Пріоритет значень: збережений settings.json →
/// env-змінні (VC_FFMPEG тощо) → захардкоджені дефолти. Env лишаємо як запасний
/// шлях, щоб консольний ранер і headless-прогони працювали без файлу налаштувань.
///
/// API-ключі/секрети тут НЕ зберігаємо — окрема захищена тема (розділ 9).
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;

    public SettingsStore(string? path = null) => _path = path ?? ProjectPaths.SettingsFile;

    /// <summary>
    /// Читає налаштування з файлу; за відсутності/помилки — будує дефолти з env.
    /// Порожні рядкові поля (шляхи/папки) теж добиваємо дефолтами, щоб збережений
    /// колись файл із пустим значенням не лишив прогін без ffmpeg.
    /// </summary>
    public AppSettings LoadOrDefault()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            settings = new AppSettings(); // битий файл не має валити старт
        }

        FillDefaults(settings);
        return settings;
    }

    public void Save(AppSettings settings) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));

    /// <summary>Дефолти з env або захардкоджені — лише для порожніх полів.</summary>
    private static void FillDefaults(AppSettings s)
    {
        if (IsBlank(s.FfmpegPath) || s.FfmpegPath == "ffmpeg")
            s.FfmpegPath = Env("VC_FFMPEG", @"C:\ffmpeg\bin\ffmpeg.exe");
        if (IsBlank(s.FfprobePath) || s.FfprobePath == "ffprobe")
            s.FfprobePath = Env("VC_FFPROBE", @"C:\ffmpeg\bin\ffprobe.exe");
        if (IsBlank(s.YtDlpPath) || s.YtDlpPath == "yt-dlp")
            s.YtDlpPath = Env("VC_YTDLP", Path.Combine(ProjectPaths.ToolsDir, "yt-dlp.exe"));

        // Whisper (субтитри) — ОПЦІЙНИЙ. На відміну від yt-dlp/ffmpeg, дефолт із
        // tools/whisper/ підставляємо ЛИШЕ якщо файли реально там: на машині без
        // whisper поле має лишитись порожнім (нейтральний статус у UI), а не вказувати
        // на неіснуючий шлях червоним. Env-змінні мають пріоритет, якщо задані.
        if (IsBlank(s.WhisperPath))
        {
            var envWhisper = Environment.GetEnvironmentVariable("VC_WHISPER");
            var defWhisper = Path.Combine(ProjectPaths.ToolsDir, "whisper", "whisper-cli.exe");
            if (!IsBlank(envWhisper)) s.WhisperPath = envWhisper;
            else if (File.Exists(defWhisper)) s.WhisperPath = defWhisper;
        }
        if (IsBlank(s.WhisperModelPath))
        {
            var envModel = Environment.GetEnvironmentVariable("VC_WHISPER_MODEL");
            var defModel = Path.Combine(ProjectPaths.ToolsDir, "whisper", "models", "ggml-small.bin");
            if (!IsBlank(envModel)) s.WhisperModelPath = envModel;
            else if (File.Exists(defModel)) s.WhisperModelPath = defModel;
        }

        if (IsBlank(s.WorkingRoot))
            s.WorkingRoot = Env("VC_WORK", ProjectPaths.WorkDir);
        if (IsBlank(s.OutputRoot))
            s.OutputRoot = Env("VC_OUTPUT", ProjectPaths.OutputDir);
    }

    private static bool IsBlank(string? v) => string.IsNullOrWhiteSpace(v);

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
}
