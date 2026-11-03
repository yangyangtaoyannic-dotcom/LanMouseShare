# LanCtrl 远程调试脚本集合
# 用于调试 LanMouseShare 项目

param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('info', 'logs', 'restart', 'enable-new', 'disable-new', 'monitor')]
    [string]$Command,

    [string]$Host = "192.168.1.100",
    [int]$Port = 47800,
    [string]$Code = "1234",
    [string]$LanCtrlPath = "C:\Users\yangyang\Desktop\跨win系统调试\artifacts"
)

$CliPath = Join-Path $LanCtrlPath "LanCtrl.Cli.exe"

if (-not (Test-Path $CliPath)) {
    Write-Host "[错误] 找不到 LanCtrl.Cli.exe: $CliPath" -ForegroundColor Red
    exit 1
}

function Get-SystemInfo {
    Write-Host "获取系统信息..." -ForegroundColor Cyan
    & $CliPath --host $Host --port $Port --code $Code --info
}

function Get-Logs {
    param([int]$Lines = 100)

    Write-Host "读取最近 $Lines 行日志..." -ForegroundColor Cyan
    $logPath = "C:\ProgramData\LanMouseShare\Logs\live\Agent-*.log"
    & $CliPath --host $Host --port $Port --code $Code --tail $logPath --lines $Lines
}

function Restart-Agent {
    Write-Host "重启 Agent..." -ForegroundColor Cyan

    # 停止 Agent
    Write-Host "1. 停止 Agent 进程..." -ForegroundColor Yellow
    & $CliPath --host $Host --port $Port --code $Code --run "taskkill /F /IM LanMouseShare.Agent.exe" 2>&1 | Out-Null
    Start-Sleep -Seconds 2

    # 启动 Agent
    Write-Host "2. 启动 Agent..." -ForegroundColor Yellow
    $agentPath = "C:\Program Files\LanMouseShare\Agent\LanMouseShare.Agent.exe"
    & $CliPath --host $Host --port $Port --code $Code --run "start `"`" `"$agentPath`"" 2>&1 | Out-Null
    Start-Sleep -Seconds 3

    # 验证
    $result = & $CliPath --host $Host --port $Port --code $Code --run "tasklist | findstr /i Agent" 2>&1
    if ($result -match "Agent") {
        Write-Host "[成功] Agent 已启动" -ForegroundColor Green
    } else {
        Write-Host "[失败] Agent 未能启动" -ForegroundColor Red
    }
}

function Enable-NewControllers {
    Write-Host "启用新控制器..." -ForegroundColor Cyan

    $configPath = "C:\ProgramData\LanMouseShare\config.json"

    # 备份
    Write-Host "1. 备份配置..." -ForegroundColor Yellow
    & $CliPath --host $Host --port $Port --code $Code --run "copy `"$configPath`" `"$configPath.backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')`"" 2>&1 | Out-Null

    # 修改配置
    Write-Host "2. 修改配置..." -ForegroundColor Yellow
    $psCommand = @"
`$configPath = '$configPath'
`$json = Get-Content `$configPath -Raw | ConvertFrom-Json
if (-not (`$json.PSObject.Properties.Name -contains 'UseNewControllers')) {
    `$json | Add-Member -NotePropertyName 'UseNewControllers' -NotePropertyValue `$true
} else {
    `$json.UseNewControllers = `$true
}
`$json | ConvertTo-Json -Depth 10 | Set-Content `$configPath -Encoding UTF8
Write-Host 'UseNewControllers = true'
"@

    $result = & $CliPath --host $Host --port $Port --code $Code --run "powershell -Command `"$psCommand`"" 2>&1

    if ($LASTEXITCODE -eq 0) {
        Write-Host "[成功] 配置已更新" -ForegroundColor Green
        Write-Host "3. 重启 Agent 以应用更改..." -ForegroundColor Yellow
        Restart-Agent
    } else {
        Write-Host "[失败] 配置更新失败" -ForegroundColor Red
    }
}

function Disable-NewControllers {
    Write-Host "禁用新控制器..." -ForegroundColor Cyan

    $configPath = "C:\ProgramData\LanMouseShare\config.json"

    $psCommand = @"
`$configPath = '$configPath'
`$json = Get-Content `$configPath -Raw | ConvertFrom-Json
if (`$json.PSObject.Properties.Name -contains 'UseNewControllers') {
    `$json.UseNewControllers = `$false
}
`$json | ConvertTo-Json -Depth 10 | Set-Content `$configPath -Encoding UTF8
Write-Host 'UseNewControllers = false'
"@

    $result = & $CliPath --host $Host --port $Port --code $Code --run "powershell -Command `"$psCommand`"" 2>&1

    if ($LASTEXITCODE -eq 0) {
        Write-Host "[成功] 配置已更新" -ForegroundColor Green
        Write-Host "重启 Agent 以应用更改..." -ForegroundColor Yellow
        Restart-Agent
    } else {
        Write-Host "[失败] 配置更新失败" -ForegroundColor Red
    }
}

function Start-Monitor {
    Write-Host "开始实时监控日志..." -ForegroundColor Cyan
    Write-Host "按 Ctrl+C 停止监控" -ForegroundColor Yellow
    Write-Host ""

    $logPath = "C:\ProgramData\LanMouseShare\Logs\live\Agent-*.log"
    $offset = 0

    while ($true) {
        $result = & $CliPath --host $Host --port $Port --code $Code --tail $logPath --tail-offset $offset --lines 10 2>&1

        if ($result -and $result.Length -gt 0) {
            foreach ($line in $result) {
                if ($line -match '\[ERROR\]') {
                    Write-Host $line -ForegroundColor Red
                } elseif ($line -match '\[WARN\]') {
                    Write-Host $line -ForegroundColor Yellow
                } elseif ($line -match 'control\.link|edge\.|input\.') {
                    Write-Host $line -ForegroundColor Cyan
                } else {
                    Write-Host $line -ForegroundColor Gray
                }
            }

            # 更新偏移量
            $offset += $result.Length
        }

        Start-Sleep -Seconds 1
    }
}

# 执行命令
switch ($Command) {
    'info' {
        Get-SystemInfo
    }
    'logs' {
        Get-Logs -Lines 100
    }
    'restart' {
        Restart-Agent
    }
    'enable-new' {
        Enable-NewControllers
    }
    'disable-new' {
        Disable-NewControllers
    }
    'monitor' {
        Start-Monitor
    }
}
