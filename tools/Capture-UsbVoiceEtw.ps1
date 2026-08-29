[CmdletBinding()]
param(
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList
    )

    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath 失败（ExitCode=$LASTEXITCODE）：$($output -join [Environment]::NewLine)"
    }

    return $output
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Marker {
    param(
        [Parameter(Mandatory)]
        [string]$Phase,

        [Parameter(Mandatory)]
        [string]$Path
    )

    [ordered]@{
        timestamp = [DateTimeOffset]::Now.ToString('o')
        phase = $Phase
    } | ConvertTo-Json -Compress | Add-Content -LiteralPath $Path -Encoding utf8
}

function Invoke-Countdown {
    param(
        [Parameter(Mandatory)]
        [int]$Seconds
    )

    for ($remaining = $Seconds; $remaining -ge 1; $remaining--) {
        Write-Host "  $remaining..."
        Start-Sleep -Seconds 1
    }
}

$requiredProviders = @(
    'Microsoft-Windows-USB-USBXHCI',
    'Microsoft-Windows-USB-UCX',
    'Microsoft-Windows-USB-USBHUB3'
)

if ($SelfTest) {
    $commands = 'logman.exe', 'tracerpt.exe'
    $commandResults = foreach ($command in $commands) {
        $resolved = Get-Command $command -ErrorAction SilentlyContinue
        [ordered]@{
            command = $command
            available = $null -ne $resolved
            path = if ($null -ne $resolved) { $resolved.Source } else { $null }
        }
    }

    $providerResults = foreach ($provider in $requiredProviders) {
        & logman.exe query providers $provider *> $null
        [ordered]@{
            provider = $provider
            available = $LASTEXITCODE -eq 0
        }
    }

    [ordered]@{
        selfTest = 'UsbVoiceEtw'
        commands = $commandResults
        providers = $providerResults
        scriptPath = $PSCommandPath
    } | ConvertTo-Json -Depth 4

    if (($commandResults.available -contains $false) -or ($providerResults.available -contains $false)) {
        exit 2
    }

    exit 0
}

