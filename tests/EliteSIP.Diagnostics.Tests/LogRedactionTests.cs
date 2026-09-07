namespace EliteSIP.Diagnostics.Tests;

/// <summary>
/// Маскирование секретов.
///
/// Проверка приёмки M7a в оригинале звучит как «в собранном архиве нет ни одного
/// <c>response=</c>», и держит её этот набор. Ошибка здесь стоит пароля от
/// SIP-аккаунта в чужом мессенджере, а заметить её на разработке нельзя: в
/// журнале всё выглядит правдоподобно ровно до того момента, когда файл читает
/// чужой.
///
/// Перенесено из <c>Packages/Diagnostics/Tests/DiagnosticsTests/LogRedactionTests.swift</c>
/// проверка в проверку.
/// </summary>
public sealed class LogRedactionTests
{
    [Fact]
    public void Ответ_Digest_не_попадает_в_журнал()
    {
        const string Line =
            "-> REGISTER Authorization: Digest username=\"100\", realm=\"asterisk\", " +
            "nonce=\"1a2b3c\", uri=\"sip:127.0.0.1\", " +
            "response=\"5f4dcc3b5aa765d61d8327deb882cf99\", algorithm=MD5";

        string redacted = LogRedaction.Redact(Line);

        Assert.DoesNotContain("5f4dcc3b5aa765d61d8327deb882cf99", redacted, StringComparison.Ordinal);
        Assert.Contains("response=\"скрыто\"", redacted, StringComparison.Ordinal);

        // Остальное обязано уцелеть: по realm и nonce разбирают, чем именно
        // сервер недоволен, и вырезать их — значит сделать журнал бесполезным.
        Assert.Contains("username=\"100\"", redacted, StringComparison.Ordinal);
        Assert.Contains("realm=\"asterisk\"", redacted, StringComparison.Ordinal);
        Assert.Contains("nonce=\"1a2b3c\"", redacted, StringComparison.Ordinal);
        Assert.Contains("algorithm=MD5", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Ответ_без_кавычек_маскируется_тоже()
    {
        string redacted = LogRedaction.Redact("проверка response=5f4dcc3b, дальше текст");

        Assert.DoesNotContain("5f4dcc3b", redacted, StringComparison.Ordinal);
        Assert.Contains("response=скрыто", redacted, StringComparison.Ordinal);
        Assert.Contains("дальше текст", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Response")]
    [InlineData("RESPONSE")]
    [InlineData("ReSpOnSe")]
    public void Регистр_не_спасает_секрет(string field)
    {
        string redacted = LogRedaction.Redact($"{field}=\"secretvalue\"");

        Assert.DoesNotContain("secretvalue", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Ключ_SRTP_не_попадает_в_журнал()
    {
        const string Line =
            "a=crypto:1 AES_CM_128_HMAC_SHA1_80 " +
            "inline:d0RmdmcmVCspeEc3QGZiNWpVLFJhQX1cfHAwJSoj|2^20|1:32";

        string redacted = LogRedaction.Redact(Line);

        Assert.DoesNotContain("d0RmdmcmVCspeEc3QGZiNWpVLFJhQX1cfHAwJSoj", redacted, StringComparison.Ordinal);

        // Срок жизни ключа маскируется вместе с ним.
        Assert.DoesNotContain("2^20", redacted, StringComparison.Ordinal);
        Assert.Contains("inline:скрыто", redacted, StringComparison.Ordinal);

        // Имя набора остаётся: по нему видно профиль.
        Assert.Contains("AES_CM_128_HMAC_SHA1_80", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("password=elite100")]
    [InlineData("secret = elite100")]
    [InlineData("pwd=elite100")]
    [InlineData("token=elite100")]
    [InlineData("api_key=elite100")]
    public void Пароли_под_любым_из_привычных_имён(string line)
    {
        Assert.DoesNotContain("elite100", LogRedaction.Redact(line), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<- INVITE от 2929, ответили 180")]
    [InlineData("медиа: RTP G722 на 192.168.1.221:10008")]
    [InlineData("защита: 1 нажатие, курсор двигался, вердикт «человек»")]
    [InlineData("звоню на 22998, RTP-порт 16384")]
    public void Обычные_строки_журнала_не_портятся(string line)
    {
        Assert.Equal(line, LogRedaction.Redact(line));
    }

    [Fact]
    public void Номера_и_SIP_логины_остаются_как_есть()
    {
        // Решение заказчика от 30 июля 2026: история хранит номер и SIP-логин,
        // значит и в журнале маскировать их незачем — те же данные лежат на той
        // же машине. Маскируются только секреты.
        const string Line = "<- INVITE от 2929 (AutoDialer) на 100";

        Assert.Equal(Line, LogRedaction.Redact(Line));
    }

    [Theory]
    [InlineData("response=")]
    [InlineData("response")]
    [InlineData("inline:")]
    public void Поле_без_значения_не_ломает_разбор(string line)
    {
        Assert.Equal(line, LogRedaction.Redact(line));
    }
}
