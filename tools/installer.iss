; Установщик EliteSIP для Windows.
;
; Собирается релизным скриптом:
;   iscc /DAppVersion=<версия> /DPublishDir=<каталог> /O<куда> installer.iss
;
; ------------------------------------------------------------------------------
; Почему помашинная установка, а не в профиль пользователя
; ------------------------------------------------------------------------------
;
; Аудит 9 сентября 2026 решил ставить в профиль (`%LOCALAPPDATA%\Programs`), чтобы
; обновление не просило UAC. Решение отменено 10 сентября: машины заказчика заперты
; политикой ограниченного использования программ (SRP) — уровень по умолчанию
; «Запрещено», и три правила пути с уровнем «Неограниченный»:
;
;   C:\Program Files\      C:\Program Files (x86)\      C:\Windows
;
; В профиле пользователя приложение не запустилось бы вовсе. Довод аудита при этом
; никуда не делся — оператор прав не имеет, — но решается он не местом установки, а
; задачей планировщика от SYSTEM (см. ниже).
;
; Инструкция заказчика («запрет на запуск и установку ПО на ПК») предписывает
; ставить всё ПО до включения блокировки. Отсюда `PrivilegesRequired=admin`: этот
; установщик проходит на этапе подготовки машины или под администратором позже.

#define AppName "EliteSIP"
#define AppPublisher "EliteSIP"
#define AppExe "EliteSIP.App.exe"
#define UpdaterExe "EliteSIP.Updater.exe"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #error PublishDir не задан: установщик собирается только релизным скриптом
#endif

; Заводская настройка обязана лежать в публикации, и сборка без неё
; останавливается.
;
; Цена её отсутствия выяснена дорого — половиной дня разбора 10 сентября 2026.
; Выпуск без этого файла ставится и работает: софтфон звонит, настройки
; правятся, ничего не падает. Не работает только линия панели — и узнать об
; этом можно лишь на **чистой** машине, где мастер первого запуска не даст
; ввести ключ. На машине, где файл когда-то положили руками, всё выглядит
; исправным: сброс его не стирает, и ключ вводится как ни в чём не бывало.
;
; То есть ошибка не видна ни сборщику, ни тому, кто проверяет на своей машине,
; — она видна только оператору на новом рабочем месте. Поэтому здесь стоит
; отказ сборки, а не предупреждение.
#if !FileExists(AddBackslash(PublishDir) + "provisioning.json")
  #error В публикации нет provisioning.json — линия панели, активация по ключу и обновления были бы выключены. Положите файл в каталог публикации.
#endif

[Setup]
; Идентификатор постоянен на всю жизнь продукта: по нему Windows понимает, что
; ставится не вторая копия, а новая версия той же программы. Менять его нельзя —
; поменянный оставит на машине две записи в «Программах и компонентах».
AppId={{7E3A9C41-5B2D-4F18-9A6E-C0D4B8E15A72}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; Права нужны: каталог назначения — `Program Files`, плюс правило межсетевого
; экрана и задача планировщика.
PrivilegesRequired=admin

; 64-разрядная установка на 64-разрядной системе. На 32-разрядной установщик
; уйдёт в `Program Files (x86)` — правило SRP есть и там.
ArchitecturesInstallIn64BitMode=x64compatible

OutputBaseFilename=EliteSIP-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Закрыть работающий софтфон перед заменой файлов — иначе файлы заняты.
CloseApplications=yes

; А вот поднимать его обратно установщик не должен, и это не оплошность.
;
; При обновлении установщик запущен обновляльщиком, то есть от SYSTEM в нулевом
; сеансе. Поднятое отсюда приложение оказалось бы там же: работающим, но
; невидимым — оператор решил бы, что софтфон пропал. Приложение поднимает
; обновляльщик с `--relaunch` в конце установки, маркером активного сеанса
; (см. `UserSession` и раздел [Run]).
RestartApplications=no

UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
; Каталог обмена между приложением и обновляльщиком.
;
; Оператор пишет сюда скачанный установщик и отметку о согласии, SYSTEM отсюда
; читает. Права на запись для пользователей выданы намеренно, и это не дыра:
; обновляльщик не верит содержимому этого каталога — он сам проверяет подпись
; Ed25519 ключом из `Program Files`, куда оператор писать не может. Худшее, чего
; добьётся подделавший отметку, — установка нашего же подписанного выпуска.
Name: "{commonappdata}\{#AppName}"; Permissions: users-modify
Name: "{commonappdata}\{#AppName}\updates"; Permissions: users-modify

; Сюда обновляльщик переносит проверенный установщик перед запуском. Права
; обычные: писать может только администратор и SYSTEM — потому отсюда и
; запускается.
Name: "{app}\updates"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
; Ярлык на рабочем столе — как у прочего ПО в инструкции заказчика. Тип `LNK`
; удалён из назначенных типов файлов SRP, так что сам ярлык политикой не
; проверяется; проверяется цель, а она в разрешённом каталоге.
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Run]
; --- Правило межсетевого экрана -----------------------------------------------
;
; Без него первый же входящий вызов показал бы системный запрос, а оператор,
; которому нечего на него ответить, нажал бы «Отмена» — и SIP перестал бы
; принимать. Профиль `any`: инструкция заказчика переводит сеть в «Частные», но
; полагаться на это нельзя.
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=any"; \
    Flags: runhidden; StatusMsg: "Правило межсетевого экрана..."

