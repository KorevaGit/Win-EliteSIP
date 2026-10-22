using EliteSIP.CallGuard;
using EliteSIP.App.Panel;

namespace EliteSIP.App.Settings;

/// <summary>Место карточки входящего в физических пикселях. Не часть политики защиты.</summary>
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
