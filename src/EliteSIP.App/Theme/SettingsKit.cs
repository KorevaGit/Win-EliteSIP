using System.Windows;
using System.Windows.Controls;

namespace EliteSIP.App.Theme;

/// <summary>
/// Кирпичи раскладки настроек, общие для менеджерского окна и «Управления».
/// </summary>
///
/// <remarks>
/// В оригинале это <c>SettingsKit.swift</c>, и вынесены они были ровно по той
/// причине, по которой вынесены здесь: два окна на одних правилах, и копия
/// означала бы расхождение — в macOS-версии из-за него в «Управлении» год висел
/// прежний текст предупреждения про эхо, переписанный у менеджера этапом
/// раньше.
///
/// Единственное, что различается у двух окон, — ширина колонки подписей:
/// менеджерские 72 точки посчитаны по «Громкости» и «Микрофону», а у
/// администратора есть «Отображаемое имя» и «Добавочный комнаты». Поэтому
/// колонка не зашита в кирпич, а наследуется от окна.
/// </remarks>
public static class SettingsLayout
{
    /// <summary>Ширина колонки подписей. Наследуется вниз по дереву.</summary>
    ///
    /// <remarks>
    /// Одна на все строки окна: иначе контролы соседних строк не стоят в
    /// колонку, и глаз ищет их заново на каждой.
    /// </remarks>
    public static readonly DependencyProperty LabelColumnProperty = DependencyProperty.RegisterAttached(
        "LabelColumn",
        typeof(double),
        typeof(SettingsLayout),
        new FrameworkPropertyMetadata(72.0, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Потолок ширины строки «подпись — управление».</summary>
    ///
    /// <remarks>
    /// Без него растяжимое окно даёт ползунок разброса длиной во весь монитор,
    /// и окно читается как сломанное, а не как просторное.
    /// </remarks>
    public static readonly DependencyProperty RowMaxWidthProperty = DependencyProperty.RegisterAttached(
        "RowMaxWidth",
        typeof(double),
        typeof(SettingsLayout),
        new FrameworkPropertyMetadata(420.0, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetLabelColumn(DependencyObject element)
        => (double)element.GetValue(LabelColumnProperty);

    public static void SetLabelColumn(DependencyObject element, double value)
        => element.SetValue(LabelColumnProperty, value);

    public static double GetRowMaxWidth(DependencyObject element)
        => (double)element.GetValue(RowMaxWidthProperty);

    public static void SetRowMaxWidth(DependencyObject element, double value)
        => element.SetValue(RowMaxWidthProperty, value);
}

/// <summary>Заголовок группы и плашка под её строками.</summary>
///
/// <remarks>
/// Заголовок над плашкой, а не внутри: так он читается как имя группы, а не как
/// её первая строка.
/// </remarks>
public sealed class SettingsSection : HeaderedItemsControl
{
    static SettingsSection()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SettingsSection),
            new FrameworkPropertyMetadata(typeof(SettingsSection)));
    }
}

/// <summary>Строка «подпись — управление».</summary>
public sealed class SettingsRow : HeaderedContentControl
{
    static SettingsRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SettingsRow),
            new FrameworkPropertyMetadata(typeof(SettingsRow)));
    }
}

/// <summary>
/// Всё, у чего своей подписи нет, всё равно начинается от колонки управления:
/// иначе страница расслаивается на два левых края.
/// </summary>
public sealed class SettingsIndented : ContentControl
{
    static SettingsIndented()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SettingsIndented),
            new FrameworkPropertyMetadata(typeof(SettingsIndented)));
    }
}

/// <summary>Пояснение или состояние — мелким, во второй колонке.</summary>
///
/// <remarks>
/// Был заход пустить пояснения во всю ширину блока ради высоты. Экономия
/// вышла, но страница расслоилась на два левых края: подписи и управление по
/// одной вертикали, пояснения по другой.
/// </remarks>
public sealed class SettingsNote : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(SettingsNote),
        new PropertyMetadata(string.Empty));

    /// <summary>Тревожное — красным. Обычное пояснение — второстепенным цветом.</summary>
    public static readonly DependencyProperty IsAlarmingProperty = DependencyProperty.Register(
        nameof(IsAlarming),
        typeof(bool),
        typeof(SettingsNote),
        new PropertyMetadata(false));

    static SettingsNote()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SettingsNote),
            new FrameworkPropertyMetadata(typeof(SettingsNote)));
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsAlarming
    {
        get => (bool)GetValue(IsAlarmingProperty);
        set => SetValue(IsAlarmingProperty, value);
    }
}

/// <summary>Черта между блоками одного раздела.</summary>
///
/// <remarks>
/// Начинается от колонки управления, как и всё остальное: черта во всю ширину
/// плашки прочертила бы и колонку подписей, разрезав раздел пополам.
/// </remarks>
public sealed class SettingsDivider : Control
{
    static SettingsDivider()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SettingsDivider),
            new FrameworkPropertyMetadata(typeof(SettingsDivider)));
    }
}
