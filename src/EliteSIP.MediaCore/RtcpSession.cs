using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace EliteSIP.MediaCore;

/// <summary>Что видит собеседник про наш поток.</summary>
/// <param name="FractionLost">Доля потерь за последний интервал, 0…1.</param>
/// <param name="JitterMilliseconds">Джиттер в миллисекундах.</param>
/// <param name="RoundTripTime">Задержка кругового обхода, если её удалось посчитать.</param>
public sealed record RemoteMediaView(
    double FractionLost,
    int CumulativeLost,
    double JitterMilliseconds,
    TimeSpan? RoundTripTime,
    DateTimeOffset UpdatedAt)
{
    public string Summary
    {
        get
        {
            List<string> parts =
            [
                string.Create(CultureInfo.CurrentCulture, $"потери {FractionLost * 100:F1} %"),
                string.Create(CultureInfo.CurrentCulture, $"джиттер {JitterMilliseconds:F1} мс"),
            ];

            if (RoundTripTime is TimeSpan roundTrip)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"круг {roundTrip.TotalMilliseconds:F0} мс"));
            }

            return string.Join(", ", parts);
        }
    }
}

/// <summary>Что нужно знать, чтобы составить отчёт. Заполняет владелец потока.</summary>
public sealed record LocalMediaStatistics
{
    public uint PacketsSent { get; init; }

    public uint OctetsSent { get; init; }

    public uint RtpTimestamp { get; init; }

    public uint? RemoteSsrc { get; init; }

    /// <summary>Доля потерь принятого потока, 0…1.</summary>
    public double FractionLost { get; init; }

    public int CumulativeLost { get; init; }

    public uint HighestSequenceNumber { get; init; }

    /// <summary>Джиттер принятого потока в единицах часов RTP.</summary>
    public uint Jitter { get; init; }
}

/// <summary>
/// Обмен отчётами RTCP на соседнем порту.
///
/// По RFC 3550 §11 RTCP живёт на порту RTP плюс один — именно поэтому порт под
/// RTP выбирается чётным.
///
/// Что это даёт на практике. Своя статистика отвечает только на вопрос «что мы
/// приняли». Жалоба почти всегда обратная: «меня плохо слышно», — и проверить
/// её нечем, потому что наш собственный поток мы не слышим. В отчёте приёмника
/// от собеседника приезжают доля потерь нашего потока, джиттер и время
/// кругового обхода, посчитанные им.
/// </summary>
public sealed class RtcpSession : IDisposable
{
    /// <summary>
    /// Как часто слать отчёты.
    ///
    /// Пять секунд — минимум, разрешённый RFC 3550 §6.2 для двусторонней
    /// сессии. Чаще не нужно и вредно: RTCP не должен занимать больше пяти
    /// процентов полосы разговора.
    /// </summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(5);

    private static readonly IPEndPoint AnySource = new(IPAddress.Any, 0);

    private readonly uint _ssrc;
    private readonly string _canonicalName;
    private readonly uint _clockRate;
    private readonly Socket _socket;
    private IPEndPoint _remote;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Метка последнего отчёта собеседника и время его получения — из них
    /// считается задержка, которую мы возвращаем ему обратно.
    /// </summary>
    private (uint MiddleBits, DateTimeOffset ReceivedAt)? _lastRemoteReport;

    private Task? _receiveLoop;
    private Task? _reportLoop;
    private bool _isStopped;
    private bool _isStarted;

