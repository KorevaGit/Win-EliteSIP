namespace EliteSIP.Audio;

/// <summary>
/// Служебные звуки: гудки, подключение, отбой, тоны клавиш, рингтон и его прослушивание.
/// </summary>
///
/// <remarks>
/// Сигнатуры — ровно как у <c>SignalSoundPlayer</c> Windows-версии: интерфейс
/// снят с него 20 октября 2026, а не придуман заново, чтобы WPF-приложение не
/// заметило подмены. Linux-проигрыватель реализует его же. Устройство вывода — идентификатор из настроек; <c>null</c> —
/// системное.
/// </remarks>
public interface ISignalSounds : IDisposable
{
    /// <summary>Гудки «звонит», пока не снимут.</summary>
    public void StartRingback(string? deviceId, double volume = 0.35);

    public void StopRingback();

    /// <summary>Рингтон входящего: файл по кругу, пока не снимут.</summary>
    public void StartRingtone(string? filePath, double volume, string? deviceId);

    public void StopRingtone();

    /// <summary>
    /// Прослушать рингтон в настройках тем же путём, каким он звонит на
    /// входящем. <c>false</c> — файл не читается, и звучит стандартный.
    /// </summary>
    public bool PreviewRingtone(string? filePath, double volume, string? deviceId);

    public void StopPreview();

    /// <summary>Тон клавиши набора.</summary>
    public void PlayKeyTone(char key, string? deviceId);

    /// <summary>Короткий сигнал отбоя.</summary>
    public void PlayHangUp(string? deviceId);

    /// <summary>Тихое подтверждение подключения, только в устройстве оператора.</summary>
    public void PlayConnected(string? deviceId);
}
