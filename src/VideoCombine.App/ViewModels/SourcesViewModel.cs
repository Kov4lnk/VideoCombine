namespace VideoCombine.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

/// <summary>
/// Логіка таба «Джерела» (розділ 3.1): єдине поле вставки (одне посилання
/// або пачка по рядках) + локальні файли з диска («З диска»), список доданих
/// із валідацією + дедуплікацією, вибір якості. Те, що тут зібрано, споживає
/// таб «Виконання».
///
/// Свідомо поки БЕЗ: мініатюр, drag-and-drop файлів, перемикача гілки
/// «відео/ідея», кнопки «Проаналізувати все» — це пізніші кроки (Фаза C/E).
/// </summary>
public sealed partial class SourcesViewModel : ObservableObject
{
    /// <summary>Текст із поля вставки (приймає кілька рядків).</summary>
    [ObservableProperty] private string _inputText = "";

    /// <summary>Обрана якість завантаження. Дефолт 720p (баланс якості й швидкості).</summary>
    [ObservableProperty] private string _quality = "720p";

    /// <summary>Варіанти для дропдауна якості.</summary>
    public IReadOnlyList<string> QualityOptions { get; } =
        new[] { "Найкраща", "1080p", "720p", "480p" };

    public ObservableCollection<SourceItem> Sources { get; } = new();

    [RelayCommand]
    private void Add()
    {
        var lines = InputText.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            var status = Classify(line);
            Sources.Add(new SourceItem { Url = line, Status = status });
        }

        InputText = "";
        RestartCommandsState();
    }

    /// <summary>
    /// Додати локальні відеофайли з диска через діалог. Делегує в AddLocalFiles —
    /// та сама логіка, що й для перетягування (drag-and-drop).
    /// </summary>
    [RelayCommand]
    private void AddFromDisk()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Виберіть відеофайли",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "Відео (*.mp4;*.mkv;*.mov;*.webm;*.avi)|*.mp4;*.mkv;*.mov;*.webm;*.avi|Усі файли (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        AddLocalFiles(dlg.FileNames);
    }

    /// <summary>
    /// Додати локальні файли (з діалогу або перетягуванням у поле). Дублі за
    /// повним шляхом пропускаємо. Локальні джерела завжди Valid (файл реальний),
    /// окремий тип SourceKind.LocalFile — не валідуються як URL.
    /// </summary>
    public void AddLocalFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var exists = Sources.Any(s =>
                s.Kind == SourceKind.LocalFile &&
                string.Equals(s.Url, path, StringComparison.OrdinalIgnoreCase));
            if (exists) continue;

            Sources.Add(new SourceItem
            {
                Url = path,
                Kind = SourceKind.LocalFile,
                Status = SourceStatus.Valid
            });
        }

        RestartCommandsState();
    }

    [RelayCommand]
    private void Remove(SourceItem? item)
    {
        if (item is null) return;
        Sources.Remove(item);
        // Видалення копії може «звільнити» дубль — перерахувати статуси.
        Revalidate();
    }

    [RelayCommand]
    private void Clear()
    {
        Sources.Clear();
        RestartCommandsState();
    }

    /// <summary>Лише валідні URL-джерела — на завантаження yt-dlp.</summary>
    public IReadOnlyList<string> GetUsableUrls() =>
        Sources.Where(s => s.Kind == SourceKind.Url && s.IsUsable).Select(s => s.Url).ToList();

    /// <summary>Локальні файли — у пайплайн без завантаження.</summary>
    public IReadOnlyList<string> GetLocalFiles() =>
        Sources.Where(s => s.Kind == SourceKind.LocalFile).Select(s => s.Url).ToList();

    /// <summary>Чи є хоч одне придатне джерело (URL або локальний файл) — для Старту.</summary>
    public bool HasUsableSources() =>
        Sources.Any(s => s.Kind == SourceKind.LocalFile || s.IsUsable);

    /// <summary>Якість у форматі, який розуміє DownloadSettings ("best"/"1080p"/...).</summary>
    public string GetQualityValue() =>
        Quality == "Найкраща" ? "best" : Quality;

    /// <summary>
    /// Зворотне до GetQualityValue: виставляє Quality з внутрішнього значення
    /// рецепта ("best"→"Найкраща"). Невідоме значення лишає поточну якість.
    /// </summary>
    public void SetQualityFromValue(string value)
    {
        var display = value == "best" ? "Найкраща" : value;
        if (QualityOptions.Contains(display))
            Quality = display;
    }

    // --- Валідація ---

    /// <summary>
    /// Класифікує рядок: дубль (уже є валідний такий самий) → Duplicate;
    /// не схоже на URL → Invalid; інакше Valid.
    /// Базова перевірка URL — навмисно проста; yt-dlp скаже точніше при завантаженні.
    /// </summary>
    private SourceStatus Classify(string url)
    {
        if (!LooksLikeUrl(url))
            return SourceStatus.Invalid;

        var isDuplicate = Sources.Any(s =>
            string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));
        return isDuplicate ? SourceStatus.Duplicate : SourceStatus.Valid;
    }

    private void Revalidate()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Sources)
        {
            // Локальні файли не валідуємо як URL — їхній статус сталий.
            if (item.Kind == SourceKind.LocalFile) continue;

            if (!LooksLikeUrl(item.Url))
            {
                item.Status = SourceStatus.Invalid;
                continue;
            }
            item.Status = seen.Add(item.Url) ? SourceStatus.Valid : SourceStatus.Duplicate;
        }
    }

    /// <summary>Дуже базова перевірка: http(s) і є крапка в хості.</summary>
    private static bool LooksLikeUrl(string s)
    {
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        return uri.Host.Contains('.');
    }

    private void RestartCommandsState() { /* зарезервовано: оновлення CanExecute, коли з'являться */ }
}
