namespace VideoCombine.Core.Providers;

/// <summary>
/// Крихітна абстракція «зашифрувати/розшифрувати байти». Уся ПЛАТФОРМНА
/// специфіка шифрування сидить ТУТ — рівно в одному місці.
///
/// Навіщо окремо від ISecretStore: FileSecretStore (конверт, ключі, base64,
/// файл) — портативний і живе в Core. А сам крипто-примітив залежить від ОС:
///   • Windows — DPAPI (ProtectedData), прив'язка до користувача+машини.
///     Реалізація DpapiSecretProtector — у App (там Windows-залежність легальна,
///     Core лишається чистим net8.0 і тестованим).
///   • macOS/Linux/веб — інший протектор (Keychain / AES-пароль / KMS) під той
///     самий інтерфейс, без переробки FileSecretStore і кроків.
///
/// Симетрія обов'язкова: Unprotect(Protect(x)) == x на тій самій машині.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Зашифрувати відкриті байти в захищені (для запису на диск).</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>Розшифрувати захищені байти назад (при читанні з диска).</summary>
    byte[] Unprotect(byte[] ciphertext);
}
