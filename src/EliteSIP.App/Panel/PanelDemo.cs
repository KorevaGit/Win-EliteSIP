namespace EliteSIP.App.Panel;

/// <summary>
/// Заполняет панель показательным состоянием — чтобы вёрстку можно было
/// проверить снимком экрана, не поднимая звонка.
/// </summary>
///
/// <remarks>
/// Тот же приём, что в оригинале: там панель понимала <c>--demo-incoming</c>,
/// <c>--open-history</c> и <c>--call-on-launch</c>, и по той же причине —
/// раскладку сверяют снимком живого окна, а дотянуться до окна скриптом иначе
/// нечем.
///
/// Здесь это не отладочная лазейка в бою: слоя приложения ещё нет, и до его
/// появления единственный способ увидеть середину панели заполненной — задать
/// ей состояние руками. Когда приедет настоящая модель, останутся ровно те
/// ключи, которыми проверяют раскладку, а состояние будет настоящим.
///
///     EliteSIP.App.exe --demo idle|call|two-lines|transfer
/// </remarks>
internal static class PanelDemo
{
    public static void Apply(PanelViewModel model, Settings.AppSettings settings, IReadOnlyList<string> arguments)
    {
        var index = arguments.ToList().IndexOf("--demo");
        if (index < 0)
        {
            return;
        }

        var scene = index + 1 < arguments.Count ? arguments[index + 1] : "idle";

        model.Registration = RegistrationState.Registered;
        model.StatusTitle = "172";
        model.StatusLabel = "Офис";
        model.CanPlaceCall = true;

        // Учётка показательная: раздел «Работа» без адреса АТС выглядит
        // сломанным, а не пустым.
        settings.Account.Username = "172";
        settings.Account.Domain = "pbx.elite.local";

        // Подписи макросов — из тех, на которых у заказчика ломалась вёрстка:
        // одно длинное слово, два слова и короткое. Ровно три разных случая
        // переноса, которые обязана выдержать клавиша.
        model.Macros.Add(new MacroViewModel("Юрист", "*1"));
        model.Macros.Add(new MacroViewModel("Отдел продаж", "*2"));
        model.Macros.Add(new MacroViewModel("Бухгалтерия", "*4"));
        model.Macros.Add(new MacroViewModel("Склад", "*5"));

        switch (scene)
        {
            case "call":
                StartCall(model);
                break;

            case "two-lines":
                StartCall(model);
                model.Lines.Add(new CallLineViewModel
                {
                    Title = "Смирнов А.",
                    Status = "01:12 · удержание",
                    IsOnHold = true,
                    ConnectedAt = DateTimeOffset.Now.AddSeconds(-72),
                });
                break;

            case "transfer":
                StartCall(model);
                model.ShowTransferEntry();
                break;

            case "grow":
                // Проверка главного правила панели: середина меняет высоту, низ
                // не двигается. Поле перевода открывается само и по часам —
                // чтобы снять окно до и после, не трогая его мышью: щелчок мимо
                // кнопки испортил бы замер молча. Восемь секунд, а не три, —
                // окно WPF показывается не мгновенно, и первый снимок должен
                // застать панель уже нарисованной.
                StartCall(model);
                Delay(TimeSpan.FromSeconds(8), model.ShowTransferEntry);
                break;

            default:
                model.DialedNumber = "600";
                break;
        }
    }

    private static void Delay(TimeSpan delay, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };

        timer.Start();
    }

    private static void StartCall(PanelViewModel model)
    {
        var line = new CallLineViewModel
        {
            Title = "Вызов по сделке",
            SecondaryNumber = "712",
            Status = "00:34 · разговор",
            IsActive = true,
            ConnectedAt = DateTimeOffset.Now.AddSeconds(-34),
        };

        model.Lines.Add(line);
        model.ActiveLine = line;
        model.CallStatus = "разговор";
        model.CanSendDtmf = true;
    }
}
