using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace EliteSIP.AdminAccess;

/// <summary>
/// Секрет машины, лежащий в файле настроек: пароль SIP-профиля и всё, что
/// придётся хранить рядом с ним.
///
/// <b>Почему это вообще понадобилось.</b> В оригинале пароль профиля лежал в
/// файле открытым текстом, а защитой были права <c>0600</c> — и это было
/// сознательное решение (<c>docs/profiles.md</c>): Keychain на общей машине
/// спрашивал бы разрешение у того, кто за ней сидит, а сидят за ней посменно. На
/// Windows тот же приём не работает: права файла здесь не мешают ни другому
/// администратору, ни бэкапу, ни копированию профиля на флешку. Поэтому сквозное
/// правило плана — DPAPI с областью <see cref="DataProtectionScope.CurrentUser"/>:
/// вынесенный файл не расшифровывается нигде, кроме этой учётной записи на этой
/// машине.
///
/// <b>Почему здесь, а не в проекте настроек.</b> Проекта настроек нет: модель
/// настроек и профилей — часть приложения, а оно приходит на W8. Заводить пустой
/// проект ради одного типа хуже, чем положить его туда, где уже живёт вторая
/// половина того же разговора — секреты, которые нельзя хранить как есть.
///
/// <b>Чего этот тип не делает.</b> Он не защищает от того, кто уже вошёл под
/// этой учётной записью: такой человек расшифрует секрет тем же вызовом. Это не
/// пробел, а граница задачи — она ровно та же, что была у прав <c>0600</c> в
/// оригинале.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProtectedSecret
{
    /// <summary>
    /// Дополнительная примесь к ключу DPAPI.
    ///
    /// Не секрет и секретом быть не должна: она лишь привязывает шифротекст к
    /// назначению, чтобы значение, вытащенное из чужого файла того же
    /// пользователя, здесь не расшифровалось.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EliteSIP.Settings.v1");

    /// <summary>
    /// Зашифровать секрет для записи в файл настроек. Возврат — base64, потому
    /// что настройки лежат в JSON.
    /// </summary>
    public static string Protect(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        byte[] plaintext = Encoding.UTF8.GetBytes(secret);
        try
        {
            byte[] cipher = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }
        finally
        {
            // Открытый пароль не остаётся в куче дольше, чем нужно. Строку,
            // пришедшую снаружи, затереть нельзя — а свой массив можно.
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Расшифровать секрет из файла настроек.
    ///
    /// <c>null</c> означает ровно одно: значение не наше. Так выглядит файл,
    /// принесённый с другой машины или из другой учётной записи, — и это не
    /// поломка, а обычный случай, который вызывающий обрабатывает просьбой ввести
    /// пароль заново. Отличать «испорчено» от «чужое» нечем: DPAPI на оба случая
    /// отвечает одинаково.
    /// </summary>
    public static string? Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);

        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            byte[] plaintext = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
