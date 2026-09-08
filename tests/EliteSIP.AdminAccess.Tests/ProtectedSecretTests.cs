using EliteSIP.AdminAccess;

namespace EliteSIP.AdminAccess.Tests;

/// <summary>
/// Секрет в файле настроек.
///
/// Проверок в оригинале нет и быть не могло: там пароль профиля лежал открытым
/// текстом под правами <c>0600</c>, и проверять было нечего. Здесь появился
/// шифр, а у шифра — обещание, которое надо держать.
///
/// Не на Windows проверки не делают ничего: DPAPI системный, подделывать его
/// нечем, а проверять подделку смысла нет.
/// </summary>
public sealed class ProtectedSecretTests
{
    [Fact]
    public void Секрет_возвращается_тем_же()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string stored = ProtectedSecret.Protect("пароль профиля");

        Assert.Equal("пароль профиля", ProtectedSecret.Unprotect(stored));
    }

    [Fact]
    public void В_записанном_виде_пароля_не_видно()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string stored = ProtectedSecret.Protect("СекретноеСлово");

        Assert.DoesNotContain("СекретноеСлово", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void Мусор_вместо_шифра_не_роняет_чтение_настроек()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Так выглядит файл, принесённый с другой машины, и так же — файл,
        // побитый редактором. Отличить их нечем, и обоим ответ один: пароля
        // здесь нет, спросите заново.
        Assert.Null(ProtectedSecret.Unprotect("это не base64!!"));
        Assert.Null(ProtectedSecret.Unprotect("0YHQvtCy0YHQtdC8INC00YDRg9Cz0L7QtQ=="));
    }
}
