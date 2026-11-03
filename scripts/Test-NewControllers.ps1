# LanMouseShare 自动化测试脚本
# 用于双机测试新控制器实现

param(
    [string]$ControllerHost = "192.168.1.100",
    [string]$AgentHost = "192.168.1.101",
    [int]$Port = 47800,
    [string]$Code = "1234",
    [string]$LanCtrlPath = "C:\Users\yangyang\Desktop\跨win系统调试\artifacts"
)

$ErrorActionPreference = "Continue"
$CliPath = Join-Path $LanCtrlPath "LanCtrl.Cli.exe"

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "LanMouseShare 自动化测试脚本" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# 检查 LanCtrl.Cli 是否存在
if (-not (Test-Path $CliPath)) {
    Write-Host "[错误] 找不到 LanCtrl.Cli.exe: $CliPath" -ForegroundColor Red
    exit 1
}

$TestResults = @()

function Test-Connection {
    param([string]$Host, [string]$Name)

    Write-Host "[测试] 连接到 $Name ($Host)..." -ForegroundColor Yellow

    $result = & $CliPath --host $Host --port $Port --code $Code --info --json 2>&1

    if ($LASTEXITCODE -eq 0) {
        Write-Host "[成功] $Name 连接成功" -ForegroundColor Green
        $script:TestResults += [PSCustomObject]@{
            Test = "连接-$Name"
            Status = "成功"
            Details = "连接正常"
        }
        return $true
    } else {
        Write-Host "[失败] $Name 连接失败: $result" -ForegroundColor Red
        $script:TestResults += [PSCustomObject]@{
            Test = "连接-$Name"
            Status = "失败"
            Details = $result
        }
        return $false
    }
}

function Test-AgentRunning {
    param([string]$Host, [string]$Name)

    Write-Host "[测试] 检查 $Name 上的 Agent 是否运行..." -ForegroundColor Yellow

    $result = & $CliPath --host $Host --port $Port --code $Code --run "tasklist | findstr /i agent" --quiet 2>&1

    if ($LASTEXITCODE -eq 0 -and $result -match "Agent") {
        Write-Host "[成功] Agent 正在运行" -ForegroundColor Green
        $script:TestResults += [PSCustomObject]@{
            Test = "Agent运行-$Name"
            Status = "成功"
            Details = "Agent 进程已启动"
        }
        return $true
    } else {
        Write-Host "[失败] Agent 未运行" -ForegroundColor Red
        $script:TestResults += [PSCustomObject]@{
            Test = "Agent运行-$Name"
            Status = "失败"
            Details = "找不到 Agent 进程"
        }
        return $false
    }
}

function Test-ConfigSetting {
    param([string]$Host, [string]$Name)

    Write-Host "[测试] 检查 $Name 的配置文件..." -ForegroundColor Yellow

    $configPath = "C:\ProgramData\LanMouseShare\config.json"
    $result = & $CliPath --host $Host --port $Port --code $Code --run "type `"$configPath`"" 2>&1

    if ($LASTEXITCODE -eq 0 -and $result -match "UseNewControllers") {
        if ($result -match '"UseNewControllers":\s*true') {
            Write-Host "[成功] UseNewControllers = true" -ForegroundColor Green
            $script:TestResults += [PSCustomObject]@{
                Test = "配置-$Name"
                Status = "成功"
                Details = "UseNewControllers 已启用"
            }
        } else {
            Write-Host "[警告] UseNewControllers = false (旧实现)" -ForegroundColor Yellow
            $script:TestResults += [PSCustomObject]@{
                Test = "配置-$Name"
                Status = "警告"
                Details = "使用旧实现"
            }
        }
    } else {
        Write-Host "[失败] 无法读取配置" -ForegroundColor Red
        $script:TestResults += [PSCustomObject]@{
            Test = "配置-$Name"
            Status = "失败"
            Details = "配置文件不存在或无法读取"
        }
    }
}

function Test-LogOutput {
    param([string]$Host, [string]$Name)

    Write-Host "[测试] 检查 $Name 的日志输出..." -ForegroundColor Yellow

    $logPath = "C:\ProgramData\LanMouseShare\Logs\live"
    $result = & $CliPath --host $Host --port $Port --code $Code --run "dir /b /o-d `"$logPath\*.log`"" 2>&1

    if ($LASTEXITCODE -eq 0 -and $result) {
        $latestLog = ($result -split "`n")[0].Trim()
        Write-Host "[信息] 最新日志: $latestLog" -ForegroundColor Cyan

        # 读取最后 20 行
        $tailResult = & $CliPath --host $Host --port $Port --code $Code --tail "$logPath\$latestLog" --lines 20 2>&1

        if ($tailResult -match "controller\.integration\.mode") {
            if ($tailResult -match "mode=new_controllers") {
                Write-Host "[成功] 正在使用新控制器" -ForegroundColor Green
                $script:TestResults += [PSCustomObject]@{
                    Test = "日志检查-$Name"
                    Status = "成功"
                    Details = "新控制器已激活"
                }
            } else {
                Write-Host "[信息] 使用传统实现" -ForegroundColor Yellow
                $script:TestResults += [PSCustomObject]@{
                    Test = "日志检查-$Name"
                    Status = "信息"
                    Details = "传统实现"
                }
            }
        } else {
            Write-Host "[信息] 日志中无控制器模式记录" -ForegroundColor Yellow
            $script:TestResults += [PSCustomObject]@{
                Test = "日志检查-$Name"
                Status = "信息"
                Details = "无明确模式标记"
            }
        }

        # 检查是否有错误
        if ($tailResult -match "\[ERROR\]") {
            Write-Host "[警告] 发现错误日志" -ForegroundColor Yellow
            $errors = $tailResult | Select-String "\[ERROR\]"
            Write-Host "  错误: $($errors[0])" -ForegroundColor Red
        }
    } else {
        Write-Host "[失败] 无法读取日志" -ForegroundColor Red
    }
}

