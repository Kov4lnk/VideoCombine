namespace VideoCombine.Tests;

using System.Text;
using VideoCombine.Core.Providers;
using Xunit;

/// <summary>
/// Тести FileSecretStore — портативної начинки сховища ключів. Головна цінність
/// розділення (ISecretProtector окремо від сховища) саме тут: Core тестується БЕЗ
/// DPAPI/GUI, підсунувши фейковий протектор. Перевіряємо round-trip, ізоляцію
/// значень, стійкість до битого файлу і — головне для секретів — що значення на
/// диску НЕ у відкритому вигляді.
///
/// Кожен тест — власний тимчасовий файл (ізоляція).
/// </summary>
public sealed class FileSecretStoreTests
{
    /// <summary>Фейковий протектор: оборотний XOR. Не крипто, лише симетрія Protect/Unprotect
    /// + гарантія, що байти на диску відрізняються від відкритих (як робить справжній DPAPI).</summary>
    private sealed class XorProtector : ISecretProtector
    {
        private const byte Mask = 0x5A;
        public byte[] Protect(byte[] plaintext) => Xor(plaintext);
        public byte[] Unprotect(byte[] ciphertext) => Xor(ciphertext);
        private static byte[] Xor(byte[] data)
        {
            var r = new byte[data.Length];
            for (var i = 0; i < data.Length; i++) r[i] = (byte)(data[i] ^ Mask);
            return r;
        }
    }

    [Fact]
    public async Task Set_потім_Get_повертає_значення()
    {
        var path = TempPath();
        var store = new FileSecretStore(path, new XorProtector());

        await store.SetAsync("elevenlabs", "sk-secret-123");

        Assert.Equal("sk-secret-123", await store.GetAsync("elevenlabs"));
        Cleanup(path);
    }

    [Fact]
    public async Task Get_неіснуючого_ключа_дає_null()
    {
        var path = TempPath();
        var store = new FileSecretStore(path, new XorProtector());

        Assert.Null(await store.GetAsync("нема-такого"));
        Cleanup(path);
    }

    [Fact]
    public async Task Set_перезаписує_попереднє_значення()
    {
        var path = TempPath();
        var store = new FileSecretStore(path, new XorProtector());

        await store.SetAsync("openai", "first");
        await store.SetAsync("openai", "second");

        Assert.Equal("second", await store.GetAsync("openai"));
        Cleanup(path);
    }

    [Fact]
    public async Task Delete_прибирає_ключ_і_ідемпотентний()
    {
        var path = TempPath();
        var store = new FileSecretStore(path, new XorProtector());

        await store.SetAsync("anthropic", "sk-ant");
        await store.DeleteAsync("anthropic");
        Assert.False(await store.HasAsync("anthropic"));

        // Повторне видалення не має падати.
        await store.DeleteAsync("anthropic");
        Assert.Null(await store.GetAsync("anthropic"));
        Cleanup(path);
    }

    [Fact]
    public async Task Has_відображає_наявність_без_розкриття()
    {
        var path = TempPath();
        var store = new FileSecretStore(path, new XorProtector());

        Assert.False(await store.HasAsync("openai"));
        await store.SetAsync("openai", "sk-x");
        Assert.True(await store.HasAsync("openai"));
        Cleanup(path);
    }

    [Fact]
    public async Task ListKeys_дає_імена_а_не_значення()
    {
        var path = TempPath();
        var store = new FileSecretStore(path, new XorProtector());

        await store.SetAsync("elevenlabs", "a");
        await store.SetAsync("openai", "b");

        var keys = await store.ListKeysAsync();

        Assert.Contains("elevenlabs", keys);
        Assert.Contains("openai", keys);
        // Значень у списку ключів бути не може.
        Assert.DoesNotContain("a", keys);
        Assert.DoesNotContain("b", keys);
        Cleanup(path);
    }

    [Fact]
    public async Task Значення_на_диску_НЕ_у_відкритому_вигляді()
    {
        var path = TempPath();
        var store = new FileSecretStore(path, new XorProtector());

        await store.SetAsync("elevenlabs", "PLAINTEXT-SECRET");

        var onDisk = await File.ReadAllTextAsync(path);
        // Ім'я ключа лежить відкрито (це нормально), а саме значення — ні.
        Assert.Contains("elevenlabs", onDisk);
        Assert.DoesNotContain("PLAINTEXT-SECRET", onDisk);
        Cleanup(path);
    }

    [Fact]
    public async Task Битий_файл_читається_як_порожнє_сховище()
    {
        var path = TempPath();
        await File.WriteAllTextAsync(path, "{ не валідний json ");

        var store = new FileSecretStore(path, new XorProtector());

        // Замість винятку — порожньо; можна одразу писати нове.
        Assert.Empty(await store.ListKeysAsync());
        await store.SetAsync("openai", "sk-new");
        Assert.Equal("sk-new", await store.GetAsync("openai"));
        Cleanup(path);
    }

    [Fact]
    public async Task Нерозшифровне_значення_трактується_як_відсутнє()
    {
        var path = TempPath();

        // Записуємо одним протектором…
        var storeA = new FileSecretStore(path, new XorProtector());
        await storeA.SetAsync("openai", "secret");

        // …читаємо іншим (імітація DPAPI-прив'язки до іншої машини/юзера).
        var storeB = new FileSecretStore(path, new ThrowingProtector());
        Assert.Null(await storeB.GetAsync("openai"));
        Cleanup(path);
    }

    /// <summary>Протектор, що завжди падає на Unprotect — імітує чужий DPAPI-контекст.</summary>
    private sealed class ThrowingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] ciphertext) => throw new InvalidOperationException("чужий контекст");
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "vc_secrets_" + Guid.NewGuid() + ".dat");

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
