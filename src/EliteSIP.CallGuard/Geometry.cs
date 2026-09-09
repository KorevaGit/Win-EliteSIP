namespace EliteSIP.CallGuard;

/// <summary>Точка экрана в аппаратных пикселях.</summary>
///
/// <remarks>
/// Свои типы, а не <c>System.Windows.Point</c> и <c>Rect</c>, ровно по той же
/// причине, по которой в оригинале расчёт лежал в пакете без AppKit: разбор
/// защиты обязан проверяться тестом без окон и без экрана. Ссылка на WPF
/// потянула бы за собой <c>net10.0-windows</c> и вместе с ним сборку, которую
/// на пустой машине без графики не запустить.
///
/// <b>Начало координат здесь не важно.</b> На macOS оно было в левом нижнем
/// углу, в Windows — в левом верхнем; вся математика ниже работает с
/// прямоугольниками и расстояниями, то есть одинакова в обеих системах.
/// Переводит границы экрана в эти координаты тот, кто их спрашивает у системы.
/// </remarks>
public readonly record struct ScreenPoint(double X, double Y)
{
    public double DistanceTo(ScreenPoint other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}

/// <summary>Размер окна в аппаратных пикселях.</summary>
public readonly record struct ScreenSize(double Width, double Height);

/// <summary>Прямоугольник экрана: область показа или рамка окна.</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public ScreenRect(ScreenPoint origin, ScreenSize size)
        : this(origin.X, origin.Y, size.Width, size.Height)
    {
    }

    public double MinX => X;

    public double MinY => Y;

    public double MaxX => X + Width;

    public double MaxY => Y + Height;

    public double MidX => X + (Width / 2);

    public double MidY => Y + (Height / 2);

    public ScreenPoint Origin => new(X, Y);

    public ScreenSize Size => new(Width, Height);

    /// <summary>Лежит ли чужая рамка целиком внутри этой.</summary>
    public bool Contains(ScreenRect other)
        => other.MinX >= MinX && other.MinY >= MinY && other.MaxX <= MaxX && other.MaxY <= MaxY;

    public bool Contains(ScreenPoint point)
        => point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;

    /// <summary>Рамка, поджатая со всех сторон на <paramref name="amount"/>.</summary>
    public ScreenRect Inset(double amount)
        => new(X + amount, Y + amount, Math.Max(0, Width - (2 * amount)), Math.Max(0, Height - (2 * amount)));

    /// <summary>Расстояние от точки до рамки; ноль, если точка внутри.</summary>
    public double DistanceTo(ScreenPoint point)
    {
        var dx = Math.Max(Math.Max(MinX - point.X, 0), point.X - MaxX);
        var dy = Math.Max(Math.Max(MinY - point.Y, 0), point.Y - MaxY);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
