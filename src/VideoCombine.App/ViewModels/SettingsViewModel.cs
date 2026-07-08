namespace VideoCombine.App.ViewModels;

using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VideoCombine.App.Security;
using VideoCombine.Core.Models;
using VideoCombine.Core.Providers;

/// <summary>
/// Логіка таба «Налаштування» (розділ 4.3): шляхи до зовнішніх інструментів і
/// папки work/output. Раніше шляхи були захардкоджені у двох BuildSettings —
/// тепер єдине джерело через SettingsStore. Виконання читає збережене звідси.
///
/// Ліміти одночасності й LLM-провайдер поки лише показуємо як довідку (редагування
/// додамо, коли болітиме).
///
/// API-ключі (ElevenLabs/OpenAI/Anthropic — розділ 3.6/3.11) живуть у захищеному
/// ISecretStore, ОКРЕМО від settings.json і рецептів (щоб рецепт можна було класти
/// в Git без витоку — розділ 9). У VM тримаємо лише СТАТУС «встановлено/порожньо»,
/// а не самі значення: введення йде через PasswordBox, значення передається в
/// команду разово й не осідає полем ObservableProperty.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly ISecretStore _secrets;

    public SettingsViewModel()
    {
        _store = new SettingsStore();
        _settings = _store.LoadOrDefault();

        // Сховище API-ключів: портативний FileSecretStore + DPAPI-протектор (Windows).
        // Core знає лише інтерфейс; конкретну реалізацію підсовує App (розділ 6/9).
        _secrets = new FileSecretStore(ProjectPaths.SecretsFile, new DpapiSecretProtector());

        // Розкладаємо у поля для прив'язки.
        _ffmpegPath = _settings.FfmpegPath;
        _ffprobePath = _settings.FfprobePath;
        _ytDlpPath = _settings.YtDlpPath;
        _whisperPath = _settings.WhisperPath ?? "";
        _whisperModelPath = _settings.WhisperModelPath ?? "";
        _workingRoot = _settings.WorkingRoot;
        _outputRoot = _settings.OutputRoot;

        // Початковий стан «встановлено/порожньо» для кожного ключа (значення не читаємо).
        RefreshKeyStatuses();
    }

    [ObservableProperty] private string _ffmpegPath;
    [ObservableProperty] private string _ffprobePath;
    [ObservableProperty] private string _ytDlpPath;
    // Whisper (субтитри) — exe (whisper.cpp) + файл моделі (ggml-*.bin). Опційні:
    // потрібні лише коли ввімкнено крок субтитрів, тож порожні тут — не помилка прогону.
    [ObservableProperty] private string _whisperPath;
    [ObservableProperty] private string _whisperModelPath;
    [ObservableProperty] private string _workingRoot;
    [ObservableProperty] private string _outputRoot;

    /// <summary>Короткий статус останньої дії.</summary>
    [ObservableProperty] private string _status = "";

    // --- Жива валідація полів (оновлюється при наборі) ---
    // Для exe: ✓ якщо файл існує або голе ім'я в PATH, ✕ якщо не знайдено.
    // Для папок: ✓ якщо існує, інакше нейтральне «буде створено» (не помилка —
    // застосунок створює work/output сам перед прогоном).

    public string FfmpegStatus => ToolStatus(FfmpegPath);
    public Brush FfmpegStatusColor => ToolColor(FfmpegPath);
    public string FfprobeStatus => ToolStatus(FfprobePath);
    public Brush FfprobeStatusColor => ToolColor(FfprobePath);
    public string YtDlpStatus => ToolStatus(YtDlpPath);
    public Brush YtDlpStatusColor => ToolColor(YtDlpPath);
    // Whisper exe опційний: без нього просто не ввімкнеш субтитри — тож порожньо
    // НЕ червоне (на відміну від обов'язкових ffmpeg/yt-dlp), а нейтральна підказка.
    public string WhisperStatus => OptionalToolStatus(WhisperPath);
    public Brush WhisperStatusColor => OptionalToolColor(WhisperPath);
    // Модель — конкретний файл (ggml-*.bin), не команда в PATH: перевіряємо File.Exists.
    public string WhisperModelStatus => FileStatus(WhisperModelPath);
    public Brush WhisperModelStatusColor => FileColor(WhisperModelPath);

    public string WorkingStatus => FolderStatus(WorkingRoot);
    public Brush WorkingStatusColor => FolderColor(WorkingRoot);
    public string OutputStatus => FolderStatus(OutputRoot);
    public Brush OutputStatusColor => FolderColor(OutputRoot);

    private static string ToolStatus(string path) =>
        string.IsNullOrWhiteSpace(path) ? "✕ не вказано"
        : ToolChecker.ToolPresent(path) ? "✓ знайдено"
        : "✕ не знайдено";

    private static Brush ToolColor(string path) =>
        ToolChecker.ToolPresent(path) ? Brushes.SeaGreen : Brushes.Firebrick;

    // Як ToolStatus, але порожньо — нейтрально (інструмент опційний, напр. whisper).
    private static string OptionalToolStatus(string path) =>
        string.IsNullOrWhiteSpace(path) ? "не вказано (потрібен для субтитрів)"
        : ToolChecker.ToolPresent(path) ? "✓ знайдено"
        : "✕ не знайдено";

    private static Brush OptionalToolColor(string path) =>
        string.IsNullOrWhiteSpace(path) ? Brushes.Gray
        : ToolChecker.ToolPresent(path) ? Brushes.SeaGreen
        : Brushes.Firebrick;

    private static string FolderStatus(string path) =>
        string.IsNullOrWhiteSpace(path) ? "✕ не вказано"
        : Directory.Exists(path) ? "✓ існує"
        : "буде створено";

    private static Brush FolderColor(string path) =>
        string.IsNullOrWhiteSpace(path) ? Brushes.Firebrick
        : Directory.Exists(path) ? Brushes.SeaGreen
        : Brushes.Gray;

    // Для необов'язкового файлу (модель Whisper): порожньо — нейтрально (крок просто
    // не ввімкнеш), вказано+існує — зелено, вказано+нема — червоно.
    private static string FileStatus(string path) =>
        string.IsNullOrWhiteSpace(path) ? "не вказано (потрібна для субтитрів)"
        : File.Exists(path) ? "✓ знайдено"
        : "✕ не знайдено";

    private static Brush FileColor(string path) =>
        string.IsNullOrWhiteSpace(path) ? Brushes.Gray
        : File.Exists(path) ? Brushes.SeaGreen
        : Brushes.Firebrick;

    // Поле шляху змінилось → оновити його статус-значок (генеровані OnXxxChanged).
    partial void OnFfmpegPathChanged(string value)
    {
        OnPropertyChanged(nameof(FfmpegStatus));
        OnPropertyChanged(nameof(FfmpegStatusColor));
    }

    partial void OnFfprobePathChanged(string value)
    {
        OnPropertyChanged(nameof(FfprobeStatus));
        OnPropertyChanged(nameof(FfprobeStatusColor));
    }

    partial void OnYtDlpPathChanged(string value)
    {
        OnPropertyChanged(nameof(YtDlpStatus));
        OnPropertyChanged(nameof(YtDlpStatusColor));
    }

    partial void OnWhisperPathChanged(string value)
    {
        OnPropertyChanged(nameof(WhisperStatus));
        OnPropertyChanged(nameof(WhisperStatusColor));
    }

    partial void OnWhisperModelPathChanged(string value)
    {
        OnPropertyChanged(nameof(WhisperModelStatus));
        OnPropertyChanged(nameof(WhisperModelStatusColor));
    }

    partial void OnWorkingRootChanged(string value)
    {
        OnPropertyChanged(nameof(WorkingStatus));
        OnPropertyChanged(nameof(WorkingStatusColor));
    }

    partial void OnOutputRootChanged(string value)
    {
        OnPropertyChanged(nameof(OutputStatus));
        OnPropertyChanged(nameof(OutputStatusColor));
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            _settings.FfmpegPath = FfmpegPath.Trim();
            _settings.FfprobePath = FfprobePath.Trim();
            _settings.YtDlpPath = YtDlpPath.Trim();
            _settings.WhisperPath = NullIfBlank(WhisperPath);
            _settings.WhisperModelPath = NullIfBlank(WhisperModelPath);
            _settings.WorkingRoot = WorkingRoot.Trim();
            _settings.OutputRoot = OutputRoot.Trim();

            _store.Save(_settings);
            Status = "Збережено. Застосується при наступному прогоні.";
        }
        catch (Exception ex)
        {
            Status = $"Помилка збереження: {ex.Message}";
        }
    }

    // --- Огляд (нативні діалоги вибору файлу/папки) ---
    // Діалоги — App-рівень; SettingsViewModel теж у App, тож правило "Core не знає
    // App" не порушується. По команді на поле — біндинг чистіший за параметризовану.

    [RelayCommand]
    private void BrowseFfmpeg() => FfmpegPath = PickExe("Виберіть ffmpeg.exe", FfmpegPath) ?? FfmpegPath;

    [RelayCommand]
    private void BrowseFfprobe() => FfprobePath = PickExe("Виберіть ffprobe.exe", FfprobePath) ?? FfprobePath;

    [RelayCommand]
    private void BrowseYtDlp() => YtDlpPath = PickExe("Виберіть yt-dlp.exe", YtDlpPath) ?? YtDlpPath;

    [RelayCommand]
    private void BrowseWhisper() => WhisperPath = PickExe("Виберіть whisper.cpp (main.exe / whisper-cli.exe)", WhisperPath) ?? WhisperPath;

    [RelayCommand]
    private void BrowseWhisperModel() => WhisperModelPath = PickModel("Виберіть модель Whisper (ggml-*.bin)", WhisperModelPath) ?? WhisperModelPath;

    [RelayCommand]
    private void BrowseWorking() => WorkingRoot = PickFolder("Виберіть робочу папку", WorkingRoot) ?? WorkingRoot;

    [RelayCommand]
    private void BrowseOutput() => OutputRoot = PickFolder("Виберіть папку виводу", OutputRoot) ?? OutputRoot;

    /// <summary>Діалог вибору .exe. Стартує з теки поточного значення, якщо є.</summary>
    private static string? PickExe(string title, string current)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "Програми (*.exe)|*.exe|Усі файли (*.*)|*.*",
            CheckFileExists = true
        };
        SetInitialDir(d => dlg.InitialDirectory = d, current, isFile: true);
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>Діалог вибору файлу моделі Whisper (ggml-*.bin). Стартує з теки поточного значення.</summary>
    private static string? PickModel(string title, string current)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "Моделі Whisper (*.bin)|*.bin|Усі файли (*.*)|*.*",
            CheckFileExists = true
        };
        SetInitialDir(d => dlg.InitialDirectory = d, current, isFile: true);
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>Діалог вибору папки (OpenFolderDialog — .NET 8+, без WinForms).</summary>
    private static string? PickFolder(string title, string current)
    {
        var dlg = new OpenFolderDialog { Title = title };
        SetInitialDir(d => dlg.InitialDirectory = d, current, isFile: false);
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    /// <summary>Стартова тека діалогу з поточного значення, якщо воно існує.</summary>
    private static void SetInitialDir(Action<string> set, string current, bool isFile)
    {
        if (string.IsNullOrWhiteSpace(current)) return;
        var dir = isFile ? Path.GetDirectoryName(current) : current;
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            set(dir);
    }

    /// <summary>Порожнє поле → null (опційні шляхи Whisper зберігаємо як null, не "").</summary>
    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // --- API-ключі (розділ 9 / 4.3) ---
    // Ключі живуть у захищеному сховищі (FileSecretStore + DPAPI), НЕ в settings.json
    // і НЕ в рецептах. VM тримає лише СТАТУС «встановлено/порожньо», ніколи не саме
    // значення: введене з PasswordBox передається разово в команду й одразу зникає.
    // Кожен рядок — окрема ApiKeyViewModel; додати провайдера = один запис у списку.

    /// <summary>Рядки ключів для UI (ElevenLabs / OpenAI / Anthropic).</summary>
    public IReadOnlyList<ApiKeyViewModel> ApiKeys { get; } = new List<ApiKeyViewModel>
    {
        new("elevenlabs", "ElevenLabs (озвучка)"),
        new("openai",     "OpenAI (GPT / TTS)"),
        new("anthropic",  "Anthropic (Claude)"),
    };

    /// <summary>Оновити «встановлено/порожньо» для всіх ключів (значень не читаємо).</summary>
    private async void RefreshKeyStatuses()
    {
        var present = await _secrets.ListKeysAsync();
        var set = new HashSet<string>(present);
        foreach (var k in ApiKeys)
            k.SetPresent(set.Contains(k.Key));
    }

    /// <summary>Зберегти введений ключ у сховище. value приходить із PasswordBox разово.</summary>
    [RelayCommand]
    private async Task SaveKey((ApiKeyViewModel Row, string Value) arg)
    {
        var value = arg.Value?.Trim() ?? "";
        if (value.Length == 0)
        {
            Status = $"Порожнє значення — ключ «{arg.Row.DisplayName}» не змінено.";
            return;
        }

        try
        {
            await _secrets.SetAsync(arg.Row.Key, value);
            arg.Row.SetPresent(true);
            Status = $"Ключ «{arg.Row.DisplayName}» збережено.";
        }
        catch (Exception ex)
        {
            Status = $"Не вдалось зберегти ключ: {ex.Message}";
        }
    }

    /// <summary>Стерти ключ зі сховища.</summary>
    [RelayCommand]
    private async Task ClearKey(ApiKeyViewModel row)
    {
        try
        {
            await _secrets.DeleteAsync(row.Key);
            row.SetPresent(false);
            Status = $"Ключ «{row.DisplayName}» стерто.";
        }
        catch (Exception ex)
        {
            Status = $"Не вдалось стерти ключ: {ex.Message}";
        }
    }

    /// <summary>Поточні налаштування для побудови пайплайну (читає Виконання).</summary>
    public AppSettings ToAppSettings()
    {
        _settings.FfmpegPath = FfmpegPath.Trim();
        _settings.FfprobePath = FfprobePath.Trim();
        _settings.YtDlpPath = YtDlpPath.Trim();
        _settings.WhisperPath = NullIfBlank(WhisperPath);
        _settings.WhisperModelPath = NullIfBlank(WhisperModelPath);
        _settings.WorkingRoot = WorkingRoot.Trim();
        _settings.OutputRoot = OutputRoot.Trim();
        return _settings;
    }
}
