namespace EliteSIP.CallGuard;

/// <summary>
/// Расчёт случайной позиции окна входящего вызова.
/// </summary>
///
/// <remarks>
/// Первая и самая дешёвая мера защиты: кликер по фиксированным координатам
/// ломается об неё целиком. Чистый расчёт без окон и без глобального состояния
/// — генератор случайных чисел передаётся снаружи, поэтому поведение
/// воспроизводимо и проверяется тестами.
/// </remarks>
public sealed class IncomingCallPlacement
{
    public IncomingCallPlacement(ScreenRect bounds, double minimumTravel, int maximumAttempts = 24)
    {
        Bounds = bounds;
        MinimumTravel = minimumTravel;
        MaximumAttempts = maximumAttempts;
    }

    /// <summary>Область, внутри которой окно вообще разрешено показывать.</summary>
    public ScreenRect Bounds { get; }

    /// <summary>Минимальное расстояние от предыдущей позиции.</summary>
    public double MinimumTravel { get; }

    /// <summary>
    /// Сколько раз пытаться попасть в требование по расстоянию, прежде чем взять
    /// лучшую из попыток. Без ограничения на маленьком экране цикл может не
    /// сойтись никогда.
    /// </summary>
    public int MaximumAttempts { get; }

    /// <summary>Прямоугольник допустимых левых-верхних углов окна заданного размера.</summary>
    public ScreenRect OriginBounds(ScreenSize size) => new(
        Bounds.MinX,
        Bounds.MinY,
        Math.Max(0, Bounds.Width - size.Width),
        Math.Max(0, Bounds.Height - size.Height));

    /// <summary>Возвращает рамку, целиком лежащую внутри разрешённой области.</summary>
    ///
    /// <remarks>
    /// Существует потому, что размер окна на момент выбора позиции — обещание, а
    /// не факт. Высоту окну считает содержимое, и приехать она может позже
    /// выбора точки; окно, выросшее после размещения, уезжает за край ровно на
    /// разницу. Позиция при этом остаётся случайной: рамку не пересчитывают, а
    /// вдвигают обратно на столько, на сколько она вылезла.
    ///
    /// Окно крупнее области прижимается к её углу — тем же решением, что и в
    /// <see cref="Origin"/>: лучше упереться в угол, чем разъехаться за две
    /// границы сразу.
    /// </remarks>
    public ScreenRect Contained(ScreenRect frame)
    {
        var x = frame.Width >= Bounds.Width
            ? Bounds.MinX
            : Math.Min(Math.Max(frame.X, Bounds.MinX), Bounds.MaxX - frame.Width);

        var y = frame.Height >= Bounds.Height
            ? Bounds.MinY
            : Math.Min(Math.Max(frame.Y, Bounds.MinY), Bounds.MaxY - frame.Height);

        return new ScreenRect(x, y, frame.Width, frame.Height);
    }

    /// <summary>Выбирает угол окна на этот вызов.</summary>
    public ScreenPoint Origin(ScreenSize size, ScreenPoint? previous, Random generator)
    {
        ArgumentNullException.ThrowIfNull(generator);

        var allowed = OriginBounds(size);

        // Окно шире или выше доступной области — прижимаем к углу, иначе
        // случайное смещение вытолкнет его за экран.
        if (allowed.Width <= 0 && allowed.Height <= 0)
        {
            return allowed.Origin;
        }

        ScreenPoint Candidate() => new(
            allowed.MinX + (allowed.Width > 0 ? generator.NextDouble() * allowed.Width : 0),
            allowed.MinY + (allowed.Height > 0 ? generator.NextDouble() * allowed.Height : 0));

        if (previous is not ScreenPoint from)
        {
            return Candidate();
        }

        var best = Candidate();
        var bestDistance = best.DistanceTo(from);

        var attempt = 1;
        while (attempt < MaximumAttempts && bestDistance < MinimumTravel)
        {
            var next = Candidate();
            var nextDistance = next.DistanceTo(from);
            if (nextDistance > bestDistance)
            {
                best = next;
                bestDistance = nextDistance;
            }

            attempt++;
        }

        return best;
    }
}
