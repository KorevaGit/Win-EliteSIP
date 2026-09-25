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
    Корень канала выпусков: из него собирается адрес установщика в манифесте.
    С 0.1.56 это собственный сервер, а не R2 (см. docs/RELEASES.md).

.PARAMETER LegacyBaseUrl
    Только для переходного выпуска: собрать второй манифест с адресом на
    старом канале (artifacts\legacy\current.json). Им обновляются машины,
    которые ещё читают старый канал. Установщик тот же, байт в байт.

.PARAMETER Provisioning
    Заводская настройка, которая ляжет в выпуск. Живёт вне репозитория: в ней
    пара Basic канала. По умолчанию %APPDATA%\EliteSIP-release\provisioning.json.

.PARAMETER SkipSigning
    Собрать без подписи Authenticode. Только для проверки самого скрипта:
    выпуск без подписи выкладывать нельзя — см. разбор в конце.

.PARAMETER SkipInstaller
    Не собирать установщик. То же назначение, что у -SkipSigning.

.EXAMPLE
    .\tools\release.ps1 -Version 1.4.2 -SigningKey C:\keys\releases.key `
        -CertificateThumbprint A1B2C3...

.EXAMPLE
    Переходный 0.1.56: манифест нового канала и манифест для старого.

    .\tools\release.ps1 -Version 0.1.56 -SigningKey C:\keys\releases.key `
        -SkipSigning -LegacyBaseUrl https://get.elitesip.vip
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $SigningKey,

    [string] $CertificateThumbprint,

    [string] $BaseUrl = 'https://update.elitesip.vip:8081',

    [string] $LegacyBaseUrl,

    [string] $Provisioning = (Join-Path $env:APPDATA 'EliteSIP-release\provisioning.json'),

    # Заметки выпуска: ложатся в поле notes манифеста.
    [string] $Notes = '',

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
    #
    # Прежде скрипт стирал каталог публикации, а потом требовал, чтобы файл в
    # нём лежал, — одним прогоном это не проходило никогда. Теперь файл
    # берётся из места вне репозитория и кладётся сюда сам.
    if (-not (Test-Path $Provisioning)) {
        Fail ("нет заводской настройки $Provisioning. Без неё линия панели, " +
              "активация по ключу и обновления выключены, и заметить это можно " +
              "только на чистой машине. Укажите файл параметром -Provisioning.")
    }

    # Канал выпусков в настройке обязан совпасть с каналом манифеста: выпуск,
    # собранный под новый сервер с настройкой на старый, после установки
    # ходил бы за обновлениями не туда, и заметили бы это через выпуск.
    $factory = Get-Content $Provisioning -Raw -Encoding UTF8 | ConvertFrom-Json

    # С 0.1.56 весь канал — один сервер, и разводить выпуски с панелью незачем:
    # R2 заморожен, Spark пишет только на новый сервер. Разбор поля в клиенте
    # остался на будущее, а в заводской настройке его быть не должно — иначе
    # панель могла бы молча остаться на замороженном канале.
    if ($factory.updates.PSObject.Properties['releasesURL']) {
        Fail 'в заводской настройке есть updates.releasesURL — уберите его, канал задаётся одним baseURL'
    }
    $releasesRoot = $factory.updates.baseURL
    if (-not $releasesRoot -or ($releasesRoot.TrimEnd('/') -ne $BaseUrl.TrimEnd('/'))) {
        Fail ("канал выпусков в заводской настройке ($releasesRoot) не совпадает с -BaseUrl ($BaseUrl)")
    }
    if (-not $factory.releasesPublicKey -or -not $factory.presetsPublicKey) {
        Fail 'в заводской настройке нет одного из ключей: releasesPublicKey или presetsPublicKey'
    }

    Copy-Item $Provisioning (Join-Path $publish 'provisioning.json') -Force
    Write-Host "  заводская настройка: выпуски $releasesRoot, панель $($factory.updates.baseURL)"

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

        # Inno Setup на машине сборки стоит в профиль, а не в Program Files, и
        # в PATH его нет.
        # Через переменную: под StrictMode `.Source` у пустого результата — ошибка.
        $found = Get-Command iscc.exe -ErrorAction SilentlyContinue
        $iscc = if ($found) { $found.Source } else { $null }
        if (-not $iscc) {
            $iscc = @(
                (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
                (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
            ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        }
        if (-not $iscc) { Fail 'iscc.exe не найден: нужен Inno Setup' }

        & $iscc `
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

    $publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $publicKey = [System.IO.Path]::ChangeExtension($SigningKey, '.pub')
    if (-not (Test-Path $publicKey)) { Fail "рядом с ключом нет открытой половины: $publicKey" }

    # До подписи: открытая половина обязана быть тем ключом, который клиент
    # получит в заводской настройке. Иначе манифест подписан «верно», а
    # рабочие места его отвергнут — и молча, потому что так и должны.
    $factoryKey = (Get-Content $Provisioning -Raw -Encoding UTF8 | ConvertFrom-Json).releasesPublicKey
    if ((Get-Content $publicKey -Raw).Trim() -ne ([string]$factoryKey).Trim()) {
        Fail "открытый ключ $publicKey не совпадает с releasesPublicKey заводской настройки"
    }

    # Манифест на один канал. `url` указывает на тот же хост, с которого
    # манифест будет отдан: клиент с 0.1.56 качает установщик только оттуда
    # (ReleaseManifest.IsServedFrom), и Basic-пару на чужой хост не отправит.
    function Write-Manifest {
        param([string] $Root, [string] $Directory)

        New-Item -ItemType Directory -Force $Directory | Out-Null

        $manifest = [ordered]@{
            format       = 1
            version      = $Version
            url          = "$($Root.TrimEnd('/'))/releases/EliteSIP-$Version.exe"
            sha256       = $digest
            size         = $size
            published_at = $publishedAt
            notes        = $Notes
        }

        $manifestPath = Join-Path $Directory 'manifest.json'
        $envelopePath = Join-Path $Directory 'current.json'

        # Без BOM, и это не мелочь: `Set-Content -Encoding UTF8` в Windows
        # PowerShell пишет UTF-8 с меткой порядка байтов, метка попадает внутрь
        # подписанного содержимого, и разбор у клиента спотыкается о неё. Поймано
        # именно проверкой кодом клиента — тем она и ценна.
        [System.IO.File]::WriteAllText(
            $manifestPath,
            ($manifest | ConvertTo-Json),
            (New-Object System.Text.UTF8Encoding $false))
        & $kit sign $manifestPath $SigningKey $envelopePath | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail "манифест для $Root не подписался" }

        # Проверка кодом клиента: тем же ReleaseManifest.Verified, которым его
        # прочитает рабочее место.
        & $kit verify $envelopePath $publicKey | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail "манифест для $Root не прошёл проверку клиентом" }

        return $envelopePath
    }

    $envelopePath = Write-Manifest $BaseUrl $artifacts

    # --- 7. Манифест для старого канала (только переходный выпуск) ----------

    $legacyEnvelope = $null
    if ($LegacyBaseUrl) {
        Step 'манифест для старого канала'
        $legacyEnvelope = Write-Manifest $LegacyBaseUrl (Join-Path $artifacts 'legacy')
    }

    # --- 8. Что выкладывать --------------------------------------------------

    Step 'выпуск собран'

    Write-Host ''
    Write-Host "  установщик: $installer"
    Write-Host "  sha256:     $digest"
    Write-Host "  размер:     $size"
    Write-Host "  манифест:   $envelopePath ($BaseUrl)"
    if ($legacyEnvelope) {
        Write-Host "  переходный: $legacyEnvelope ($LegacyBaseUrl)"
    }
    Write-Host ''
    Write-Host 'Выложить (docs/RELEASES.md):' -ForegroundColor Green
    Write-Host "  .\tools\publish.ps1 -Channel update -Version $Version"
    if ($legacyEnvelope) {
        Write-Host '  и только после проверки нового канала:'
        Write-Host "  .\tools\publish.ps1 -Channel legacy -Version $Version"
    }
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
