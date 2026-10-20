using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using EliteSIP.App.Panel;
using KnockSequence = EliteSIP.SipCore.PortKnockSequence;
using KnockStep = EliteSIP.SipCore.PortKnockStep;

namespace EliteSIP.App.Settings;

/// <summary>Один шаг стука в настройках: адрес, длина данных, сколько раз.</summary>
///
/// <remarks>
/// Свой тип, а не <c>PortKnockStep</c> из <c>SipCore</c>: тот неизменяемый — им
/// стучат, — а этот правится в «Управлении» построчно и обязан уведомлять окно.
/// Та же развилка, что у клавиш макросов.
/// </remarks>
public sealed class PortKnockStepSetting : Observable
{
    private string _host = string.Empty;
    private int _payloadBytes = 100;
    private int _count = 1;

    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Пустой адрес означает «адрес АТС из настроек».</summary>
    public string Host
    {
        get => _host;
        set => Set(ref _host, value);
    }

    /// <summary>Байты данных ICMP — то же, что <c>-s</c> у <c>ping</c>.</summary>
    ///
    /// <remarks>
    /// Потолок 65 500 — предел данных ICMP; пол ноль, потому что пустой пакет
    /// это тоже подпись, и запрещать его нам не за что.
    /// </remarks>
    public int PayloadBytes
    {
        get => _payloadBytes;
        set => Set(ref _payloadBytes, Math.Clamp(value, 0, 65_500));
    }

    /// <summary>Сколько пакетов подряд — то же, что <c>-c</c>.</summary>
    public int Count
    {
        get => _count;
        set => Set(ref _count, Math.Clamp(value, 1, 16));
    }
}

/// <summary>
/// Стук по портам: последовательность, которой машина открывает себе дорогу до
/// АТС.
/// </summary>
///
/// <remarks>
/// <b>Это механизм связности, а не защиты.</b> Стук решает, кого пускают, и
/// ничего не говорит о том, кто может прочитать: SIP и RTP как шли открытым UDP,
/// так и идут.
///
/// Пустой список шагов — выключенный стук. Умолчание — боевая
/// последовательность из скрипта удалённого подключения: она живёт на чужом
/// шлюзе, и менять её придётся правкой настроек или предустановкой, а не
/// пересборкой приложения.
/// </remarks>
public sealed class PortKnockSettings : Observable
{
    private double _spacingSeconds = 1;
    private double _repeatIntervalSeconds = 600;

    public PortKnockSettings()
    {
        foreach (var step in KnockSequence.Production.Steps)
        {
            Steps.Add(new PortKnockStepSetting
            {
                Host = step.Host,
                PayloadBytes = step.PayloadBytes,
                Count = step.Count,
            });
        }
    }

    public ObservableCollection<PortKnockStepSetting> Steps { get; init; } = [];

    /// <summary>Пауза между пакетами. Секунда — темп обычного <c>ping</c>.</summary>
    public double SpacingSeconds
    {
        get => _spacingSeconds;
        set => Set(ref _spacingSeconds, Math.Clamp(value, 0.1, 10));
    }

    /// <summary>Как часто повторять стук, пока всё работает.</summary>
    public double RepeatIntervalSeconds
    {
        get => _repeatIntervalSeconds;
        set => Set(ref _repeatIntervalSeconds, Math.Clamp(value, 60, 3600));
    }

    [JsonIgnore]
    public bool IsEnabled => Steps.Count > 0;

    /// <summary>Во что это превращается для стучащего.</summary>
    public KnockSequence ToSequence() => new()
    {
        Steps = [.. Steps.Select(step => new KnockStep(step.PayloadBytes, step.Host, step.Count)
        {
            Id = step.Id,
        })],
        SpacingSeconds = SpacingSeconds,
        RepeatIntervalSeconds = RepeatIntervalSeconds,
    };
}