function Test-MouseMovement {
    param([string]$Host, [string]$Name)

    Write-Host "[测试] 测试 $Name 的鼠标移动..." -ForegroundColor Yellow

    # 移动鼠标到屏幕中心
    $result = & $CliPath --host $Host --port $Port --code $Code --input "move,960,540" 2>&1

    if ($LASTEXITCODE -eq 0) {
        Write-Host "[成功] 鼠标移动成功" -ForegroundColor Green
        $script:TestResults += [PSCustomObject]@{
            Test = "鼠标移动-$Name"
            Status = "成功"
            Details = "移动到 (960, 540)"
        }
    } else {
        Write-Host "[失败] 鼠标移动失败: $result" -ForegroundColor Red
        $script:TestResults += [PSCustomObject]@{
            Test = "鼠标移动-$Name"
            Status = "失败"
            Details = $result
        }
    }
}

function Enable-NewControllers {
    param([string]$Host, [string]$Name)

    Write-Host "[操作] 在 $Name 上启用新控制器..." -ForegroundColor Yellow

    $configPath = "C:\ProgramData\LanMouseShare\config.json"

    # 备份配置
    & $CliPath --host $Host --port $Port --code $Code --run "copy `"$configPath`" `"$configPath.backup`"" 2>&1 | Out-Null

    # 修改配置（使用 PowerShell）
    $psCommand = @"
`$config = Get-Content '$configPath' | ConvertFrom-Json
`$config | Add-Member -NotePropertyName 'UseNewControllers' -NotePropertyValue `$true -Force
`$config | ConvertTo-Json -Depth 10 | Set-Content '$configPath'
Write-Host 'UseNewControllers 已设置为 true'
"@

    $result = & $CliPath --host $Host --port $Port --code $Code --run "powershell -Command `"$psCommand`"" 2>&1

    if ($LASTEXITCODE -eq 0) {
        Write-Host "[成功] 配置已更新" -ForegroundColor Green
        Write-Host "[提示] 需要重启 Agent 才能生效" -ForegroundColor Yellow
    } else {
        Write-Host "[失败] 配置更新失败: $result" -ForegroundColor Red
    }
}

# 主测试流程
Write-Host "========== 测试开始 ==========" -ForegroundColor Cyan
Write-Host ""

# 1. 连接测试
Write-Host ">>> 阶段 1: 连接测试" -ForegroundColor Magenta
$controllerConnected = Test-Connection $ControllerHost "主控端"
$agentConnected = Test-Connection $AgentHost "被控端"
Write-Host ""

if (-not $controllerConnected -or -not $agentConnected) {
    Write-Host "[错误] 连接失败，测试中止" -ForegroundColor Red
    exit 1
}

# 2. Agent 运行检查
Write-Host ">>> 阶段 2: Agent 运行检查" -ForegroundColor Magenta
Test-AgentRunning $ControllerHost "主控端"
Test-AgentRunning $AgentHost "被控端"
Write-Host ""

# 3. 配置检查
Write-Host ">>> 阶段 3: 配置检查" -ForegroundColor Magenta
Test-ConfigSetting $ControllerHost "主控端"
Test-ConfigSetting $AgentHost "被控端"
Write-Host ""

# 4. 日志检查
Write-Host ">>> 阶段 4: 日志检查" -ForegroundColor Magenta
Test-LogOutput $ControllerHost "主控端"
Test-LogOutput $AgentHost "被控端"
Write-Host ""

# 5. 鼠标移动测试
Write-Host ">>> 阶段 5: 鼠标移动测试" -ForegroundColor Magenta
Test-MouseMovement $AgentHost "被控端"
Write-Host ""

# 测试报告
Write-Host "========== 测试报告 ==========" -ForegroundColor Cyan
$TestResults | Format-Table -AutoSize
Write-Host ""

$successCount = ($TestResults | Where-Object { $_.Status -eq "成功" }).Count
$totalCount = $TestResults.Count
$successRate = [math]::Round(($successCount / $totalCount) * 100, 2)

Write-Host "总计: $totalCount 项测试" -ForegroundColor Cyan
Write-Host "成功: $successCount 项" -ForegroundColor Green
Write-Host "成功率: $successRate%" -ForegroundColor $(if ($successRate -ge 80) { "Green" } else { "Yellow" })
Write-Host ""

# 询问是否启用新控制器
if ($TestResults | Where-Object { $_.Test -like "配置-*" -and $_.Details -like "*旧实现*" }) {
    Write-Host "检测到某些机器仍在使用旧实现。" -ForegroundColor Yellow
    $enable = Read-Host "是否要在所有机器上启用新控制器? (y/n)"

    if ($enable -eq "y") {
        Enable-NewControllers $ControllerHost "主控端"
        Enable-NewControllers $AgentHost "被控端"

        Write-Host ""
        Write-Host "[提示] 请重启两台机器上的 Agent，然后重新运行此测试脚本" -ForegroundColor Yellow
    }
}

Write-Host "========== 测试完成 ==========" -ForegroundColor Cyan
