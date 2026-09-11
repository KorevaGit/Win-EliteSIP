<#
.SYNOPSIS
    Выпуск EliteSIP: одна команда вместо десяти шагов.

.DESCRIPTION
    Причина ровно та же, что в оригинале (Tools/release.sh): ручной выпуск из
    десяти шагов рано или поздно выпускается без одного из них, и обычно это
    оказывается версия или подпись.

    Шаги идут в этом порядке и не переставляются:

      1. дерево чистое, версия задана и растёт;
      2. версия проставляется в Directory.Build.props — одно место на всё решение;
      3. сборка и все проверки; провалилась хоть одна — выпуска нет;
      4. публикация приложения;
      5. подпись Authenticode (без неё SmartScreen блокирует загрузку);
      6. установщик Inno Setup и его подпись;
      7. манифест линии обновлений и подпись Ed25519;
      8. проверка манифеста кодом клиента;
      9. что и куда выкладывать — печатается, а не делается.

    Установщика (шаг 6) пока нет: он вторая половина W12 вместе с подписью
    Authenticode. Скрипт написан целиком, но на этих шагах честно
    останавливается, а не собирает «почти выпуск».

    Шаг 9 намеренно не автоматизирован: выкладка на канал — это то место, где
    ошибка видна всем тридцати машинам сразу, и делает её человек глазами.

.PARAMETER Version
    Версия выпуска: три числа через точку.

.PARAMETER SigningKey
    Файл закрытого ключа линии (Ed25519, base64). Тот же, которым подписаны
    предустановки. В репозитории его нет и быть не должно.

.PARAMETER CertificateThumbprint
    Отпечаток сертификата Authenticode в хранилище машины.

.PARAMETER BaseUrl
    Корень канала раздачи: из него собирается адрес установщика в манифесте.

.PARAMETER SkipSigning
    Собрать без подписи Authenticode. Только для проверки самого скрипта:
    выпуск без подписи выкладывать нельзя — см. разбор в конце.

.PARAMETER SkipInstaller
    Не собирать установщик. То же назначение, что у -SkipSigning.

