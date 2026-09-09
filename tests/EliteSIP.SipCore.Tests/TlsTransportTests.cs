using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EliteSIP.SipCore;
using EliteSIP.SipCore.Udp;

namespace EliteSIP.SipCore.Tests;

/// <summary>
/// TLS-транспорт: рукопожатие, доверие и нарезка сообщений.
/// </summary>
///
/// <remarks>
/// Проверки здесь сквозные и поднимают настоящий TLS-сервер на петле — иначе
/// проверять нечего: важно не то, что метод вернул, а что рукопожатие сошлось
/// или не сошлось.
///
/// Сертификат самоподписанный и создаётся на месте. Это же и есть боевой случай
/// пиннинга: у лабораторного Asterisk сертификат ровно такой, системную проверку
/// он не проходит по построению.
/// </remarks>
public sealed class TlsTransportTests : IDisposable
{
    private readonly X509Certificate2 _certificate = SelfSigned();
    private readonly Socket _listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    private readonly CancellationTokenSource _lifetime = new();

    public TlsTransportTests()
    {
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(4);
        Port = (ushort)((IPEndPoint)_listener.LocalEndPoint!).Port;
    }

    private ushort Port { get; }

    private string Fingerprint => SipTlsPinning.Fingerprint(_certificate.RawData);

    public void Dispose()
    {
        _lifetime.Cancel();
        _listener.Dispose();
        _certificate.Dispose();
        _lifetime.Dispose();
    }

    [Fact]
    public async Task Пиннинг_по_отпечатку_поднимает_соединение()
    {
        var served = ServeAsync(sendAfterHandshake: null);

        using SocketSipTransport transport = new(
            new SipEndpoint("127.0.0.1", Port),
            SipTransport.Tls,
            new SipTlsTrust.PinnedCertificateSha256(new HashSet<string> { Fingerprint }));

        await transport.StartAsync();

        Assert.IsType<SipTransportEvent.Ready>(await FirstEventAsync(transport));
        await served;
    }

    /// <summary>
    /// Чужой отпечаток обязан отвергнуть соединение, а не поднять его.
    /// </summary>
    ///
    /// <remarks>
    /// Это и есть та проверка, ради которой набор существует: пиннинг, который
    /// пропускает чужой сертификат, выглядит работающим ровно до того дня, когда
    /// между машиной и АТС кто-то встанет.
    ///
    /// Отказ приходит как <c>Failed</c>, а не <c>Closed</c>: транспорт будет
    /// пробовать снова сам — сертификат могли просто перевыпустить, и до
    /// исправления настройки регистрация обязана продолжать стучаться, а не
    /// умереть молча.
    /// </remarks>
    [Fact]
    public async Task Чужой_отпечаток_не_поднимает_соединение()
    {
        var served = ServeAsync(sendAfterHandshake: null);

        using SocketSipTransport transport = new(
            new SipEndpoint("127.0.0.1", Port),
            SipTransport.Tls,
            new SipTlsTrust.PinnedCertificateSha256(new HashSet<string>
            {
                SipTlsPinning.Fingerprint("чужой сертификат"u8),
            }));

        await transport.StartAsync();

        var failure = Assert.IsType<SipTransportEvent.Failed>(await FirstEventAsync(transport));
        Assert.Contains("TLS", failure.Reason, StringComparison.Ordinal);

        await served;
    }

    /// <summary>
    /// Системная проверка не принимает самоподписанный сертификат.
    /// </summary>
    ///
    /// <remarks>
    /// Проверка выглядит проверкой очевидного и стоит здесь именно поэтому:
    /// умолчание транспорта — системная проверка, и если бы она молча
    /// пропускала что угодно, ни одна другая проверка этого не заметила бы.
    /// </remarks>
    [Fact]
    public async Task Системная_проверка_отвергает_самоподписанный_сертификат()
    {
        var served = ServeAsync(sendAfterHandshake: null);

        using SocketSipTransport transport = new(new SipEndpoint("127.0.0.1", Port), SipTransport.Tls);

        await transport.StartAsync();

        Assert.IsType<SipTransportEvent.Failed>(await FirstEventAsync(transport));
        await served;
    }

    /// <summary>
    /// Сообщение, пришедшее по TLS, нарезается фреймером и доходит целиком.
    /// </summary>
    ///
    /// <remarks>
    /// На потоке границы сообщений задаёт только <c>Content-Length</c>, и
    /// разрезанное на два чтения сообщение обязано собраться обратно. Здесь оно
    /// нарочно отправляется двумя кусками с паузой.
    /// </remarks>
    [Fact]
    public async Task Сообщение_по_TLS_собирается_из_кусков()
    {
        const string Message =
            "SIP/2.0 200 OK\r\nVia: SIP/2.0/TLS 127.0.0.1;branch=z9hG4bK1\r\n"
            + "Content-Length: 4\r\n\r\nabcd";

        var served = ServeAsync(sendAfterHandshake: Message);

        using SocketSipTransport transport = new(
            new SipEndpoint("127.0.0.1", Port),
            SipTransport.Tls,
            new SipTlsTrust.PinnedCertificateSha256(new HashSet<string> { Fingerprint }));

        await transport.StartAsync();

        await foreach (var next in transport.Events.WithCancellation(Timeout))
        {
            if (next is SipTransportEvent.Received received)
            {
                Assert.Equal(Message, Encoding.UTF8.GetString(received.Data.Span));
                await served;
                return;
            }

            Assert.IsNotType<SipTransportEvent.Closed>(next);
        }

        Assert.Fail("сообщение не доехало");
    }

    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static async Task<SipTransportEvent> FirstEventAsync(SocketSipTransport transport)
    {
        await foreach (var next in transport.Events.WithCancellation(Timeout))
        {
            return next;
        }

        Assert.Fail("транспорт не сказал ничего");
        throw new InvalidOperationException();
    }

    /// <summary>Один клиент: рукопожатие и, если просили, одно сообщение двумя кусками.</summary>
    private async Task ServeAsync(string? sendAfterHandshake)
    {
        using var client = await _listener.AcceptAsync(_lifetime.Token);
        await using SslStream stream = new(new NetworkStream(client, ownsSocket: false));

        try
        {
            await stream.AuthenticateAsServerAsync(_certificate, false, checkCertificateRevocation: false);
        }
        catch (Exception error) when (error is AuthenticationException or IOException)
        {
            // Клиент отверг сертификат — это законный исход половины проверок.
            return;
        }

        if (sendAfterHandshake is null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(sendAfterHandshake);
        var split = bytes.Length - 2;

        await stream.WriteAsync(bytes.AsMemory(0, split), _lifetime.Token);
        await stream.FlushAsync(_lifetime.Token);
        await Task.Delay(50, _lifetime.Token);
        await stream.WriteAsync(bytes.AsMemory(split), _lifetime.Token);
        await stream.FlushAsync(_lifetime.Token);

        // Держим соединение, пока проверка не кончится: закрытие сразу после
        // записи пришло бы клиенту как «сервер закрыл соединение» вперёд самого
        // сообщения.
        await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token).ContinueWith(
            _ => { }, TaskScheduler.Default);
    }

    /// <summary>
    /// Самоподписанный сертификат на «localhost», живущий только внутри проверки.
    /// </summary>
    ///
    /// <remarks>
    /// Через PFX и обратно — так требует Windows: сертификат, созданный
    /// <c>CreateSelfSigned</c>, отдаёт закрытый ключ SslStream'у только после
    /// такого круга.
    /// </remarks>
    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null);
    }
}
