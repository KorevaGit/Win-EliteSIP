# Win EliteSIP — заметки для агентов

- Сборка: `dotnet build WinEliteSIP.slnx -c Debug` (должна быть без ошибок и предупреждений).
- Тесты: `dotnet test WinEliteSIP.slnx --no-build` после сборки; окно WPF проверяется тестами в `tests/EliteSIP.App.Tests` (`WpfHost.Layout`, `RenderTargetBitmap` для снимков).
- Общий код с Linux-версией: `Linux EliteSip` (ветка `origin/main`, каталог `core/`) — правки там переносятся в `src/EliteSIP.App` и `src/EliteSIP.PanelLink` с теми же путями; заметки о выпуске Linux — `patchnote.md`, снимки — `newui/`.
- Журнал изменений — `Mychanges.md`. Серверные правки: Spark — ветка `support`, elitesip_update — ветка `dev`.