.EXAMPLE
    .\tools\release.ps1 -Version 1.4.2 -SigningKey C:\keys\releases.key `
        -CertificateThumbprint A1B2C3... -BaseUrl https://get.elitesip.vip
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $SigningKey,

    [string] $CertificateThumbprint,

    [string] $BaseUrl = 'https://get.elitesip.vip',

    [switch] $SkipSigning,

    [switch] $SkipInstaller,

    # Не трогать рабочее дерево: только проверить, что выпуск собрался бы.
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$propsPath = Join-Path $root 'Directory.Build.props'

function Step {
    param([string] $Title)

    Write-Host ''
    Write-Host "== $Title" -ForegroundColor Cyan
}

function Fail {
    param([string] $Reason)

    Write-Host ''
    Write-Host "выпуск остановлен: $Reason" -ForegroundColor Red
    exit 1
}

# --- 1. Дерево и версия -----------------------------------------------------

Step 'проверка дерева и версии'

Push-Location $root
try {
    $dirty = (git status --porcelain) | Where-Object { $_ }
    if ($dirty -and -not $DryRun) {
        # Выпуск из грязного дерева невоспроизводим: собранное невозможно
        # сопоставить с коммитом, а искать разницу придётся тогда, когда
        # что-нибудь сломается на боевой машине.
        Fail 'в рабочем дереве есть несохранённые правки'
    }

    # Через XPath, а не через точки: PropertyGroup в файле несколько, и обход
    # точками отдаёт массив, у которого свойства Version нет вовсе.
    $current = ([xml](Get-Content $propsPath)).SelectSingleNode('//Version').InnerText

    if ([version]$Version -le [version]$current) {
        # Версия обязана расти: линия обновлений предлагает только вперёд, и
        # выпуск с прежним номером до машин просто не доедет.
        Fail "версия $Version не больше текущей $current"
    }

    Write-Host "  версия: $current -> $Version"

    if (-not $DryRun) {
        (Get-Content $propsPath -Raw) `
            -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>" |
            Set-Content $propsPath -Encoding UTF8 -NoNewline
    }

    # --- 2. Сборка и проверки ------------------------------------------------

    Step 'сборка и проверки'

    dotnet build --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { Fail 'сборка не прошла' }

    dotnet test --configuration Release --nologo --no-build
    if ($LASTEXITCODE -ne 0) { Fail 'проверки не прошли' }

    # --- 3. Публикация -------------------------------------------------------

    Step 'публикация'

    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    # Самодостаточная публикация: рантайм едет внутри выпуска.
    #
    # Решено 10 сентября 2026. Машины заказчика заперты политикой SRP и готовятся
    # по инструкции, которая уже согласована; зависимая от рантайма публикация
    # потребовала бы дописать в неё установку .NET Desktop Runtime отдельным
    # пунктом — и машина, где этот пункт пропустили, встречала бы оператора
    # молча не запускающейся программой. Цена известна и принята: выпуск вместо
    # пяти мегабайт весит под сотню, и столько же качает каждое обновление.
    dotnet publish (Join-Path $root 'src\EliteSIP.App\EliteSIP.App.csproj') `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $publish `
        --nologo
    if ($LASTEXITCODE -ne 0) { Fail 'публикация не прошла' }

    # Обновляльщик — в тот же каталог: установщик кладёт их рядом, и задача
    # планировщика зовёт его по пути внутри `Program Files`.
    dotnet publish (Join-Path $root 'tools\Updater\EliteSIP.Updater.csproj') `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $publish `
        --nologo
    if ($LASTEXITCODE -ne 0) { Fail 'обновляльщик не опубликовался' }

    # Заводская настройка в публикацию не кладётся скриптом и не должна:
    # в ней пара авторизации канала, и место ей — рядом с ключом подписи, а не
    # в дереве сборки. Но выпуск без неё бессмыслен.
    #
    # Здесь отказ, а не предупреждение. Прежде было предупреждение, и 10 сентября
    # 2026 оно обошлось половиной дня разбора: собранный без файла выпуск
    # ставится и работает, а мёртвую линию панели видно только на чистой машине —
    # мастер первого запуска не даёт ввести ключ. На машине, где файл когда-то
    # положили руками, всё выглядит исправным, потому что сброс его не стирает.
    # Жёлтую строку в выводе на такое не поставишь.
    if (-not (Test-Path (Join-Path $publish 'provisioning.json'))) {
        Fail ("в публикации нет provisioning.json. Без него линия панели, " +
              "активация по ключу и обновления выключены, и заметить это можно " +
              "только на чистой машине. Положите файл в каталог публикации: $publish")
    }

    # --- 4. Подпись Authenticode ---------------------------------------------

    Step 'подпись Authenticode'

    if ($SkipSigning) {
        Write-Host '  пропущено по -SkipSigning' -ForegroundColor Yellow
    }
    elseif (-not $CertificateThumbprint) {
        Fail 'не задан -CertificateThumbprint (или укажите -SkipSigning для проверки скрипта)'
    }
    else {
        $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
        if (-not $signtool) { Fail 'signtool.exe не найден: нужен Windows SDK' }

        & $signtool.Source sign /sha1 $CertificateThumbprint /fd SHA256 `
            /tr http://timestamp.digicert.com /td SHA256 `
            (Join-Path $publish 'EliteSIP.App.exe')
        if ($LASTEXITCODE -ne 0) { Fail 'подпись приложения не прошла' }
    }

    # --- 5. Установщик -------------------------------------------------------

    Step 'установщик'

    $installer = Join-Path $artifacts "EliteSIP-$Version.exe"

    if ($SkipInstaller) {
        Write-Host '  пропущено по -SkipInstaller' -ForegroundColor Yellow
    }
    else {
        $innoScript = Join-Path $PSScriptRoot 'installer.iss'
        if (-not (Test-Path $innoScript)) {
            # Установщик — вторая половина W12, и её ещё нет. Скрипт про это
            # говорит прямо, а не собирает молча «почти выпуск»: непонятно
            # собранный выпуск хуже несобранного.
            Fail 'нет tools\installer.iss — установщик из W12 ещё не написан (проверить скрипт можно с -SkipInstaller)'
        }

        $iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
        if (-not $iscc) { Fail 'iscc.exe не найден: нужен Inno Setup' }

        & $iscc.Source `
            "/DAppVersion=$Version" `
            "/DPublishDir=$publish" `
            "/O$artifacts" `
            (Join-Path $PSScriptRoot 'installer.iss')
        if ($LASTEXITCODE -ne 0) { Fail 'установщик не собрался' }

        if (-not $SkipSigning) {
            & (Get-Command signtool.exe).Source sign /sha1 $CertificateThumbprint /fd SHA256 `
                /tr http://timestamp.digicert.com /td SHA256 $installer
            if ($LASTEXITCODE -ne 0) { Fail 'подпись установщика не прошла' }
        }
    }

    # --- 6. Манифест линии обновлений ----------------------------------------

    Step 'манифест обновления'

    if (-not (Test-Path $installer)) {
        Write-Host '  установщика нет — манифест не собирается' -ForegroundColor Yellow
        Write-Host ''
        Write-Host 'проверочный прогон закончен: до манифеста дело не дошло.' -ForegroundColor Yellow
        exit 0
    }

    $kit = Join-Path $root 'tools\ReleaseKit\bin\Release\net10.0\releasekit.exe'
    if (-not (Test-Path $kit)) { Fail "releasekit не собран: $kit" }

    if (-not $SigningKey) { Fail 'не задан -SigningKey: манифест нечем подписать' }
    if (-not (Test-Path $SigningKey)) { Fail "ключ подписи не найден: $SigningKey" }

    $digest = & $kit hash $installer
    $size = (Get-Item $installer).Length

    $manifest = [ordered]@{
        format       = 1
        version      = $Version
        url          = "$($BaseUrl.TrimEnd('/'))/releases/EliteSIP-$Version.exe"
        sha256       = $digest
        size         = $size
        published_at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        notes        = ''
    }

    $manifestPath = Join-Path $artifacts 'manifest.json'
    $envelopePath = Join-Path $artifacts 'current.json'

    # Без BOM, и это не мелочь: `Set-Content -Encoding UTF8` в Windows
    # PowerShell пишет UTF-8 с меткой порядка байтов, метка попадает внутрь
    # подписанного содержимого, и разбор у клиента спотыкается о неё. Поймано
    # именно проверкой на шаге 8 — тем и ценна проверка кодом клиента.
    [System.IO.File]::WriteAllText(
        $manifestPath,
        ($manifest | ConvertTo-Json),
        (New-Object System.Text.UTF8Encoding $false))
    & $kit sign $manifestPath $SigningKey $envelopePath
    if ($LASTEXITCODE -ne 0) { Fail 'манифест не подписался' }

    # --- 7. Проверка кодом клиента -------------------------------------------

    Step 'проверка манифеста кодом клиента'

    $publicKey = [System.IO.Path]::ChangeExtension($SigningKey, '.pub')
    if (-not (Test-Path $publicKey)) { Fail "рядом с ключом нет открытой половины: $publicKey" }

    & $kit verify $envelopePath $publicKey
    if ($LASTEXITCODE -ne 0) { Fail 'манифест не прошёл проверку клиентом' }

    # --- 8. Что выкладывать --------------------------------------------------

    Step 'выпуск собран'

    Write-Host ''
    Write-Host 'Выложить на канал:' -ForegroundColor Green
    Write-Host "  $installer"
    Write-Host "    -> releases/EliteSIP-$Version.exe"
    Write-Host "  $envelopePath"
    Write-Host '    -> releases/current.json'
    Write-Host ''
    Write-Host 'Порядок значим: сперва установщик, потом манифест. Наоборот —'
    Write-Host 'это машины, которые пошли за файлом, которого ещё нет.'
    Write-Host ''
    Write-Host 'После выкладки: поставить выпуск на одну машину и убедиться, что'
    Write-Host 'она обновилась сама. Проверка «манифест разобрался» этого не'
    Write-Host 'заменяет — установщик запускает Windows, а не мы.'
}
finally {
    Pop-Location
}
