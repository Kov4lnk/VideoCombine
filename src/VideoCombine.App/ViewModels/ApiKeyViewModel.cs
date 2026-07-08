namespace VideoCombine.App.ViewModels;

using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Один рядок API-ключа на табі «Налаштування» (розділ 9 / 4.3). Тримає лише
/// СТАТУС «встановлено/порожньо» — саме значення ключа сюди НІКОЛИ не потрапляє:
/// воно вводиться в PasswordBox і передається в команду збереження разово, а
/// звідти йде прямо в захищене сховище. Тому тут нема властивості зі значенням.
/// </summary>
public sealed partial class ApiKeyViewModel : ObservableObject
{
    public ApiKeyViewModel(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    /// <summary>Технічний ідентифікатор провайдера у сховищі ("elevenlabs" тощо).</summary>
    public string Key { get; }

    /// <summary>Людська назва для UI.</summary>
    public string DisplayName { get; }

    /// <summary>Чи є збережене значення (визначає статус і колір, без розкриття ключа).</summary>
    [ObservableProperty] private bool _isPresent;

    public string StatusText => IsPresent ? "✓ встановлено" : "порожньо";

    /// <summary>Колір статусу: зелений — встановлено, сірий — порожньо (не помилка).</summary>
    public Brush StatusColor => IsPresent ? Brushes.SeaGreen : Brushes.Gray;

    /// <summary>Оновити стан ззовні (після збереження/стирання чи стартового зчитування).</summary>
    public void SetPresent(bool present) => IsPresent = present;

    partial void OnIsPresentChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
    }
}
