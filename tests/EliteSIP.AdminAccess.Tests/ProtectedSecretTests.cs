using System.Runtime.Versioning;
using EliteSIP.AdminAccess;
using EliteSIP.Tests;

namespace EliteSIP.AdminAccess.Tests;

/// <summary>
/// Секрет в файле настроек.
///
/// Проверок в оригинале нет и быть не могло: там пароль профиля лежал открытым
/// текстом под правами <c>0600</c>, и проверять было нечего. Здесь появился
/// шифр, а у шифра — обещание, которое надо держать.
///
/// Первые три проверки — общие: обещание одно на обе ОС, хотя держат его
/// разные механизмы (DPAPI и AES-GCM с ключом в профиле). Остальные — про ключ,
/// который есть только вне Windows. До 20 октября 2026 общие проверки вне
/// Windows молча выходили и отчитывались зелёными, ничего не проверив.
///
/// Каталог ключа у каждой проверки свой, во временной папке: настоящий
/// профиль того, кто запускает проверки, не трогается.
/// </summary>
public sealed class ProtectedSecretTests : IDisposable
{
    private readonly string _previousKeyDirectory = ProtectedSecret.KeyDirectory;
    private readonly string _keyDirectory = Path.Combine(
        Path.GetTempPath(),
        "elitesip-secret-test-" + Guid.NewGuid().ToString("N"));

    public ProtectedSecretTests()
    {
        ProtectedSecret.KeyDirectory = _keyDirectory;
    }

    private string KeyPath => Path.Combine(_keyDirectory, "secret.key");

    public void Dispose()
    {
        ProtectedSecret.KeyDirectory = _previousKeyDirectory;
        if (Directory.Exists(_keyDirectory))
        {
            Directory.Delete(_keyDirectory, recursive: true);
        }
    }

    [Fact]
    public void Секрет_возвращается_тем_же()
    {
        string stored = ProtectedSecret.Protect("пароль профиля");

        Assert.Equal("пароль профиля", ProtectedSecret.Unprotect(stored));
    }

    [Fact]
    public void В_записанном_виде_пароля_не_видно()
    {
        string stored = ProtectedSecret.Protect("СекретноеСлово");

        Assert.DoesNotContain("СекретноеСлово", stored, StringComparison.Ordinal);

        // base64 открытого текста — тоже «не видно» глазом, но не шифр.
        // Сверяем и с ним, иначе проверку прошла бы простая перекодировка.
        string encodedOnly = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("СекретноеСлово"));
        Assert.DoesNotContain(encodedOnly, stored, StringComparison.Ordinal);
    }

    [Fact]
    public void Мусор_вместо_шифра_не_роняет_чтение_настроек()
    {
        // Так выглядит файл, принесённый с другой машины, и так же — файл,
        // побитый редактором. Отличить их нечем, и обоим ответ один: пароля
        // здесь нет, спросите заново.
        _ = ProtectedSecret.Protect("чтобы ключ уже был");

        Assert.Null(ProtectedSecret.Unprotect("это не base64!!"));
        Assert.Null(ProtectedSecret.Unprotect("0YHQvtCy0YHQtdC8INC00YDRg9Cz0L7QtQ=="));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void Ключ_закрыт_от_всех_кроме_владельца()
    {
        _ = ProtectedSecret.Protect("пароль");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(_keyDirectory));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void Без_ключа_секрет_не_читается()
    {
        string stored = ProtectedSecret.Protect("пароль");

        File.Delete(KeyPath);
        ProtectedSecret.KeyDirectory = _keyDirectory;

        Assert.Null(ProtectedSecret.Unprotect(stored));

        // Чтение не заводит новый ключ: старое им всё равно не открылось бы.
        Assert.False(File.Exists(KeyPath));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void Чужой_ключ_секрет_не_открывает()
    {
        string stored = ProtectedSecret.Protect("пароль");

        // Новый ключ на месте старого — то же, что файл настроек, принесённый
        // из другого профиля или с другой машины.
        File.Delete(KeyPath);
        ProtectedSecret.KeyDirectory = _keyDirectory;
        _ = ProtectedSecret.Protect("другой пароль");

        Assert.Null(ProtectedSecret.Unprotect(stored));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void Ключ_переживает_перезапуск()
    {
        string stored = ProtectedSecret.Protect("пароль");

        // Сброс прочитанного ключа — то же, что новый запуск приложения.
        ProtectedSecret.KeyDirectory = _keyDirectory;

        Assert.Equal("пароль", ProtectedSecret.Unprotect(stored));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void Расширенные_руками_права_ключа_сужаются()
    {
        _ = ProtectedSecret.Protect("пароль");
        File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        ProtectedSecret.KeyDirectory = _keyDirectory;
        _ = ProtectedSecret.Protect("ещё пароль");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
    }
}
