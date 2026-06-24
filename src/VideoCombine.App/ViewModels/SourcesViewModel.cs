namespace VideoCombine.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>
/// Логіка таба «Джерела» (розділ 3.1): єдине поле вставки (одне посилання
/// або пачка по рядках), список доданих із валідацією + дедуплікацією,
/// вибір якості. Те, що тут зібрано, споживає таб «Виконання».
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

    /// <summary>Лише валідні посилання — для передачі в пайплайн.</summary>
    public IReadOnlyList<string> GetUsableUrls() =>
        Sources.Where(s => s.IsUsable).Select(s => s.Url).ToList();

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
