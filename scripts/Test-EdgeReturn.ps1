# 边缘返回检测压力测试
# 模拟各种鼠标移动场景，验证新的 AdaptiveEdgeReturnDetector

param(
    [string]$Host = "192.168.1.101",
    [int]$Port = 47800,
    [string]$Code = "1234",
    [string]$LanCtrlPath = "C:\Users\yangyang\Desktop\跨win系统调试\artifacts"
)

$CliPath = Join-Path $LanCtrlPath "LanCtrl.Cli.exe"

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "边缘返回检测压力测试" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

function Test-Scenario {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    Write-Host "[测试] $Name" -ForegroundColor Yellow
    Start-Sleep -Seconds 1

    try {
        & $Action
        Write-Host "[完成] $Name" -ForegroundColor Green
    } catch {
        Write-Host "[失败] $Name - $($_.Exception.Message)" -ForegroundColor Red
    }

    Start-Sleep -Seconds 2
    Write-Host ""
}

# 测试 1: 快速甩动（应该不触发返回）
Test-Scenario "快速甩动测试" {
    Write-Host "  模拟快速从左到右甩动鼠标" -ForegroundColor Gray

    # 快速移动到右边缘
    & $CliPath --host $Host --port $Port --code $Code --input "move,100,540" | Out-Null
    Start-Sleep -Milliseconds 50
    & $CliPath --host $Host --port $Port --code $Code --input "move,1900,540" | Out-Null
    Start-Sleep -Milliseconds 50
    & $CliPath --host $Host --port $Port --code $Code --input "move,1919,540" | Out-Null

    Write-Host "  预期: 单次大位移被限制，不应触发返回" -ForegroundColor Cyan
}

# 测试 2: 缓慢推动（应该触发返回）
Test-Scenario "缓慢推动测试" {
    Write-Host "  模拟缓慢向右边缘推动鼠标" -ForegroundColor Gray

    # 从中心开始，慢慢推向右边缘
    $x = 960
    while ($x -lt 1919) {
        & $CliPath --host $Host --port $Port --code $Code --input "move,$x,540" | Out-Null
        Start-Sleep -Milliseconds 100
        $x += 10
    }

    Write-Host "  预期: 累计推动超过 90px，应触发返回" -ForegroundColor Cyan
}

# 测试 3: 停在边缘（不应触发）
Test-Scenario "停在边缘测试" {
    Write-Host "  模拟鼠标停在边缘不动" -ForegroundColor Gray

    & $CliPath --host $Host --port $Port --code $Code --input "move,1919,540" | Out-Null
    Start-Sleep -Milliseconds 500

    Write-Host "  预期: 停在边缘不动，不应触发返回" -ForegroundColor Cyan
}

# 测试 4: 轻微抖动（不应触发）
Test-Scenario "边缘抖动测试" {
    Write-Host "  模拟在边缘附近轻微抖动" -ForegroundColor Gray

    & $CliPath --host $Host --port $Port --code $Code --input "move,1919,540" | Out-Null
    Start-Sleep -Milliseconds 50
    & $CliPath --host $Host --port $Port --code $Code --input "move,1915,540" | Out-Null
    Start-Sleep -Milliseconds 50
    & $CliPath --host $Host --port $Port --code $Code --input "move,1918,540" | Out-Null
    Start-Sleep -Milliseconds 50
    & $CliPath --host $Host --port $Port --code $Code --input "move,1916,540" | Out-Null

    Write-Host "  预期: 小范围抖动，不应触发返回" -ForegroundColor Cyan
}

# 测试 5: 来回移动（不应触发）
Test-Scenario "来回移动测试" {
    Write-Host "  模拟在边缘来回移动" -ForegroundColor Gray

    & $CliPath --host $Host --port $Port --code $Code --input "move,1919,540" | Out-Null
    Start-Sleep -Milliseconds 100
    & $CliPath --host $Host --port $Port --code $Code --input "move,1850,540" | Out-Null
    Start-Sleep -Milliseconds 100
    & $CliPath --host $Host --port $Port --code $Code --input "move,1919,540" | Out-Null
    Start-Sleep -Milliseconds 100
    & $CliPath --host $Host --port $Port --code $Code --input "move,1850,540" | Out-Null

    Write-Host "  预期: 来回移动没有持续推动，不应触发返回" -ForegroundColor Cyan
}

# 测试 6: 持续推动（应该触发）
Test-Scenario "持续推动测试" {
    Write-Host "  模拟持续向边缘推动（300ms 窗口内）" -ForegroundColor Gray

    & $CliPath --host $Host --port $Port --code $Code --input "move,1850,540" | Out-Null
    Start-Sleep -Milliseconds 50
    & $CliPath --host $Host --port $Port --code $Code --input "move,1880,540" | Out-Null
    Start-Sleep -Milliseconds 50
    & $CliPath --host $Host --port $Port --code $Code --input "move,1900,540" | Out-Null
    Start-Sleep -Milliseconds 50
    & $CliPath --host $Host --port $Port --code $Code --input "move,1919,540" | Out-Null

    Write-Host "  预期: 300ms 内累计超过 90px，应触发返回" -ForegroundColor Cyan
}

# 测试 7: 角落区域（应该被阻挡）
Test-Scenario "角落阻挡测试" {
    Write-Host "  测试屏幕角落是否阻挡切换" -ForegroundColor Gray

    # 右上角
    & $CliPath --host $Host --port $Port --code $Code --input "move,1919,50" | Out-Null
    Start-Sleep -Milliseconds 200

    # 右下角
    & $CliPath --host $Host --port $Port --code $Code --input "move,1919,1030" | Out-Null
    Start-Sleep -Milliseconds 200

    Write-Host "  预期: 角落区域（80px）应阻止切换" -ForegroundColor Cyan
}

# 读取日志查看结果
Write-Host "========== 日志分析 ==========" -ForegroundColor Cyan
Write-Host "读取最近的控制日志..." -ForegroundColor Yellow
Write-Host ""

$logPath = "C:\ProgramData\LanMouseShare\Logs\live"
$result = & $CliPath --host $Host --port $Port --code $Code --tail "$logPath\Agent-*.log" --lines 50 2>&1

if ($result) {
    # 查找边缘返回相关的日志
    $edgeReturnLogs = $result | Select-String "edge\.return"

    if ($edgeReturnLogs) {
        Write-Host "发现边缘返回日志:" -ForegroundColor Green
        foreach ($log in $edgeReturnLogs) {
            Write-Host "  $log" -ForegroundColor Gray
        }
    } else {
        Write-Host "未发现边缘返回日志" -ForegroundColor Yellow
    }

    Write-Host ""

    # 查找状态变化
    $stateLogs = $result | Select-String "control\.link\.role|control\.link\.released"

    if ($stateLogs) {
        Write-Host "发现控制状态变化:" -ForegroundColor Green
        foreach ($log in $stateLogs) {
            Write-Host "  $log" -ForegroundColor Gray
        }
    }
}

Write-Host ""
Write-Host "========== 测试完成 ==========" -ForegroundColor Cyan
Write-Host "请检查上述日志，验证边缘返回行为是否符合预期。" -ForegroundColor Yellow
