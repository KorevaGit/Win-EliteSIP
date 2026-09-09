using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace EliteSIP.SipCore.Udp;

/// <summary>
/// Транспорт SIP на <c>System.Net.Sockets</c>: UDP, TCP или TLS.
///
/// Интерфейс тот же, что у стенда из тестов, — <see cref="ISipTransportChannel"/>.
/// Ни слой транзакций, ни агент про сокеты не знают: в оригинале ту же границу
/// держал протокол <c>SIPTransportChannel</c>, и она перенесена как есть.
///
/// <b>Чем это отличается от оригинала.</b> На macOS повторные попытки соединения
/// брал на себя <c>Network.framework</c>: состояние «waiting» означало «повторю
/// сам», и транспорт сообщал о нём событием <see cref="SipTransportEvent.Failed"/>,
/// после которого канал оставался живым. У сокетов такого механизма нет —
/// неудавшийся <c>Connect</c> просто возвращает ошибку. Поэтому цикл повторов
/// живёт здесь, и семантика событий сохраняется дословно:
///
/// <list type="bullet">
/// <item><see cref="SipTransportEvent.Failed"/> — соединение не поднялось, но мы
/// попробуем ещё раз сами. Регистрация в этот момент ничего не пересобирает.</item>
/// <item><see cref="SipTransportEvent.Closed"/> — канал мёртв: приём отказал,
/// сервер закрыл TCP или испортился поток. Лечится только пересборкой
/// транспорта, и решает это тот, кто его создавал.</item>
/// </list>
///
/// <b>TLS — это тот же TCP плюс <c>SslStream</c> поверх сокета.</b> Отдельного
/// транспорта он не потребовал: нарезка сообщений, повторы соединения и разбор
/// отказов у потока общие, а различие ровно одно — через что читать и писать.
/// Завести ради этого второй класс значило бы держать две копии цикла повторов,
/// которые разойдутся на первой же правке.
/// </summary>
public sealed class SocketSipTransport : ISipTransportChannel, IDisposable
{
    /// <summary>
    /// Наибольший разумный размер датаграммы SIP.
    ///
    /// 64 килобайта — предел самой датаграммы UDP. На потоке столько же берётся
    /// за один приём, дальше нарезает фреймер.
    /// </summary>
    private const int ReceiveBufferSize = 64 * 1024;

