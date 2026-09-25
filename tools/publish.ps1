<#
.SYNOPSIS
    Выкладка собранного выпуска на канал обновлений и проверка того, что
    канал отдаёт.

.DESCRIPTION
    Отдельно от release.ps1 по той же причине, что в macOS-версии
    (Tools/publish.sh): выпуск делается один раз и требует ключа подписи,
    выкладка — сколько угодно раз и требует доступа к каналу. Слитые вместе,
    они означали бы пересборку ради повторной выкладки, то есть другой бинарь
    под тем же номером.

    Каналов два:

      update  — собственный сервер https://update.elitesip.vip:8081, закрытый
                объектный API (PUT /internal/objects/<ключ>, Bearer). С 0.1.56
                выпуски публикуются только сюда.
      legacy  — старый R2 за Worker'ом https://get.elitesip.vip, S3 API.
                Нужен ровно один раз: переходный 0.1.56 для машин, которые ещё
                читают старый канал (docs/RELEASES.md).

    Что делает, по порядку:

      1. сверяет манифест с установщиком до всякой сети: версия, адрес на
         этом же канале, sha256 и размер. Манифест с url на другой хост
         отвергается — клиент такой выпуск качать откажется;
      2. кладёт установщик, потом манифест. Наоборот — это машины, которые
         пошли за файлом, которого ещё нет;
      3. забирает с канала то, что он теперь отдаёт, с парой Basic: манифест
         байт в байт, установщик по sha256 и размеру, и отказ 401 без пары.

    Доступы — в %APPDATA%\EliteSIP-release\publish.local.json, вне Git.
    Токен и ключи R2 в командную строку curl не попадают: они уходят в
    файл настроек curl, который удаляется сразу после запроса.

.PARAMETER Channel
    update или legacy.

.PARAMETER Version
    Какую версию выкладывать. Файлы берутся из artifacts: EliteSIP-<версия>.exe
    и current.json (для legacy — legacy\current.json).

.PARAMETER VerifyOnly
    Ничего не класть — только проверить то, что канал отдаёт сейчас.

.PARAMETER DryRun
    Сверить файлы и показать, что было бы выложено.

.EXAMPLE
    .\tools\publish.ps1 -Channel update -Version 0.1.56
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('update', 'legacy')]
    [string] $Channel,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $Config = (Join-Path $env:APPDATA 'EliteSIP-release\publish.local.json'),

    [switch] $VerifyOnly,

    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'

function Step { param([string] $Title) Write-Host ''; Write-Host "== $Title" -ForegroundColor Cyan }
function Ok { param([string] $Text) Write-Host "  ок: $Text" -ForegroundColor Green }
function Fail { param([string] $Reason) Write-Host ''; Write-Host "выкладка остановлена: $Reason" -ForegroundColor Red; exit 1 }

# --- Доступы ------------------------------------------------------------------

if (-not (Test-Path $Config)) {
    Fail "нет $Config — заведите его по образцу из docs/RELEASES.md"
}

$settings = (Get-Content $Config -Raw -Encoding UTF8 | ConvertFrom-Json).$Channel
if (-not $settings) { Fail "в $Config нет раздела $Channel" }

$baseUrl = ([string]$settings.baseURL).TrimEnd('/')
$basicAuth = [string]$settings.basicAuth
if (-not $baseUrl) { Fail "в $Config нет $Channel.baseURL" }
if (-not $basicAuth) { Fail "в $Config нет $Channel.basicAuth" }

# curl из System32 есть на любой Windows 10/11; для legacy нужен тот, что
# умеет подписывать S3 (--aws-sigv4, curl 7.75 и новее).
$curl = (Get-Command curl.exe -ErrorAction SilentlyContinue).Source
if (-not $curl) { Fail 'curl.exe не найден' }

<#
    Запрос через curl с секретами в файле настроек (-K), а не в аргументах:
    аргументы процесса видны любому, кто смотрит список процессов.
    Возвращает код ответа строкой.
#>
function Invoke-Curl {
    param(
        [string[]] $Arguments,
        [string[]] $Secrets = @()
    )

    $configFile = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllLines($configFile, $Secrets, (New-Object System.Text.UTF8Encoding $false))
        $code = & $curl -sS -K $configFile -w '%{http_code}' @Arguments
        if ($LASTEXITCODE -ne 0) { Fail "curl завершился с кодом $LASTEXITCODE" }
        return ([string]$code).Trim()
    }
    finally {
        Remove-Item $configFile -Force -ErrorAction SilentlyContinue
    }
}

function Quote { param([string] $Value) '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"' }

$basicSecret = @("user = $(Quote $basicAuth)")

# --- 1. Что выкладываем -------------------------------------------------------

Step "канал $Channel → $baseUrl"

$installer = Join-Path $artifacts "EliteSIP-$Version.exe"
$envelope = if ($Channel -eq 'legacy') { Join-Path $artifacts 'legacy\current.json' } else { Join-Path $artifacts 'current.json' }

if (-not (Test-Path $installer)) { Fail "нет установщика $installer" }
if (-not (Test-Path $envelope)) { Fail "нет манифеста $envelope" }

$payload = [System.Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String((Get-Content $envelope -Raw | ConvertFrom-Json).payload)) | ConvertFrom-Json

