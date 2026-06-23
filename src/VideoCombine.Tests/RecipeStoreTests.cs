namespace VideoCombine.Tests;

using VideoCombine.Core.Recipes;
using Xunit;

public sealed class RecipeStoreTests
{
    [Fact]
    public async Task Recipe_зберігається_і_читається_назад()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vc_test_" + Guid.NewGuid());
        var store = new RecipeStore(dir);

        var recipe = new Recipe
        {
            Name = "Шортси з лекцій",
            StartBranch = "video",
            Steps =
            {
                new RecipeStep { TypeId = "download", Settings = { ["quality"] = "720p" } },
                new RecipeStep { TypeId = "cut", Settings = { ["mode"] = "random", ["minSec"] = 3, ["maxSec"] = 8 } },
                new RecipeStep { TypeId = "assemble", Settings = { ["order"] = "random" } }
            }
        };

        await store.SaveAsync(recipe);
        var file = store.ListRecipeFiles().Single();
        var loaded = await store.LoadAsync(file);

        Assert.NotNull(loaded);
        Assert.Equal("Шортси з лекцій", loaded!.Name);
        Assert.Equal(3, loaded.Steps.Count);
        Assert.Equal("cut", loaded.Steps[1].TypeId);

        Directory.Delete(dir, recursive: true);
    }
}
