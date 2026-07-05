namespace VideoCombine.Tests;

using VideoCombine.Core.Providers;
using VideoCombine.Core.Recipes;
using VideoCombine.Core.Steps;
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

    [Fact]
    public async Task RecipeMapper_кругообіг_через_файл_зберігає_значення()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vc_test_" + Guid.NewGuid());
        var store = new RecipeStore(dir);

        // Збираємо рецепт із налаштувань...
        var recipe = RecipeMapper.ToRecipe(
            name: "Тест мапера",
            quality: "1080p",
            cut: new CutSettings { Mode = "fixed", MinSec = 4, MaxSec = 10 },
            upscale: new UpscaleSettings { Enabled = true, Scale = 2 },
            assemble: new AssembleSettings { Order = "random", VideoCodec = "nvenc" },
            subtitle: new SubtitleSettings(),
            tts: new TtsSettings());

        // ...і ганяємо через файл — тут значення стають JsonElement.
        await store.SaveAsync(recipe);
        var loaded = await store.LoadAsync(store.ListRecipeFiles().Single());
        Assert.NotNull(loaded);

        // Читання має витягти значення з JsonElement, а не впасти на дефолти.
        Assert.Equal("1080p", RecipeMapper.ReadQuality(loaded!));

        var cut = RecipeMapper.ReadCutSettings(loaded!);
        Assert.Equal("fixed", cut.Mode);
        Assert.Equal(4, cut.MinSec);
        Assert.Equal(10, cut.MaxSec);

        var asm = RecipeMapper.ReadAssembleSettings(loaded!);
        Assert.Equal("random", asm.Order);
        Assert.Equal("nvenc", asm.VideoCodec);

        var up = RecipeMapper.ReadUpscaleSettings(loaded!);
        Assert.True(up.Enabled);
        Assert.Equal(2, up.Scale);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void RecipeMapper_відсутні_ключі_дають_дефолти()
    {
        // Порожній рецепт (старий/урізаний) не повинен валити читання.
        var empty = new Recipe { Name = "Порожній" };

        Assert.Equal("720p", RecipeMapper.ReadQuality(empty));
        var cut = RecipeMapper.ReadCutSettings(empty);
        Assert.Equal(new CutSettings().Mode, cut.Mode);
        Assert.Equal(new CutSettings().MinSec, cut.MinSec);
        var asm = RecipeMapper.ReadAssembleSettings(empty);
        Assert.Equal(new AssembleSettings().VideoCodec, asm.VideoCodec);
    }
}
