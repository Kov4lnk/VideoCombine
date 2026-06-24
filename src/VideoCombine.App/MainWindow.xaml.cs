namespace VideoCombine.App;

using System.Windows;
using VideoCombine.App.ViewModels;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Корінь, що тримає VM усіх табів і дає їм спільний стан.
        DataContext = new MainViewModel();
    }
}
