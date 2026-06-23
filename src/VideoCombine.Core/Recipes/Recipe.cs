namespace VideoCombine.Core.Recipes;

/// <summary>
/// Рецепт пайплайну — зберігається у JSON (розділ 5).
/// Людиночитабельний, копіюється, кладеться в Git. БЕЗ API-ключів.
/// Кожен крок пише лише СВІЙ блок налаштувань — додавання нових кроків
/// не ламає старі рецепти.
/// </summary>
public sealed class Recipe
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>"video" (переробка) або "idea" (генеративна гілка) — розділ 1.1.</summary>
    public string StartBranch { get; set; } = "video";

    /// <summary>Кроки в порядку виконання. Позиція зберігається тут (розділ 4.1).</summary>
    public List<RecipeStep> Steps { get; set; } = new();
}

/// <summary>Один крок у рецепті: тип, увімкненість, і вільний блок налаштувань.</summary>
public sealed class RecipeStep
{
    public required string TypeId { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Налаштування саме цього кроку як вільний словник
    /// (серіалізується/десеріалізується кроком). Напр. для нарізки:
    /// { "mode": "random", "minSec": 3, "maxSec": 8 }.
    /// </summary>
    public Dictionary<string, object?> Settings { get; set; } = new();
}