    /// <param name="boundSocket">
    /// Уже привязанный сокет — обычно из
    /// <see cref="RtpPortReservation.TakeRtcpSocket"/>. Причина та же, что у
    /// потока RTP: порт не должен освобождаться между резервацией и разговором
    /// даже на миллисекунду.
    /// </param>
    public RtcpSession(
        uint ssrc,
        string canonicalName,
        uint clockRate,
        ushort localPort,
        string remoteHost,
        ushort remotePort,
        Socket? boundSocket = null)
    {
        _ssrc = ssrc;
        _canonicalName = canonicalName;
        _clockRate = clockRate;
        _remote = new IPEndPoint(IPAddress.Parse(remoteHost), remotePort);

        if (boundSocket is not null)
        {
            _socket = boundSocket;
            return;
        }

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true,
        };
        _socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
    }

    /// <summary>Переводит отчёты на другое плечо собеседника, не трогая сокет.</summary>
    public void Retarget(string remoteHost, ushort remotePort)
    {
        ArgumentNullException.ThrowIfNull(remoteHost);

        IPEndPoint replacement = new(IPAddress.Parse(remoteHost), remotePort);
        lock (_lock)
        {
            _remote = replacement;
        }
    }

    public Action<RemoteMediaView>? OnRemoteView { get; set; }

    public Action<string>? OnDiagnostic { get; set; }

    /// <summary>Откуда брать свежую статистику в момент отправки отчёта.</summary>
    public Func<LocalMediaStatistics>? StatisticsProvider { get; set; }

    public void Start()
    {
        _isStarted = true;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _reportLoop = Task.Run(ReportLoopAsync);
    }

    /// <summary>
    /// Закрывает поток, попрощавшись, и дожидается, пока порт освободится.
    ///
    /// Ждать приходится ради пересогласования — ровно то же, что и у
    /// <see cref="RtpSession"/>. Провал привязки в UDP ничем не мешает
    /// разговору — просто отчёты собеседника перестают приходить, и понять это
    /// по звуку невозможно.
    /// </summary>
    public void Stop(TimeSpan? waitingForReleaseUpTo = null)
    {
        lock (_lock)
        {
            if (_isStopped)
            {
                return;
            }

            _isStopped = true;
        }

        if (_isStarted)
        {
            SendRaw(Rtcp.EncodeGoodbye(_ssrc));
        }

        _stopping.Cancel();
        _socket.Close();

        TimeSpan timeout = waitingForReleaseUpTo ?? TimeSpan.FromMilliseconds(500);
        _receiveLoop?.Wait(timeout);
        _reportLoop?.Wait(timeout);
    }

    public void Dispose()
    {
        Stop();
        _stopping.Dispose();
        _socket.Dispose();
    }

    /// <summary>
    /// Составляет очередной отчёт. Открыт для теста: содержимое отчёта — это
    /// то, по чему собеседник судит о нашем потоке, и проверять его через
    /// сокет и пятисекундное ожидание значило бы не проверять вовсе.
    /// </summary>
    internal byte[]? BuildReport(DateTimeOffset now)
    {
        if (StatisticsProvider?.Invoke() is not LocalMediaStatistics statistics)
        {
            return null;
        }

        ulong ntp = Rtcp.NtpTimestamp(now);
        List<RtcpReportBlock> blocks = [];

        if (statistics.RemoteSsrc is uint remoteSsrc)
        {
            // Задержка с момента получения последнего отчёта собеседника — в
            // 1/65536 секунды. По ней он и посчитает время кругового обхода.
            uint delay = 0;
            if (_lastRemoteReport is (uint _, DateTimeOffset receivedAt))
            {
                delay = (uint)Math.Min((now - receivedAt).TotalSeconds * 65536, uint.MaxValue);
            }

            blocks.Add(new RtcpReportBlock(
                remoteSsrc,
                statistics.FractionLost,
                statistics.CumulativeLost,
                statistics.HighestSequenceNumber,
                statistics.Jitter,
                _lastRemoteReport?.MiddleBits ?? 0,
                _lastRemoteReport is null ? 0 : delay));
        }

        // Отчёт отправителя, если мы говорим, и приёмника, если только слушаем.
        byte[] report = statistics.PacketsSent > 0
            ? Rtcp.Encode(new RtcpSenderReport(
                _ssrc,
                ntp,
                statistics.RtpTimestamp,
                statistics.PacketsSent,
                statistics.OctetsSent,
                blocks))
            : Rtcp.Encode(new RtcpReceiverReport(_ssrc, blocks));

        return Rtcp.Compound(report, _ssrc, _canonicalName);
    }

    /// <summary>Разбор входящего пакета. Открыт по той же причине, что и сборка отчёта.</summary>
    internal void Handle(IReadOnlyList<RtcpPacket> packets, DateTimeOffset now)
    {
        foreach (RtcpPacket packet in packets)
        {
            switch (packet)
            {
                case RtcpSenderReport report:
                    // Запоминаем метку: собеседник ждёт её обратно, чтобы
                    // посчитать время кругового обхода со своей стороны.
                    _lastRemoteReport = (Rtcp.MiddleBits(report.NtpTimestamp), now);
                    Publish(report.Reports, now);
                    break;

                case RtcpReceiverReport report:
                    Publish(report.Reports, now);
                    break;

                case RtcpGoodbye:
                    OnDiagnostic?.Invoke("собеседник закрыл поток RTCP");
                    break;

                case RtcpSourceDescription:
                    // Штатная часть каждого составного отчёта, и говорить о ней
                    // нечего: имя источника нужно тем, кто сводит несколько
                    // потоков, а у софтфона собеседник один. Раньше она попадала
                    // в общую ветку и засоряла журнал каждые пять секунд.
                    break;

                case RtcpOther other:
                    OnDiagnostic?.Invoke($"RTCP: пакет типа {other.Type} пропущен");
                    break;

                default:
                    break;
            }
        }
    }

    private void Publish(IReadOnlyList<RtcpReportBlock> blocks, DateTimeOffset now)
    {
        foreach (RtcpReportBlock block in blocks)
        {
            // Отчёты про чужие источники нас не касаются: в конференции их
            // может приехать несколько, а интересен только наш собственный
            // поток.
            if (block.SourceSsrc != _ssrc)
            {
                continue;
            }

            OnRemoteView?.Invoke(new RemoteMediaView(
                block.FractionLost,
                block.CumulativeLost,
                (double)block.Jitter / _clockRate * 1000,
                block.RoundTripTime(Rtcp.MiddleBits(Rtcp.NtpTimestamp(now))),
                now));
        }
    }

    private async Task ReportLoopAsync()
    {
        // Первый отчёт с задержкой: сразу после установления соединения слать
        // нечего — ни одного пакета ещё не принято.
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ReportInterval, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (BuildReport(DateTimeOffset.UtcNow) is byte[] report)
            {
                SendRaw(report);
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[2048];

        while (!_stopping.IsCancellationRequested)
        {
            int received;
            try
            {
                SocketReceiveFromResult result = await _socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, AnySource, _stopping.Token)
                    .ConfigureAwait(false);
                received = result.ReceivedBytes;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // Отказ приёма RTCP разговору не мешает: продолжаем слушать.
                continue;
            }

            if (received <= 0)
            {
                continue;
            }

            // Чужой или битый пакет молча пропускаем: на открытый UDP-порт
            // прилетает что угодно, и рвать разговор из-за этого нельзя.
            try
            {
                Handle(Rtcp.Parse(buffer.AsSpan(0, received)), DateTimeOffset.UtcNow);
            }
            catch (RtcpParseException)
            {
                continue;
            }
        }
    }

    private void SendRaw(byte[] data)
    {
        try
        {
            _socket.SendTo(data, _remote);
        }
        catch (SocketException error)
        {
            OnDiagnostic?.Invoke($"RTCP не отправлен: {error.Message}");
        }
        catch (ObjectDisposedException)
        {
            // Штатная остановка.
        }
    }
}
