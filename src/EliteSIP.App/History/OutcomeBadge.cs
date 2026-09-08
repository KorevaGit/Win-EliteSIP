using System.Windows;
using System.Windows.Controls;
using EliteSIP.CallHistory;

namespace EliteSIP.App.History;

/// <summary>
/// Значок исхода: кружок со стрелкой. Крупный — в истории он единственное, что
/// различает четыре состояния звонка, и читается первым.
/// </summary>
///
/// <remarks>
/// Залитый кружок значит «поговорили», обведённый — «нет». Направление стрелки
/// — входящий или исходящий. Цвет — исход: дозвонился, не ответили, не
/// дозвонился, пропущенный.
///
/// Тем же значком рисует себя кнопка фильтра, и это не переиспользование ради
/// экономии: кнопка обязана показывать ровно ту фигуру, которую человек будет
/// искать глазами в списке. Отсюда и отдельные свойства вместо записи —
/// собирать под кнопку поддельный звонок значило бы заводить звонок, которого
/// не было.
/// </remarks>
public sealed class OutcomeBadge : Control
{
    public static readonly DependencyProperty IsIncomingProperty = DependencyProperty.Register(
        nameof(IsIncoming),
        typeof(bool),
        typeof(OutcomeBadge),
        new PropertyMetadata(true));

    public static readonly DependencyProperty IsCompletedProperty = DependencyProperty.Register(
        nameof(IsCompleted),
        typeof(bool),
        typeof(OutcomeBadge),
        new PropertyMetadata(true));

    public static readonly DependencyProperty OutcomeProperty = DependencyProperty.Register(
        nameof(Outcome),
        typeof(CallOutcome),
        typeof(OutcomeBadge),
        new PropertyMetadata(CallOutcome.Completed));

    /// <summary>Сторона квадрата. В строке 22, на кнопке фильтра 14.</summary>
    ///
    /// <remarks>
    /// На кнопке меньше строчных 22, но не вдвое: на одиннадцати кольцо у
    /// «Пропущенных» вырождается в точку — толщина обводки там <c>size / 14</c>,
    /// то есть меньше точки экрана.
    /// </remarks>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size),
        typeof(double),
        typeof(OutcomeBadge),
        new PropertyMetadata(22.0));

    static OutcomeBadge()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(OutcomeBadge),
            new FrameworkPropertyMetadata(typeof(OutcomeBadge)));
    }

    public bool IsIncoming
    {
        get => (bool)GetValue(IsIncomingProperty);
        set => SetValue(IsIncomingProperty, value);
    }

    public bool IsCompleted
    {
        get => (bool)GetValue(IsCompletedProperty);
        set => SetValue(IsCompletedProperty, value);
    }

    public CallOutcome Outcome
    {
        get => (CallOutcome)GetValue(OutcomeProperty);
        set => SetValue(OutcomeProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }
}
