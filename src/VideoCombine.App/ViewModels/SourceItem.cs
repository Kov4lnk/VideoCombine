namespace VideoCombine.App.ViewModels;

using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Статус рядка-джерела у списку (розділ 3.1: валідація з підсвіткою).</summary>
public enum SourceStatus
{
    Valid,      // схоже на посилання, не дубль
    Duplicate,  // вже є в списку
    Invalid     // не схоже на URL
}

/// <summary>
/// Один доданий рядок на табі «Джерела»: посилання + його статус.
/// ObservableObject — щоб підсвітка оновлювалась, якщо статус зміниться
/// (напр. дубль став єдиним після видалення копії).
/// </summary>
public sealed partial class SourceItem : ObservableObject
{
    public required string Url { get; init; }

    [ObservableProperty] private SourceStatus _status;

    /// <summary>Текст-пояснення статусу праворуч від рядка.</summary>
    public string StatusText => Status switch
    {
        SourceStatus.Valid => "✓",
        SourceStatus.Duplicate => "⚠ дубль",
        SourceStatus.Invalid => "✕ не схоже на посилання",
        _ => ""
    };

    public Brush StatusColor => Status switch
    {
        SourceStatus.Valid => Brushes.SeaGreen,
        SourceStatus.Duplicate => Brushes.DarkGoldenrod,
        SourceStatus.Invalid => Brushes.Firebrick,
        _ => Brushes.Gray
    };

    /// <summary>Лише валідні (не дублі, не биті) йдуть у пайплайн.</summary>
    public bool IsUsable => Status == SourceStatus.Valid;

    // Статус змінився → оновити похідні (текст/колір) для прив'язки.
    partial void OnStatusChanged(SourceStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(IsUsable));
    }
}