    /// <summary>Задержки перед повтором соединения: 1, 2, 4, 8, 16, дальше 30 секунд.</summary>
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
    ];

    private readonly Channel<SipTransportEvent> _events =
        Channel.CreateBounded<SipTransportEvent>(new BoundedChannelOptions(256)
        {
            // Буфер с запасом: терять сигнализацию из-за переполнения нельзя, а
            // держать её бесконечно — способ съесть память на флуде.
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Только для потокового транспорта. На UDP датаграмма и есть сообщение.</summary>
    private readonly SipMessageFramer _framer = new();

    private readonly SipTlsTrust _trust;
    private readonly string _serverName;

    /// <summary>
    /// Поток TLS поверх сокета. `null` на UDP и на голом TCP.
    ///
    /// Своя блокировка на запись нужна именно ему: <see cref="SslStream"/> не
    /// терпит двух одновременных записей и отвечает на них исключением, а
    /// сигнализация пишется из нескольких мест разом — регистрация, звонок,
    /// keep-alive.
    /// </summary>
    private SslStream? _stream;
    private readonly SemaphoreSlim _writing = new(1, 1);

    private Socket? _socket;
    private Task? _worker;
    private bool _isStopped;

    /// <summary>Чем кончилось последнее рукопожатие TLS.</summary>
    private string? _handshakeFailure;

    /// <param name="trust">
    /// Как проверять сертификат сервера. Нужен только TLS; для UDP и TCP
    /// не читается вовсе.
    ///
    /// Умолчание — системная проверка. Это единственный правильный режим для
    /// боя, и он же обязан быть умолчанием: режим, который надо не забыть
    /// включить, однажды забудут.
    /// </param>
    /// <param name="serverName">
    /// Имя для SNI и для проверки сертификата. По умолчанию — адрес АТС.
    ///
    /// Отдельным параметром, потому что адрес и имя расходятся: до сервера,
    /// прописанного по IP, сертификат всё равно выписан на имя, и проверять его
    /// по IP значит отказывать всегда.
    /// </param>
    public SocketSipTransport(
        SipEndpoint remote,
        SipTransport transport,
        SipTlsTrust? trust = null,
        string? serverName = null)
    {
        Remote = remote;
        Transport = transport;
        _trust = trust ?? new SipTlsTrust.System();
        _serverName = serverName ?? remote.Host;
    }

    public SipTransport Transport { get; }

    public SipEndpoint Remote { get; }

    public IAsyncEnumerable<SipTransportEvent> Events => _events.Reader.ReadAllAsync();

    public Task StartAsync()
    {
        lock (_gate)
        {
            if (_worker is not null || _isStopped)
            {
                return Task.CompletedTask;
            }
            _worker = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data)
    {
        Socket socket;
        SslStream? stream;
        lock (_gate)
        {
            socket = _socket ?? throw new IOException("канал ещё не поднялся");
            stream = _stream;
        }

        if (stream is not null)
        {
            await _writing.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(data, _lifetime.Token).ConfigureAwait(false);
                await stream.FlushAsync(_lifetime.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                throw new IOException("канал TLS закрыт", error);
            }
            finally
            {
                _writing.Release();
            }
        }

        try
        {
            await socket.SendAsync(data, SocketFlags.None, _lifetime.Token).ConfigureAwait(false);
        }
        catch (SocketException error)
        {
            throw new IOException(
                SipTransportFailureText.Describe(error.SocketErrorCode, Remote, Transport),
                error);
        }
        catch (ObjectDisposedException error)
        {
            throw new IOException("канал закрыт", error);
        }
    }

    public async Task StopAsync()
    {
        Task? worker;
        lock (_gate)
        {
            if (_isStopped)
            {
                return;
            }
            _isStopped = true;
            worker = _worker;
            _worker = null;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        CloseSocket();

        // Своё закрытие — это Cancelled, а не Closed: Closed означает «канал
        // умер сам», и путать их значит просить приложение пересобирать то, что
        // оно только что закрыло намеренно.
        _events.Writer.TryWrite(new SipTransportEvent.Cancelled());
        _events.Writer.TryComplete();

        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose()
    {
        CloseSocket();
        _writing.Dispose();
        _lifetime.Dispose();
    }

    private void CloseSocket()
    {
        Socket? socket;
        SslStream? stream;
        lock (_gate)
        {
            socket = _socket;
            stream = _stream;
            _socket = null;
            _stream = null;
        }

        // Поток первым: он владеет своим NetworkStream и закрывает его сам,
        // а сокет под ним переживает это спокойно — `ownsSocket: false`.
        stream?.Dispose();
        socket?.Dispose();
    }

    // Соединение

    private async Task RunAsync(CancellationToken token)
    {
        int attempt = 0;

        while (!token.IsCancellationRequested)
        {
            string? failure = await ConnectAsync(token).ConfigureAwait(false);
            if (failure is null)
            {
                // Соединение поднялось: дальше живём в цикле приёма, и выходим
                // из него только вместе с каналом.
                await ReceiveLoopAsync(token).ConfigureAwait(false);
                return;
            }

            _events.Writer.TryWrite(new SipTransportEvent.Failed(failure));

            TimeSpan delay = ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)];
            attempt++;

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Поднимает соединение. Возвращает причину отказа или <see langword="null"/>
    /// при успехе.
    /// </summary>
    private async Task<string?> ConnectAsync(CancellationToken token)
    {
        try
        {
            IPAddress[] addresses = IPAddress.TryParse(Remote.Host, out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(Remote.Host, token).ConfigureAwait(false);

            if (addresses.Length == 0)
            {
                return $"имя {Remote.Host} не разрешается";
            }

            var endpoint = new IPEndPoint(addresses[0], Remote.Port);
            var socket = new Socket(
                endpoint.AddressFamily,
                Transport == SipTransport.Udp ? SocketType.Dgram : SocketType.Stream,
                Transport == SipTransport.Udp ? ProtocolType.Udp : ProtocolType.Tcp);

            // Соединённый UDP-сокет, а не «отправить кому угодно», и это не
            // мелочь: он приносит локальный адрес, отсекает чужие датаграммы на
            // нашем порту и доносит ICMP-отказ как ошибку сокета. Без него
            // «порт закрыт» выглядело бы как молчание сервера.
            await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);

            SslStream? stream = null;
            if (Transport == SipTransport.Tls)
            {
                stream = await HandshakeAsync(socket, token).ConfigureAwait(false);
                if (stream is null)
                {
                    // Причина уже описана внутри — там же, где известно, чем
                    // именно кончилось рукопожатие.
                    socket.Dispose();
                    return _handshakeFailure;
                }
            }

            lock (_gate)
            {
                if (_isStopped)
                {
                    stream?.Dispose();
                    socket.Dispose();
                    return null;
                }
                _stream?.Dispose();
                _socket?.Dispose();
                _socket = socket;
                _stream = stream;
            }

            var local = socket.LocalEndPoint as IPEndPoint;
            _events.Writer.TryWrite(new SipTransportEvent.Ready(
                new SipEndpoint(local?.Address.ToString() ?? "0.0.0.0", (ushort)(local?.Port ?? 0))));

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException error)
        {
            return SipTransportFailureText.Describe(error.SocketErrorCode, Remote, Transport);
        }
    }

    /// <summary>
    /// Рукопожатие TLS. <see langword="null"/> — не сошлось, причина в
    /// <see cref="_handshakeFailure"/>.
    /// </summary>
    ///
    /// <remarks>
    /// Проверка сертификата — своя, а не системная, и это решение, а не
    /// удобство: пиннинг по отпечатку нужен лаборатории с самоподписанным
    /// сертификатом, и в <c>Network.framework</c> оригинала он давался
    /// параметром. В .NET его пишут руками через
    /// <see cref="RemoteCertificateValidationCallback"/> — то есть проверка
    /// целиком наша, включая ту, что делает система.
    ///
    /// Отсюда важное: в режиме <see cref="SipTlsTrust.System"/> мы не
    /// «пропускаем всё, что система одобрила», а просто отдаём ей решение
    /// нетронутым — <c>errors == None</c> и ничего больше. Всякая попытка
    /// «поправить» её вердикт кончилась бы своей, худшей проверкой.
    /// </remarks>
    private async Task<SslStream?> HandshakeAsync(Socket socket, CancellationToken token)
    {
        SslStream stream = new(
            new NetworkStream(socket, ownsSocket: false),
            leaveInnerStreamOpen: false,
            userCertificateValidationCallback: ValidateCertificate);

        try
        {
            await stream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = _serverName,

                    // Ниже 1.2 не опускаемся. Asterisk 13 умеет 1.2, а 1.0 и 1.1
                    // объявлены негодными много лет назад; оставить их значило бы
                    // дать серверу выбрать худшее из возможного.
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
                token).ConfigureAwait(false);

            return stream;
        }
        catch (Exception error) when (error is AuthenticationException or IOException
                                          or SocketException or ObjectDisposedException)
        {
            _handshakeFailure = SipTransportFailureText.DescribeTlsFailure(Remote, error.Message);
            await stream.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            _handshakeFailure = null;
            await stream.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Проверка сертификата сервера по выбранному режиму доверия.</summary>
    private bool ValidateCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        switch (_trust)
        {
            case SipTlsTrust.System:
                return errors == SslPolicyErrors.None;

            case SipTlsTrust.PinnedCertificateSha256 pinned:
                // Пиннинг заменяет системную проверку целиком, а не дополняет
                // её: самоподписанный сертификат лаборатории её не проходит по
                // построению, и требовать оба условия сразу значило бы, что
                // пиннинг не работает никогда.
                //
                // Сверяется отпечаток самого сертификата (DER), а не цепочки:
                // цепочки у самоподписанного нет, а подменивший его подменит и
                // её.
                if (certificate is null)
                {
                    return false;
                }

                return SipTlsPinning.Matches(pinned.Fingerprints, certificate.GetRawCertData());

            case SipTlsTrust.AcceptAnyCertificateInsecurely:
                // Защиты от перехвата здесь нет вовсе — см. описание режима.
                return true;

            default:
                return false;
        }

    }

    // Приём

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        byte[] buffer = new byte[ReceiveBufferSize];

        while (!token.IsCancellationRequested)
        {
            Socket? socket;
            SslStream? stream;
            lock (_gate)
            {
                socket = _socket;
                stream = _stream;
            }
            if (socket is null)
            {
                return;
            }

            int read;
            try
            {
                read = stream is null
                    ? await socket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false)
                    : await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException error)
            {
                // Отказ чтения из TLS — это конец соединения: перезаводить
                // чтение не на чем. Тот же случай, что и отказ приёма на голом
                // TCP, и обходится так же — Closed, а не Failed.
                Close(error.InnerException is SocketException socketError
                    ? SipTransportFailureText.Describe(socketError.SocketErrorCode, Remote, Transport)
                    : $"соединение TLS оборвалось: {error.Message}");
                return;
            }
            catch (SocketException error)
            {
                string reason = SipTransportFailureText.Describe(error.SocketErrorCode, Remote, Transport);

                // На UDP отказ приёма — это доехавший ICMP, а не конец канала:
                // сокет цел, и следующая датаграмма уйдёт по нему же. Так
                // выглядит выключенный на минуту Asterisk, и объявлять из-за
                // этого канал мёртвым значит требовать пересборки транспорта там,
                // где достаточно подождать. Наверх уходит Failed — «жив, но не
                // получилось», — и приём продолжается.
                if (Transport == SipTransport.Udp && IsTransient(error.SocketErrorCode))
                {
                    _events.Writer.TryWrite(new SipTransportEvent.Failed(reason));
                    continue;
                }

                // На потоке всё иначе: приём отказал — значит соединение
                // кончилось, перезаводить приём на нём нечем, а другого способа
                // получать сообщения у нас нет. Это тот самый случай, ради
                // которого заведено Closed: иначе здесь молча умирает весь SIP, а
                // снаружи это выглядит как «зарегистрирован, но звонки не
                // приходят».
                Close(reason);
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (read == 0)
            {
                if (Transport == SipTransport.Udp)
                {
                    // Пустая датаграмма законна и ничего не значит.
                    continue;
                }

                // Ноль байт на потоке означает закрытие с другой стороны.
                //
                // Это Closed, а не Cancelled: Cancelled означает «закрыли мы
                // сами», и обходиться с чужим закрытием как со своим значит
                // остаться без регистрации молча. Перезапуск Asterisk и разрыв
                // TCP по таймауту приходят именно сюда.
                Close("сервер закрыл соединение");
                return;
            }

            if (Transport == SipTransport.Udp)
            {
                // Одна датаграмма — одно сообщение. Фреймер тут не нужен и
                // только мешал бы: датаграмма без Content-Length законна.
                _events.Writer.TryWrite(new SipTransportEvent.Received(buffer.AsSpan(0, read).ToArray()));
                continue;
            }

            _framer.Append(buffer.AsSpan(0, read));
            try
            {
                while (_framer.NextMessageData() is byte[] message)
                {
                    _events.Writer.TryWrite(new SipTransportEvent.Received(message));
                }
            }
            catch (SipFramingException error)
            {
                // Испорченный поток не чинится: границы сообщений потеряны, и
                // всё, что придёт дальше, разберётся мусором. Канал закрывается
                // насовсем, и его пересоберут.
                Close($"поток испорчен: {error.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// Отказ, после которого сокет ещё годен.
    ///
    /// Все три кода приезжают из ICMP в ответ на нашу датаграмму: адресат жив,
    /// маршрут есть, а порт или хост в эту минуту недоступны.
    /// </summary>
    private static bool IsTransient(SocketError error) =>
        error is SocketError.ConnectionReset
            or SocketError.ConnectionRefused
            or SocketError.HostUnreachable
            or SocketError.NetworkUnreachable
            or SocketError.MessageSize;

    private void Close(string reason)
    {
        CloseSocket();
        _events.Writer.TryWrite(new SipTransportEvent.Closed(reason));
        _events.Writer.TryComplete();
    }
}
