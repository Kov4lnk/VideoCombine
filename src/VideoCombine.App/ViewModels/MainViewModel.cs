namespace VideoCombine.App.ViewModels;

/// <summary>
/// Корінь, що тримає VM усіх табів і дає їм спільний стан.
/// Поки табів мало — простий контейнер; DI додамо, коли розростеться (Фаза C).
/// «Виконання» бере джерела зі «Sources», тож вони ділять один список.
/// </summary>
public sealed class MainViewModel
{
    public SourcesViewModel Sources { get; }
    public BuilderViewModel Builder { get; }
    public RecipesViewModel Recipes { get; }
    public SettingsViewModel Settings { get; }
    public ExecutionViewModel Execution { get; }

    public MainViewModel()
    {
        Sources = new SourcesViewModel();
        Builder = new BuilderViewModel();
        Recipes = new RecipesViewModel(Sources, Builder);
        Settings = new SettingsViewModel();
        Execution = new ExecutionViewModel(Sources, Builder, Settings);
    }
}
