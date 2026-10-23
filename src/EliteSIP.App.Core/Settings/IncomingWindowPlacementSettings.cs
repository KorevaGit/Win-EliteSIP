using EliteSIP.CallGuard;
using EliteSIP.App.Panel;

namespace EliteSIP.App.Settings;

/// <summary>Место карточки входящего в пикселях экрана. Не часть политики защиты.</summary>
///
/// <remarks>
/// Пиксели — те, в которых ОС ставит окно: на Windows физические, на Linux —
/// пиксели корневого окна X. Файл настроек между ОС не переносится, поэтому
/// перевода нет; чужая точка всё равно вдвигается в рабочую область.
/// </remarks>
public sealed class IncomingWindowPlacementSettings : Observable
{
    private ScreenPoint? _origin;

    public ScreenPoint? Origin
    {
        get => _origin;
        set => Set(ref _origin,
            value is { } point && double.IsFinite(point.X) && double.IsFinite(point.Y) ? value : null);
    }
}