$digest = (Get-FileHash $installer -Algorithm SHA256).Hash.ToUpperInvariant()
$size = (Get-Item $installer).Length
$expectedUrl = "$baseUrl/releases/EliteSIP-$Version.exe"

if ($payload.version -ne $Version) { Fail "в манифесте версия $($payload.version), а выкладывается $Version" }
if ($payload.url -ne $expectedUrl) {
    Fail ("в манифесте url $($payload.url), а канал $baseUrl. Клиент качает установщик только " +
          "с хоста манифеста; для этого канала нужен манифест с url $expectedUrl")
}
if ($payload.sha256.ToUpperInvariant() -ne $digest) { Fail "sha256 в манифесте не совпадает с установщиком" }
if ([long]$payload.size -ne $size) { Fail "размер в манифесте $($payload.size), у установщика $size" }

Ok "манифест $Version сходится с установщиком: sha256 $digest, $size байт"

# --- 2. Выкладка --------------------------------------------------------------

function Publish-Object {
    param([string] $Source, [string] $Key, [string] $ContentType)

    if ($DryRun) {
        Write-Host "  [сухой прогон] положил бы $(Split-Path -Leaf $Source) → $Key"
        return
    }

    switch ($Channel) {
        'update' {
            $token = [string]$settings.adminToken
            if (-not $token) { Fail "в $Config нет update.adminToken" }

            $code = Invoke-Curl -Arguments @(
                '-o', 'NUL', '-X', 'PUT',
                '-H', "Content-Type: $ContentType",
                '--data-binary', "@$Source",
                "$baseUrl/internal/objects/$Key") -Secrets @("header = $(Quote "Authorization: Bearer $token")")

            if ($code -ne '204') { Fail "сервер отверг $Key`: HTTP $code" }
        }
        'legacy' {
            $r2 = $settings.r2
            if (-not $r2 -or -not $r2.accessKeyId -or -not $r2.secretAccessKey) {
                Fail "в $Config не заполнены ключи legacy.r2"
            }

            $code = Invoke-Curl -Arguments @(
                '-o', 'NUL',
                '--aws-sigv4', 'aws:amz:auto:s3',
                '-H', "Content-Type: $ContentType",
                '--upload-file', $Source,
                "https://$($r2.accountId).r2.cloudflarestorage.com/$($r2.bucket)/$Key") `
                -Secrets @("user = $(Quote "$($r2.accessKeyId):$($r2.secretAccessKey)")")

            if ($code -ne '200') { Fail "R2 отверг $Key`: HTTP $code" }
        }
    }

    Ok "выложено: $Key"
}

if (-not $VerifyOnly) {
    Step 'выкладка: сперва установщик, потом манифест'
    Publish-Object $installer "releases/EliteSIP-$Version.exe" 'application/octet-stream'
    Publish-Object $envelope 'releases/current.json' 'application/json'
}

if ($DryRun) {
    Write-Host ''
    Write-Host 'сухой прогон: на канале ничего не изменилось' -ForegroundColor Yellow
    exit 0
}

# --- 3. Что канал отдаёт ------------------------------------------------------

Step 'проверка канала с парой Basic'

$work = Join-Path ([System.IO.Path]::GetTempPath()) "elitesip-publish-$([guid]::NewGuid())"
New-Item -ItemType Directory $work | Out-Null

try {
    $servedManifest = Join-Path $work 'current.json'
    $code = Invoke-Curl -Arguments @('-o', $servedManifest, "$baseUrl/releases/current.json") -Secrets $basicSecret
    if ($code -ne '200') { Fail "манифест не отдан: HTTP $code" }

    $same = (Get-FileHash $servedManifest).Hash -eq (Get-FileHash $envelope).Hash
    if (-not $same) { Fail 'канал отдаёт не тот манифест (кэш или чужая выкладка)' }
    Ok 'манифест отдаётся, байт в байт тот, что подписан'

    $servedInstaller = Join-Path $work "EliteSIP-$Version.exe"
    $code = Invoke-Curl -Arguments @('-o', $servedInstaller, $expectedUrl) -Secrets $basicSecret
    if ($code -ne '200') { Fail "установщик не отдан: HTTP $code" }

    $servedDigest = (Get-FileHash $servedInstaller -Algorithm SHA256).Hash
    $servedSize = (Get-Item $servedInstaller).Length
    if ($servedDigest -ne $digest -or $servedSize -ne $size) {
        Fail "скачанный установщик не сходится: sha256 $servedDigest, $servedSize байт"
    }
    Ok "установщик скачивается: sha256 и размер совпадают"

    # Докачка: клиент её не использует, но сервер обещает 206, и прокси между
    # ним и машиной ломают именно это.
    $code = Invoke-Curl -Arguments @('-o', 'NUL', '-r', '0-1023', $expectedUrl) -Secrets $basicSecret
    if ($code -eq '206') { Ok 'Range отдаёт 206' } else { Write-Host "  внимание: Range ответил $code, а не 206" -ForegroundColor Yellow }

    $code = Invoke-Curl -Arguments @('-o', 'NUL', "$baseUrl/releases/current.json")
    if ($code -ne '401') { Fail "без пары манифест отдаётся с кодом $code — канал открыт" }
    Ok 'без пары — 401'
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "Канал $Channel отдаёт $Version." -ForegroundColor Green
Write-Host 'Подпись манифеста проверена при сборке (releasekit verify); отдаётся он байт в байт.'
