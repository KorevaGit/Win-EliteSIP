using System.Windows;

namespace EliteSIP.App.Panel;

/// <summary>Панель софтфона — единственное окно, которое оператор видит весь день.</summary>
public partial class PanelWindow : Window
{
    public PanelWindow() => InitializeComponent();

    private void OnMinimizeClick(object sender, RoutedEventArgs eventArgs)
        => WindowState = WindowState.Minimized;

    // Закрытие панели — выход из приложения, а не сворачивание в значок.
    // Форма приложения решается вместе со значком в области уведомлений
    // (шаг «строка меню» этапа), и до тех пор кнопка делает то, что на ней
    // написано: скрытая панель без значка означала бы работающий софтфон,
    // которого нигде не видно.
    private void OnCloseClick(object sender, RoutedEventArgs eventArgs) => Close();
}