; --- Задача планировщика ------------------------------------------------------
;
; Здесь и решается задача, ради которой всё это затевалось.
;
; Оператор не может ни писать в `Program Files`, ни повышать права, а обновление
; обязано проходить без него. Задача заводится один раз — сейчас, с правами
; администратора, — и дальше работает от SYSTEM. К процессам SYSTEM политика SRP
; не применяется, а сам обновляльщик к тому же лежит в разрешённом каталоге:
; работает при любом из двух условий.
;
; `/RU SYSTEM` — без пароля и без учётной записи с ним.
; `/SC MINUTE /MO 10` — опрос, а не запуск по требованию: запуск по требованию
; потребовал бы выдать оператору право на выполнение задачи, то есть править её
; список управления доступом. Обход дешевле: обновляльщик просыпается, видит, что
; отметки нет, и сразу выходит.
;
; Аргументов задача не принимает **намеренно**: аргументы, приходящие от того, кто
; её запускает, — это запуск произвольного кода с правами SYSTEM. Что делать,
; обновляльщик решает сам, прочитав подписанный манифест.
Filename: "{sys}\schtasks.exe"; \
    Parameters: "/Create /F /TN ""{#AppName}\Update"" /TR ""\""{app}\{#UpdaterExe}\"""" /SC MINUTE /MO 10 /RU SYSTEM /RL HIGHEST"; \
    Flags: runhidden; StatusMsg: "Задача обновления..."

; Настройки задачи — явно, а не по умолчанию `schtasks`.
;
; По умолчанию `schtasks` запрещает старт от батареи: на ноутбуке такты
; обновления просто пропускались, и выпуск не доезжал, пока машина не
; постоит в розетке. Срок в полчаса снимает зависший экземпляр сам: до 0.1.48
; такой висел сутками и отклонял все следующие такты — обновления переставали
; проверяться. `IgnoreNew` — второй экземпляр поверх идущего не нужен.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Set-ScheduledTask -TaskPath '\{#AppName}\' -TaskName Update -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 30)) | Out-Null"""; \
    Flags: runhidden; StatusMsg: "Настройки задачи обновления..."

; Право разбудить задачу — оператору.
;
; Такта в десять минут хватает, чтобы обновление не потерялось, но не хватает,
; чтобы оператор не заметил: согласившись, он ждал бы до десяти минут непонятно
; чего. Поэтому приложение будит задачу сразу, а для этого учётной записи нужно
; право на выполнение — по умолчанию его нет.
;
; Права ровно два: прочитать и выполнить. Изменить задачу оператор не может —
; иначе он подменил бы то, что запускается от SYSTEM, то есть получил бы полные
; права на машину в обход политики.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$s = New-Object -ComObject Schedule.Service; $s.Connect(); $t = $s.GetFolder('\{#AppName}').GetTask('Update'); $t.SetSecurityDescriptor('D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;AU)', 0)"""; \
    Flags: runhidden; StatusMsg: "Права на задачу обновления..."

; Запуск после установки — только при обычной установке. При обновлении
; (`/SILENT`) приложение поднимает обновляльщик сам, в сеансе оператора: то, что
; запущено отсюда под SYSTEM, оказалось бы в нулевом сеансе и никому не видно.
;
; `runasoriginaluser` обязателен и стоил проверки на живой машине.
;
; Без него Inno запускает программу с правами установщика, то есть от
; администратора, — и софтфон уходит в его профиль. Там нет ни настроек, ни
; заводского файла: приложение встречает мастером первого запуска и говорит, что
; заводская настройка не положена, хотя она лежит на месте. Оператор при этом
; входит потом в собственный профиль, где ничего не настроено, а работа админа
; в мастере пропадает целиком.
Filename: "{app}\{#AppExe}"; Description: "Запустить {#AppName}"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser

; Подъём приложения после обновления — отсюда, а не из того обновляльщика,
; который установку начал.
;
; Тот обновляльщик до конца установки не доживает. Он запущен из `{app}` и
; держит файлы, которые установщик заменяет; диспетчер перезапуска
; (`CloseApplications=yes`) их освобождает, закрывая процесс, — то есть убивает
; того, кто ждал установщика, чтобы потом поднять софтфон. На живой машине
; 11 сентября 2026 журнал обновления так и обрывался на «взят выпуск»: выпуск
; вставал, а софтфон не поднимался до следующего входа в систему.
;
; Здесь запускается уже новый обновляльщик, после того как всё заменено, и
; ровно с одним поручением. От SYSTEM, как и установщик: только отсюда можно
; поднять программу в чужом сеансе, и только в сеансе оператора её увидят.
Filename: "{app}\{#UpdaterExe}"; Parameters: "--relaunch"; \
    Flags: nowait runhidden; Check: WizardSilent

[UninstallRun]
; Софтфон закрывается первым, и это не вежливость.
;
; Он живёт в трее и переживает закрытие окон, а диспетчер перезапуска Windows
; его не уносит. Работающий процесс держит свои файлы, удаление их не трогает и
; молча оставляет — на живой машине после «Удалить» в каталоге оставалось
; почти три сотни файлов.
;
; Двумя заходами: сперва просьба закрыться, потом принуждение. Первая даёт
; снять регистрацию на АТС, вторая нужна на случай, если окна нет вовсе.
; Задача снимается первой: иначе обновляльщик проснётся по такту прямо посреди
; удаления и снова займёт файлы, которые мы только что освободили.
Filename: "{sys}\schtasks.exe"; \
    Parameters: "/Delete /F /TN ""{#AppName}\Update"""; \
    Flags: runhidden; RunOnceId: "RemoveUpdateTask"

; Одноразовая задача, которой обновляльщик ставит выпуск (с 0.1.48).
Filename: "{sys}\schtasks.exe"; \
    Parameters: "/Delete /F /TN ""{#AppName}\Install"""; \
    Flags: runhidden; RunOnceId: "RemoveInstallTask"

Filename: "{sys}\taskkill.exe"; Parameters: "/IM ""{#UpdaterExe}"" /F"; \
    Flags: runhidden skipifdoesntexist; RunOnceId: "ForceUpdaterToClose"

; Сперва просьба закрыться — она даёт снять регистрацию на АТС.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM ""{#AppExe}"""; \
    Flags: runhidden skipifdoesntexist; RunOnceId: "AskAppToClose"

; Пауза средствами PowerShell, а не `timeout`: тот под скрытым запуском без
; своей консоли отказывается работать.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -Command ""Start-Sleep -Seconds 4"""; \
    Flags: runhidden; RunOnceId: "WaitForAppToClose"

; Потом принуждение — на случай, если окна нет вовсе или оно не ответило.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM ""{#AppExe}"" /F"; \
    Flags: runhidden skipifdoesntexist; RunOnceId: "ForceAppToClose"

Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; \
    Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[UninstallDelete]
; Перенесённые установщики — наши файлы, и оставлять их незачем. Настройки,
; история и журнал живут в профиле пользователя и удалением программы не
; трогаются: переустановка не должна стирать рабочее место.
Type: filesandordirs; Name: "{app}\updates"

[Code]
{
  Всё, что держит файлы программы, снимается до копирования, а не
  диспетчером перезапуска посреди него.

  Диспетчер перезапуска (`CloseApplications`) умеет закрыть окно, но не
  процесс без окна: обновляльщик и зависший прежний установщик он оставлял, и
  установщик упирался в занятый файл. В тихом режиме это окно «файл занят»
  в нулевом сеансе, которое некому закрыть, — так 11 сентября 2026 висела
  установка 0.1.47.

  Снимаются:
    - софтфон — принудительно: регистрацию он восстановит, поднявшись заново;
    - обновляльщик — до 0.1.48 он запускал установщик сам и ждал его, держа
      свои файлы и общие библиотеки .NET;
    - прежние установщики EliteSIP, кроме этого самого, — зависшие в нулевом
      сеансе от предыдущих попыток.
}
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#UpdaterExe}', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "Get-Process | Where-Object { ' +
    '$_.ProcessName -like ''{#AppName}-*'' -and $_.ProcessName -notlike ''*{#AppVersion}*'' } | ' +
    'Stop-Process -Force -ErrorAction SilentlyContinue"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { Процесс уходит не мгновенно: дескрипторы файлов закрываются после него. }
  Sleep(1500);
  Result := '';
end;

{
  Удаление: по желанию — и настройки.

  По умолчанию удаление программы настроек не трогает: переустановка не
  должна стирать рабочее место. Но машину, которую отдают другому человеку
  или выводят из работы, надо уметь очистить целиком — отсюда галочка.

  Настройки, история и журнал лежат в профиле того, кто работал за машиной
  (`%LOCALAPPDATA%\EliteSIP`), а удаляет обычно администратор — у него профиль
  свой. Поэтому чистятся профили всех пользователей машины, плюс общий каталог
  обмена с обновляльщиком.

  Тихое удаление (`/SILENT`, развёртывание через GPO) ничего не спрашивает и
  настройки оставляет: стирать чужие данные без вопроса нельзя.
}
var
  RemoveSettings: Boolean;

function InitializeUninstall(): Boolean;
var
  Form: TSetupForm;
  Note: TNewStaticText;
  Check: TNewCheckBox;
  Confirm, Cancel: TNewButton;
begin
  Result := True;
  RemoveSettings := False;

  if UninstallSilent then
    Exit;

  Form := CreateCustomForm(ScaleX(400), ScaleY(160), False, False);
  try
    Form.Caption := 'Удаление {#AppName}';
    Form.Position := poScreenCenter;

    Note := TNewStaticText.Create(Form);
    Note.Parent := Form;
    Note.Left := ScaleX(16);
    Note.Top := ScaleY(16);
    Note.Width := Form.ClientWidth - ScaleX(32);
    Note.AutoSize := False;
    Note.Height := ScaleY(48);
    Note.WordWrap := True;
    Note.Caption := 'Программа будет удалена с компьютера. Настройки рабочего места, ' +
      'история звонков и журналы по умолчанию сохраняются — на случай переустановки.';

    Check := TNewCheckBox.Create(Form);
    Check.Parent := Form;
    Check.Left := ScaleX(16);
    Check.Top := ScaleY(72);
    Check.Width := Form.ClientWidth - ScaleX(32);
    Check.Height := ScaleY(20);
    Check.Caption := 'Удалить также все настройки, историю звонков и журналы';
    Check.Checked := False;

    Cancel := TNewButton.Create(Form);
    Cancel.Parent := Form;
    Cancel.Width := ScaleX(90);
    Cancel.Height := ScaleY(26);
    Cancel.Left := Form.ClientWidth - ScaleX(16) - Cancel.Width;
    Cancel.Top := Form.ClientHeight - ScaleY(16) - Cancel.Height;
    Cancel.Caption := 'Отмена';
    Cancel.ModalResult := mrCancel;
    Cancel.Cancel := True;

    Confirm := TNewButton.Create(Form);
    Confirm.Parent := Form;
    Confirm.Width := ScaleX(90);
    Confirm.Height := ScaleY(26);
    Confirm.Left := Cancel.Left - ScaleX(8) - Confirm.Width;
    Confirm.Top := Cancel.Top;
    Confirm.Caption := 'Удалить';
    Confirm.ModalResult := mrOk;
    Confirm.Default := True;

    Result := Form.ShowModal() = mrOk;
    RemoveSettings := Check.Checked;
  finally
    Form.Free();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if (CurUninstallStep <> usPostUninstall) or not RemoveSettings then
    Exit;

  { Общий каталог обмена с обновляльщиком. }
  DelTree(ExpandConstant('{commonappdata}\{#AppName}'), True, True, True);

  { Профили всех пользователей: настройки, история, журнал. }
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath (Join-Path $env:SystemDrive ''Users'') ' +
    '-Directory -Force -ErrorAction SilentlyContinue | ForEach-Object { ' +
    'Remove-Item -LiteralPath (Join-Path $_.FullName ''AppData\Local\{#AppName}'') ' +
    '-Recurse -Force -ErrorAction SilentlyContinue }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
