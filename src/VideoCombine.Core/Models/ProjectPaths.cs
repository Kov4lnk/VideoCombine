namespace VideoCombine.Core.Models;

/// <summary>
/// Єдине джерело правди для шляхів проекту (корінь репо, папки work/output/
/// recipes, файл налаштувань). Раніше пошук кореня репо був скопійований у
/// трьох місцях (ExecutionViewModel, ConsoleRunner, RecipesViewModel) — звідси
/// ризик розбіжностей. Тепер усі беруть шляхи звідси.
///
/// ВІДОМЕ ОБМЕЖЕННЯ: корінь шукається по VideoCombine.sln угору по дереву — це
/// працює в dev-режимі (запуск із репо). У запакованому застосунку .sln немає;
/// тоді fallback — поточна тека. Пакування закриє це окремо (своя база шляхів).
/// </summary>
public static class ProjectPaths
{
    /// <summary>Корінь репо (де лежить .sln) або поточна тека як запасний варіант.</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string WorkDir => Path.Combine(RepoRoot, "work");
    public static string OutputDir => Path.Combine(RepoRoot, "output");
    public static string RecipesDir => Path.Combine(RepoRoot, "recipes");
    public static string ToolsDir => Path.Combine(RepoRoot, "tools");

    /// <summary>Файл глобальних налаштувань (машинозалежний, у .gitignore).</summary>
    public static string SettingsFile => Path.Combine(RepoRoot, "settings.json");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VideoCombine.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