if (-not (Test-IsAdministrator)) {
    Write-Host '本诊断必须以管理员身份启动 Windows 自带的 USB ETW 会话。'
    Write-Host '即将弹出 Windows UAC；允许后会打开新的诊断窗口。'
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        "`"$PSCommandPath`""
    )
    exit $process.ExitCode
}

if (Get-Process -Name 'VoiceRemoteBridge.App' -ErrorAction SilentlyContinue) {
    Write-Host '请先退出 Voice Remote Bridge，再重新运行本诊断。' -ForegroundColor Yellow
    Read-Host '按 Enter 关闭'
    exit 3
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputDirectory = Join-Path $projectRoot "artifacts\usb-etw-release-$timestamp"
$markerPath = Join-Path $outputDirectory 'phase-markers.jsonl'
$etlBasePath = Join-Path $outputDirectory 'usb-full.etl'
$xmlPath = Join-Path $outputDirectory 'usb-full.xml'
$summaryPath = Join-Path $outputDirectory 'summary.json'
$sessionName = "VoiceRemoteBridgeUsbTrace_$PID"
$sessionCreated = $false
$sessionStarted = $false

New-Item -ItemType Directory -Path $outputDirectory | Out-Null

Write-Host 'Voice Remote Bridge - USB 总线松手诊断' -ForegroundColor Cyan
Write-Host '只使用 Windows 自带 ETW，捕获约 25 秒，不向遥控器写入命令。'
Write-Host '采集期间不要操作其他 USB 键盘、鼠标或存储设备。'
Write-Host 'USB 等时音频的内容不会写入 ETW；诊断也不会保存转写文字。'
Write-Host ''
Read-Host '遥控器和接收端准备好后，按 Enter 开始'

try {
    Invoke-CheckedCommand -FilePath 'logman.exe' -ArgumentList @(
        'create', 'trace', '-n', $sessionName,
        '-o', $etlBasePath,
        '-nb', '64', '256', '-bs', '128'
    ) | Out-Null
    $sessionCreated = $true

    foreach ($provider in $requiredProviders) {
        Invoke-CheckedCommand -FilePath 'logman.exe' -ArgumentList @(
            'update', 'trace', '-n', $sessionName,
            '-p', $provider, '(Default,FullDataBusTrace)'
        ) | Out-Null
    }

    Invoke-CheckedCommand -FilePath 'logman.exe' -ArgumentList @('start', '-n', $sessionName) | Out-Null
    $sessionStarted = $true
    Write-Marker -Phase 'trace-started' -Path $markerPath

    Write-Host ''
    Write-Host '基线 5 秒：不要碰遥控器。'
    Write-Marker -Phase 'released-baseline-start' -Path $markerPath
    Start-Sleep -Seconds 5

    foreach ($trial in 1..2) {
        Write-Host ''
        Write-Host "第 $trial 次：倒计时结束听到提示音后，立即按住语音键。"
        Write-Marker -Phase "trial-$trial-press-countdown-start" -Path $markerPath
        Invoke-Countdown -Seconds 3
        [Console]::Beep(900, 180)
        Write-Marker -Phase "trial-$trial-press-cue" -Path $markerPath
        Write-Host '>>> 现在按住，保持 4 秒，不要松开。' -ForegroundColor Green
        Start-Sleep -Seconds 4

        [Console]::Beep(550, 250)
        Write-Marker -Phase "trial-$trial-release-cue" -Path $markerPath
        Write-Host '>>> 现在立即松开语音键，之后不要再碰遥控器。' -ForegroundColor Yellow
        Start-Sleep -Seconds 3
        Write-Marker -Phase "trial-$trial-released-observation-end" -Path $markerPath
    }

    Write-Marker -Phase 'trace-scenario-complete' -Path $markerPath
}
finally {
    if ($sessionStarted) {
        & logman.exe stop -n $sessionName *> $null
        $sessionStarted = $false
    }

    if ($sessionCreated) {
        & logman.exe delete -n $sessionName *> $null
        $sessionCreated = $false
    }
}

$etlFile = Get-ChildItem -LiteralPath $outputDirectory -Filter 'usb-full*.etl' |
    Sort-Object LastWriteTime |
    Select-Object -Last 1
if ($null -eq $etlFile) {
    throw "ETW 会话已结束，但在 $outputDirectory 中没有找到 ETL 文件。"
}

Write-Host ''
Write-Host '正在把 ETL 转换为可分析 XML，请稍候……'
Invoke-CheckedCommand -FilePath 'tracerpt.exe' -ArgumentList @(
    $etlFile.FullName, '-o', $xmlPath, '-of', 'XML', '-y'
) | Out-Null

$summary = [ordered]@{
    capturedAt = [DateTimeOffset]::Now.ToString('o')
    purpose = 'Determine whether the receiver sends a USB transfer on physical voice-key release.'
    sessionName = $sessionName
    providers = $requiredProviders
    keyword = 'Default,FullDataBusTrace'
    etlPath = $etlFile.FullName
    etlBytes = $etlFile.Length
    etlSha256 = (Get-FileHash -LiteralPath $etlFile.FullName -Algorithm SHA256).Hash
    xmlPath = $xmlPath
    xmlBytes = (Get-Item -LiteralPath $xmlPath).Length
    markerPath = $markerPath
    safety = [ordered]@{
        deviceWrites = $false
        audioSavedByTool = $false
        transcriptionSaved = $false
        usbIsochronousPayloadRequested = $false
    }
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8

Write-Host ''
Write-Host 'USB 总线松手诊断已完成。' -ForegroundColor Green
Write-Host "结果目录：$outputDirectory"
Write-Host '请告诉 Codex：“USB ETW 诊断已完成”。'
Read-Host '按 Enter 关闭窗口'
