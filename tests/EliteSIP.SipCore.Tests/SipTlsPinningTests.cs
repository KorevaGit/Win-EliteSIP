using System.Text;
using EliteSIP.SipCore;

namespace EliteSIP.SipCore.Tests;

/// <summary>Пиннинг сертификата.</summary>
public sealed class SipTlsPinningTests
{
    private static readonly byte[] Certificate = Encoding.ASCII.GetBytes("это не сертификат, а его место");

    private static string Expected => SipTlsPinning.Fingerprint(Certificate);

    [Fact]
    public void Свой_отпечаток_сходится()
    {
        Assert.True(SipTlsPinning.Matches(new HashSet<string> { Expected }, Certificate));
    }

    /// <summary>
    /// Отпечаток принимается в том виде, в каком его диктуют и вставляют.
    /// </summary>
    ///
    /// <remarks>
    /// Двоеточия ставит <c>openssl x509 -fingerprint</c>, пробелы — оснастка
    /// Windows, нижний регистр — половина документации. Требовать одного
    /// написания значит отказывать человеку, который всё сделал правильно, а
    /// разбирать он это будет как «сертификат не подошёл».
    /// </remarks>
    [Fact]
    public void Написание_отпечатка_не_важно()
    {
        var hex = Expected;
        var withColons = string.Join(':', Enumerable.Range(0, hex.Length / 2)
            .Select(index => hex.Substring(index * 2, 2)));

        Assert.True(SipTlsPinning.Matches(new HashSet<string> { withColons.ToLowerInvariant() }, Certificate));
        Assert.True(SipTlsPinning.Matches(
            new HashSet<string> { withColons.Replace(':', ' ') + "  " }, Certificate));
    }

    [Fact]
    public void Чужой_отпечаток_не_сходится()
    {
        var stranger = SipTlsPinning.Fingerprint("другой сертификат"u8);

        Assert.False(SipTlsPinning.Matches(new HashSet<string> { stranger }, Certificate));
    }

    /// <summary>
    /// Пустой список не совпадает ни с чем.
    /// </summary>
    ///
    /// <remarks>
    /// Пиннинг заменяет системную проверку целиком, и «список пуст — значит
    /// пускаем всех» превратило бы забытую настройку в отключённую защиту.
    /// </remarks>
    [Fact]
    public void Пустой_список_не_пускает_никого()
    {
        Assert.False(SipTlsPinning.Matches(new HashSet<string>(), Certificate));
    }

    [Fact]
    public void Один_из_нескольких_отпечатков_достаточен()
    {
        HashSet<string> fingerprints =
        [
            SipTlsPinning.Fingerprint("старый сертификат"u8),
            Expected,
        ];

        // Несколько отпечатков — это смена сертификата без простоя: старый ещё
        // принимается, новый уже принимается.
        Assert.True(SipTlsPinning.Matches(fingerprints, Certificate));
    }
}
