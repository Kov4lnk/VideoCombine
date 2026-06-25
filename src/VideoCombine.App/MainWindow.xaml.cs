namespace VideoCombine.App;

using System.Linq;
using System.Windows;
using System.Windows.Input;
using VideoCombine.App.ViewModels;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Корінь, що тримає VM усіх табів і дає їм спільний стан.
        DataContext = new MainViewModel();
    }

    // --- Єдине поле джерел: Enter додає посилання, Shift+Enter — новий рядок ---
    // Поле приймає і текст (лінки), і перетягнуті файли. Enter як свідомий жест
    // «додати» замість окремої кнопки. Логіка розбору живе у VM (AddCommand).

    private void SourcesInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        // Shift+Enter — звичайний перенос рядка (ручний набір кількох посилань).
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) return;

        e.Handled = true; // не вставляти зайвий перенос рядка перед очищенням
        if ((sender as FrameworkElement)?.DataContext is SourcesViewModel vm)
            vm.AddCommand.Execute(null);
    }

    // --- Перетягування файлів у поле джерел (drag-and-drop) ---
    // Обробники в code-behind — стандартна практика для WPF DnD: подія несе
    // DataObject з провідника, що погано лягає на чистий біндинг. Сама логіка
    // додавання живе у VM (AddLocalFiles), тут лише дістаємо шляхи.

    private void SourcesDrop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SourcesDrop_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        // e.Handled = true ДО логіки — інакше TextBox вставить шлях як текст у поле.
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        // DataContext таба «Джерела» — SourcesViewModel.
        if ((sender as FrameworkElement)?.DataContext is SourcesViewModel vm)
            vm.AddLocalFiles(paths.Where(System.IO.File.Exists));
    }
}
