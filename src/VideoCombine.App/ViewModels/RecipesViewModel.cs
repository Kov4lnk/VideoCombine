namespace VideoCombine.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoCombine.Core.Recipes;

/// <summary>
/// Збереження/завантаження рецептів на табі «Конструктор» (розділ 5).
/// Оркеструє Sources (якість) + Builder (нарізка/склейка) ↔ Recipe через
/// RecipeStore. Сам мапінг живе в Core (RecipeMapper) і покритий тестами;
/// тут лише UI-обв'язка: ім'я, список файлів, команди.
/// </summary>
public sealed partial class RecipesViewModel : ObservableObject
{
    private readonly SourcesViewModel _sources;
    private readonly BuilderViewModel _builder;
    private readonly RecipeStore _store;

    public RecipesViewModel(SourcesViewModel sources, BuilderViewModel builder)
    {
        _sources = sources;
        _builder = builder;
        _store = new RecipeStore(GetRecipesFolder());
        RefreshList();
    }

    /// <summary>Ім'я для збереження поточного рецепта.</summary>
    [ObservableProperty] private string _recipeName = "";

    /// <summary>Обраний у списку файл рецепта (повний шлях).</summary>
    [ObservableProperty] private RecipeFile? _selectedRecipe;

    /// <summary>Короткий статус останньої дії (зберегли/завантажили/помилка).</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>Збережені рецепти (файли з папки рецептів).</summary>
    public ObservableCollection<RecipeFile> Recipes { get; } = new();

    private bool CanSave() => !string.IsNullOrWhiteSpace(RecipeName);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        try
        {
            var recipe = RecipeMapper.ToRecipe(
                RecipeName.Trim(),
                _sources.GetQualityValue(),
                _builder.BuildCutSettings(),
                _builder.BuildAssembleSettings());

            await _store.SaveAsync(recipe);
            RefreshList();
            Status = $"Збережено: {recipe.Name}";
        }
        catch (Exception ex)
        {
            Status = $"Помилка збереження: {ex.Message}";
        }
    }

    /// <summary>
    /// Завантажує обраний рецепт у таби Джерела+Конструктор. Викликається авто-
    /// матично при виборі в дропдауні (тулбар-патерн: вибір = застосувати).
    /// </summary>
    private async Task LoadAsync(RecipeFile file)
    {
        try
        {
            var recipe = await _store.LoadAsync(file.Path);
            if (recipe is null)
            {
                Status = "Не вдалося прочитати рецепт";
                return;
            }

            // Заливаємо в обидва таби, з яких рецепт зібрано.
            _sources.SetQualityFromValue(RecipeMapper.ReadQuality(recipe));
            _builder.Apply(RecipeMapper.ReadCutSettings(recipe), RecipeMapper.ReadAssembleSettings(recipe));

            RecipeName = recipe.Name;
            Status = $"Завантажено: {recipe.Name}";
        }
        catch (Exception ex)
        {
            Status = $"Помилка завантаження: {ex.Message}";
        }
    }

    private void RefreshList()
    {
        Recipes.Clear();
        foreach (var path in _store.ListRecipeFiles())
            Recipes.Add(new RecipeFile(path));
    }

    // Команди-залежності: оновити доступність кнопок, коли змінюється стан.
    partial void OnRecipeNameChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    // Вибір у дропдауні = застосувати рецепт (тулбар-патерн, без окремої кнопки).
    // _ = бо подія синхронна, а завантаження асинхронне (fire-and-forget, помилки
    // ловить сам LoadAsync і пише в Status).
    partial void OnSelectedRecipeChanged(RecipeFile? value)
    {
        if (value is not null) _ = LoadAsync(value);
    }

    /// <summary>
    /// Папка рецептів — recipes/ поряд із work/output. ПРИМІТКА: пошук кореня
    /// репо тут дублює ExecutionViewModel і ConsoleRunner — це борг, який прибере
    /// слайс «Налаштування» (єдине джерело шляхів).
    /// </summary>
    private static string GetRecipesFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VideoCombine.sln")))
                return Path.Combine(dir.FullName, "recipes");
            dir = dir.Parent;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "recipes");
    }
}

/// <summary>Рядок у списку збережених рецептів: повний шлях + ім'я для показу.</summary>
public sealed class RecipeFile
{
    public RecipeFile(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public string Path { get; }
    public string Name { get; }
}
