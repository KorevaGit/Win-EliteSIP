namespace EliteSIP.SipCore.Tests;

/// <summary>
/// Примитивы SIP: методы, транспорт, токены, учётная запись.
///
/// Перенесено из <c>Packages/SIPCore/Tests/SIPCoreTests/SIPPrimitiveTests.swift</c>
/// проверка в проверку.
/// </summary>
public sealed class SipPrimitiveTests
{
    [Fact]
    public void Метод_разбирается_без_учёта_регистра()
    {
        Assert.Equal(SipMethod.Invite, SipMethodExtensions.Parse("invite"));
        Assert.Equal(SipMethod.Refer, SipMethodExtensions.Parse("ReFeR"));
        Assert.Null(SipMethodExtensions.Parse("PUBLISH"));
    }

    [Fact]
    public void Диалог_создаёт_только_INVITE()
    {
        Assert.True(SipMethod.Invite.CreatesDialog());
        foreach (SipMethod method in Enum.GetValues<SipMethod>())
        {
            if (method != SipMethod.Invite)
            {
                Assert.False(method.CreatesDialog(), $"{method.Name()} не должен создавать диалог");
            }
        }
    }

    [Fact]
    public void Порты_и_свойства_транспорта()
    {
        Assert.Equal(5060, SipTransport.Udp.DefaultPort());
        Assert.Equal(5060, SipTransport.Tcp.DefaultPort());
        Assert.Equal(5061, SipTransport.Tls.DefaultPort());

        Assert.True(SipTransport.Tls.IsSecure());
        Assert.False(SipTransport.Udp.IsSecure());

        // От этого зависит, запускать ли retransmit-таймеры транзакции.
        Assert.False(SipTransport.Udp.IsReliable());
        Assert.True(SipTransport.Tcp.IsReliable());
        Assert.True(SipTransport.Tls.IsReliable());

        Assert.Equal("UDP", SipTransport.Udp.ProtocolName());
        Assert.Equal(SipTransport.Tls, SipTransportExtensions.Parse("TLS"));
    }

    [Fact]
    public void Branch_начинается_с_обязательного_магического_префикса()
    {
        string branch = SipToken.Branch();
        Assert.StartsWith(SipToken.BranchMagicCookie, branch, StringComparison.Ordinal);
        Assert.True(branch.Length > SipToken.BranchMagicCookie.Length, "после префикса должна быть случайная часть");
    }

    [Fact]
    public void Токены_не_повторяются()
    {
        // Совпадение call-id между звонками ломает маршрутизацию диалогов,
        // поэтому проверяем именно уникальность, а не просто ненулевую длину.
        const int Count = 2000;

        Assert.Equal(Count, Generate(Count, () => SipToken.Branch()).Count);
        Assert.Equal(Count, Generate(Count, () => SipToken.CallId()).Count);
        Assert.Equal(Count, Generate(Count, SipToken.Tag).Count);

        static HashSet<string> Generate(int count, Func<string> make)
        {
            HashSet<string> values = new(StringComparer.Ordinal);
            for (int index = 0; index < count; index++)
            {
                values.Add(make());
            }
            return values;
        }
    }

    [Fact]
    public void Токены_состоят_только_из_безопасных_символов()
    {
        const string Allowed = "abcdefghijklmnopqrstuvwxyz0123456789";
        for (int attempt = 0; attempt < 200; attempt++)
        {
            Assert.All(SipToken.Tag(), character => Assert.True(Allowed.Contains(character, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void CallId_с_хостом_и_без()
    {
        Assert.DoesNotContain('@', SipToken.CallId());
        Assert.EndsWith("@win.local", SipToken.CallId("win.local"), StringComparison.Ordinal);
        Assert.DoesNotContain('@', SipToken.CallId(string.Empty));
    }

    /// <summary>
    /// По согласованному плану номер аккаунта служит и user-part, и
    /// отображаемым именем: пустое поле означает «имя равно номеру», а не
    /// «имени нет».
    /// </summary>
    [Fact]
    public void Отображаемое_имя_по_умолчанию_равно_номеру()
    {
        var account = new SipAccount { Username = "711", Domain = "pbx.example" };
        Assert.Equal("711", account.EffectiveDisplayName);

        account.DisplayName = "Call_Center";
        Assert.Equal("Call_Center", account.EffectiveDisplayName);
    }

    /// <summary>
    /// Логин для аутентификации падает на номер по тому же правилу — проверяем
    /// рядом, чтобы две подстановки не разъехались.
    /// </summary>
    [Fact]
    public void Логин_аутентификации_по_умолчанию_равен_номеру()
    {
        var account = new SipAccount { Username = "711", Domain = "pbx.example" };
        Assert.Equal("711", account.EffectiveAuthUsername);

        account.AuthUsername = "711-auth";
        Assert.Equal("711-auth", account.EffectiveAuthUsername);

        account.AuthUsername = string.Empty;
        Assert.Equal("711", account.EffectiveAuthUsername);
    }
}
