namespace VideoCombine.App.Security;

using System.Security.Cryptography;
using VideoCombine.Core.Providers;

/// <summary>
/// Windows-реалізація ISecretProtector через DPAPI (ProtectedData). Уся платформна
/// специфіка шифрування API-ключів сидить ТУТ, у App — Core лишається чистим
/// net8.0 і портативним (розділ 6).
///
/// DataProtectionScope.CurrentUser: секрет прив'язаний до поточного користувача
/// Windows. Наслідок (свідомий): файл secrets.dat, зашифрований на цій машині/юзері,
/// на іншому Windows чи в іншого користувача НЕ розшифрується — ключі не мандрують
/// із копією репо/бекапом. Для секретів це радше плюс, ніж мінус. FileSecretStore
/// на невдалий Unprotect відповідає "ключа нема" — користувач просто введе заново.
///
/// Додатковий "entropy" (сіль додатка) підмішується, щоб секрет не міг розшифрувати
/// інший застосунок того ж користувача — лише VideoCombine.
///
/// Двері на майбутнє: macOS/Linux — свій протектор (Keychain / AES-пароль); веб —
/// зовсім інша реалізація ISecretStore (секрети на сервері). Цей клас не чіпається.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Стала "сіль" застосунку — не секрет, лише розділяє наш DPAPI-простір від чужих.
    private static readonly byte[] Entropy =
        System.Text.Encoding.UTF8.GetBytes("VideoCombine.SecretStore.v1");

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}
