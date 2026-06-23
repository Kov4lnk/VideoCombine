namespace VideoCombine.App;

using System.Windows;
using VideoCombine.App.ViewModels;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Поки живий лише таб «Виконання» — його VM як DataContext вікна.
        // Коли табів стане більше (Фаза C), перейдемо на окремі VM per-tab.
        DataContext = new ExecutionViewModel();
    }
}
