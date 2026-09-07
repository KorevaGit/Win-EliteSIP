using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace EliteSIP.SipCore.Udp;

/// <summary>
/// Транспорт SIP на <c>System.Net.Sockets</c>: UDP или TCP.
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
/// TLS здесь не поддержан намеренно: он приезжает на этапе W11 вместе со
/// <c>SslStream</c>, пиннингом сертификата и своей диагностикой. Попытка создать
/// TLS-канал отказывает сразу и вслух, а не притворяется работающей.
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

    private Socket? _socket;
    private Task? _worker;
    private bool _isStopped;

    public SocketSipTransport(SipEndpoint remote, SipTransport transport)
    {
        if (transport == SipTransport.Tls)
        {
            throw new NotSupportedException(
                "TLS для сигнализации появится на этапе W11 вместе с пиннингом сертификата");
        }

        Remote = remote;
        Transport = transport;
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
        lock (_gate)
        {
            socket = _socket ?? throw new IOException("канал ещё не поднялся");
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
        _lifetime.Dispose();
    }

    private void CloseSocket()
    {
        Socket? socket;
        lock (_gate)
        {
            socket = _socket;
            _socket = null;
        }
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

            lock (_gate)
            {
                if (_isStopped)
                {
                    socket.Dispose();
                    return null;
                }
                _socket?.Dispose();
                _socket = socket;
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

    // Приём

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        byte[] buffer = new byte[ReceiveBufferSize];

        while (!token.IsCancellationRequested)
        {
            Socket? socket;
            lock (_gate)
            {
                socket = _socket;
            }
            if (socket is null)
            {
                return;
            }

            int read;
            try
            {
                read = await socket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
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
