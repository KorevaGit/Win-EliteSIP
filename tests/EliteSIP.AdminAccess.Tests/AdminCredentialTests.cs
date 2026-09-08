using System.Text;
using System.Text.Json;
using EliteSIP.AdminAccess;

namespace EliteSIP.AdminAccess.Tests;

/// <summary>Административный пароль.</summary>
public sealed class AdminCredentialTests
{
    /// <summary>
    /// Быстрая цена перебора: тесту нужна верность проверки, а не её стоимость.
    /// Боевое число итераций живёт в <c>KeyDerivation.DefaultIterations</c> и
    /// здесь стоило бы секунд на каждый случай.
    /// </summary>
    private const int TestIterations = 1_000;

    [Fact]
    public void Свой_пароль_подходит_чужой_нет()
    {
        AdminCredential credential = AdminCredential.Create("Гладиолус7", TestIterations);

        Assert.True(credential.Matches("Гладиолус7"));
        Assert.False(credential.Matches("гладиолус7"));
        Assert.False(credential.Matches("Гладиолус"));
        Assert.False(credential.Matches(string.Empty));
    }

    [Fact]
    public void Самого_пароля_в_сохранённых_данных_нет()
    {
        const string password = "СекретноеСлово";
        AdminCredential credential = AdminCredential.Create(password, TestIterations);

        string json = JsonSerializer.Serialize(credential);

        // Ни в каком виде: ни строкой, ни её байтами внутри base64-полей.
        Assert.DoesNotContain(password, json, StringComparison.Ordinal);
        Assert.True(
            credential.LoginHash.AsSpan().IndexOf(Encoding.UTF8.GetBytes(password)) < 0,
            "байты пароля не должны находиться и внутри самого хеша");
    }

    [Fact]
    public void Одинаковые_пароли_дают_разные_хеши()
    {
        AdminCredential first = AdminCredential.Create("Одно и то же", TestIterations);
        AdminCredential second = AdminCredential.Create("Одно и то же", TestIterations);

        Assert.False(first.LoginSalt.AsSpan().SequenceEqual(second.LoginSalt));
        Assert.False(first.LoginHash.AsSpan().SequenceEqual(second.LoginHash));

        // При этом оба проверяют один и тот же пароль.
        Assert.True(first.Matches("Одно и то же"));
        Assert.True(second.Matches("Одно и то же"));
    }

    [Fact]
    public void Пустой_пароль_не_заводится()
    {
        AdminAccessException error = Assert.Throws<AdminAccessException>(
            () => AdminCredential.Create(string.Empty, TestIterations));

        Assert.Equal(AdminAccessFailure.EmptyPassword, error.Failure);
    }

    [Fact]
    public void Пустые_учётные_данные_не_открываются()
    {
        AdminCredential credential = new(TestIterations, [], []);

        Assert.False(credential.Matches("что угодно"));
    }

    [Fact]
    public void Учётные_данные_переживают_запись_и_чтение_файла_настроек()
    {
        AdminCredential credential = AdminCredential.Create("Пароль", TestIterations);

        string json = JsonSerializer.Serialize(credential);
        AdminCredential restored = JsonSerializer.Deserialize<AdminCredential>(json)!;

        Assert.Equal(credential, restored);
        Assert.True(restored.Matches("Пароль"));
    }

    /// <summary>
    /// Проверки в оригинале нет: там число итераций приезжало из того же JSON,
    /// но потолок закрывался только чтением кода. Здесь он закрыт проверкой —
    /// правка файла обязана давать отказ, а не зависший вход.
    /// </summary>
    [Fact]
    public void Немыслимое_число_итераций_из_файла_не_вешает_вход()
    {
        AdminCredential credential = AdminCredential.Create("Пароль", TestIterations);
        AdminCredential tampered = new(int.MaxValue, credential.LoginSalt, credential.LoginHash);

        // Секунды, а не часы: число обрезано потолком. И пароль при этом не
        // сходится — вход отвечает «неверный пароль», а не молчит.
        Assert.False(tampered.Matches("Пароль"));
    }
}
