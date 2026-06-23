namespace VideoCombine.Core.Recipes;

using System.Text.Json;

/// <summary>Зберігає й читає рецепти як JSON-файли з папки рецептів (розділ 5).</summary>
public sealed class RecipeStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _folder;

    public RecipeStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(_folder);
    }

    public async Task SaveAsync(Recipe recipe, CancellationToken token = default)
    {
        var safeName = string.Concat(recipe.Name.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(_folder, safeName + ".json");
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, recipe, Options, token);
    }

    public async Task<Recipe?> LoadAsync(string path, CancellationToken token = default)
    {
        if (!File.Exists(path)) return null;
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Recipe>(fs, Options, token);
    }

    public IEnumerable<string> ListRecipeFiles() =>
        Directory.EnumerateFiles(_folder, "*.json");
}
