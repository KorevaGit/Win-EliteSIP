using EliteSIP.AdminAccess;

namespace EliteSIP.AdminAccess.Tests;

/// <summary>Состояние административного режима.</summary>
public sealed class AdminAccessStateTests
{
    private const int TestIterations = 1_000;

    /// <summary>
    /// Состояние с уже заданным паролем.
    ///
    /// <c>SetPassword</c> считает боевым числом итераций; для проверок
    /// пересобираем учётные данные дешёвыми, с тем же паролем.
    /// </summary>
    private static AdminAccessState StateWith(string password)
    {
        AdminAccessState state = new();
        state.SetPassword(password);
        state.Restore(AdminCredential.Create(password, TestIterations));
        return state;
    }

    [Fact]
    public void Без_пароля_закрытая_часть_открыта()
    {
        AdminAccessState state = new();

        Assert.False(state.IsProtected);
        Assert.True(state.AllowsAdministration);
    }

    [Fact]
    public void С_паролем_закрытая_часть_закрыта_пока_не_вошли()
    {
        AdminAccessState state = StateWith("Пароль");

        Assert.True(state.IsProtected);
        Assert.False(state.AllowsAdministration);

        state.Unlock("Пароль");
        Assert.True(state.AllowsAdministration);
    }

    [Fact]
    public void Чужой_пароль_не_открывает_и_не_оставляет_режим_открытым()
    {
        AdminAccessState state = StateWith("Пароль");

        AdminAccessException error = Assert.Throws<AdminAccessException>(() => state.Unlock("Не пароль"));

        Assert.Equal(AdminAccessFailure.WrongPassword, error.Failure);
        Assert.False(state.AllowsAdministration);
    }

    [Fact]
    public void Выход_закрывает_режим()
    {
        AdminAccessState state = StateWith("Пароль");
        state.Unlock("Пароль");

        state.Lock();

        Assert.False(state.AllowsAdministration);
    }

    [Fact]
    public void Смена_пароля_из_закрытого_режима_невозможна()
    {
        AdminAccessState state = StateWith("Старый");

        Assert.Throws<AdminAccessException>(() => state.SetPassword("Новый"));
        Assert.Throws<AdminAccessException>(state.RemovePassword);
        Assert.True(state.IsProtected);
    }

    [Fact]
    public void Загрузка_настроек_всегда_закрывает_режим()
    {
        AdminAccessState state = StateWith("Пароль");
        state.Unlock("Пароль");
        Assert.True(state.AllowsAdministration);

        state.Restore(AdminCredential.Create("Пароль", TestIterations));

        Assert.False(state.AllowsAdministration);
    }

    [Fact]
    public void Снятый_пароль_открывает_настройки_всем()
    {
        AdminAccessState state = StateWith("Пароль");
        state.Unlock("Пароль");

        state.RemovePassword();

        Assert.False(state.IsProtected);
        Assert.True(state.AllowsAdministration);

        state.Lock();
        Assert.True(state.AllowsAdministration);
    }
}
