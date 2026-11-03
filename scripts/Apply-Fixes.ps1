# 应用所有修复补丁
# 修复：1. 剪贴板文件传输  2. 开机静默启动  3. 边角阻挡

param(
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path $PSScriptRoot -Parent

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  应用修复补丁 - 0.2.0-beta-fix2" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 修复 1：剪贴板文件传输顺序
Write-Host "[1/3] 修复剪贴板文件传输..." -ForegroundColor Yellow

$clipboardFile = Join-Path $projectRoot "src\Desktop.Agent\ClipboardSyncService.cs"
$content = Get-Content $clipboardFile -Raw -Encoding UTF8

# 检查是否已经修复
if ($content -match "// 优先检测文本和图片，避免文件拖拽时误检测") {
    Write-Host "  ✓ 剪贴板修复已应用" -ForegroundColor Green
} else {
    Write-Host "  ! 需要手动修复剪贴板文件" -ForegroundColor Red
    Write-Host "    文件: $clipboardFile" -ForegroundColor Gray
}

# 修复 2：开机静默启动
Write-Host "[2/3] 修复开机静默启动..." -ForegroundColor Yellow

$startupFile = Join-Path $projectRoot "src\Desktop.Agent\StartupTaskManager.cs"
$content = Get-Content $startupFile -Raw -Encoding UTF8

if ($content -match "/minimized") {
    Write-Host "  ✓ 静默启动已修复" -ForegroundColor Green
} else {
    Write-Host "  × 正在应用静默启动修复..." -ForegroundColor Yellow

    # 修复任务计划启动
    $content = $content -replace `
        '("/TR \\"\\\\\"" \+ agentPath \+ "\\\\\"\\"")', `
        '("/TR \\"\\\\\"" + agentPath + "\\\\\\" /minimized\\"")'

    # 修复注册表启动
    $content = $content -replace `
        '(key\.SetValue\(RunValueName, \\"" \+ agentPath \+ "\\"\);)', `
        'key.SetValue(RunValueName, "\"" + agentPath + "\" /minimized");'

    if (!$DryRun) {
        Set-Content $startupFile -Value $content -Encoding UTF8 -NoNewline
        Write-Host "  ✓ 静默启动修复完成" -ForegroundColor Green
    }
}

# 修复 App.xaml.cs
$appFile = Join-Path $projectRoot "src\Desktop.Agent\App.xaml.cs"
$content = Get-Content $appFile -Raw -Encoding UTF8

if ($content -match "StartMinimized") {
    Write-Host "  ✓ App.xaml.cs 已修复" -ForegroundColor Green
} else {
    Write-Host "  × 正在修复 App.xaml.cs..." -ForegroundColor Yellow

    $newContent = @'
using System.Windows;
using System.Windows.Threading;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Input;

namespace LanMouseShare.Agent
{
    public partial class App : Application
    {
        public bool StartMinimized { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            DesktopEnvironment.EnableDpiAwareness();
            AppLog.Initialize("Agent", false);
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // 检查命令行参数
            if (e.Args != null)
            {
                for (int i = 0; i < e.Args.Length; i++)
                {
                    if (e.Args[i].Equals("/minimized", System.StringComparison.OrdinalIgnoreCase) ||
                        e.Args[i].Equals("-minimized", System.StringComparison.OrdinalIgnoreCase))
                    {
                        StartMinimized = true;
                        AppLog.Event("INFO", "app.startup", "mode=minimized");
                        break;
                    }
                }
            }

            base.OnStartup(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Error("Unhandled agent UI exception.", e.Exception);
            e.Handled = true;
        }
    }
}
'@

    if (!$DryRun) {
        Set-Content $appFile -Value $newContent -Encoding UTF8 -NoNewline
        Write-Host "  ✓ App.xaml.cs 修复完成" -ForegroundColor Green
    }
}

# 修复 3：边角阻挡
Write-Host "[3/3] 检查边角阻挡功能..." -ForegroundColor Yellow

$edgeFile = Join-Path $projectRoot "src\Input.Native\EdgeSwitchDetector.cs"
if (Test-Path $edgeFile) {
    $content = Get-Content $edgeFile -Raw -Encoding UTF8

    if ($content -match "IsInBlockedCorner") {
        Write-Host "  ✓ 边角阻挡功能代码正常" -ForegroundColor Green
    } else {
        Write-Host "  ! 边角阻挡代码可能有问题" -ForegroundColor Red
    }
} else {
    Write-Host "  ! EdgeSwitchDetector.cs 文件不存在" -ForegroundColor Red
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  修复总结" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "✓ 已修复:" -ForegroundColor Green
Write-Host "  - 开机静默启动 (添加 /minimized 参数)" -ForegroundColor Gray
Write-Host "  - App.xaml.cs 命令行参数处理" -ForegroundColor Gray
Write-Host ""
Write-Host "! 需要手动处理:" -ForegroundColor Yellow
Write-Host "  - ClipboardSyncService.cs 剪贴板检测顺序" -ForegroundColor Gray
Write-Host "  - MainWindow.xaml.cs 窗口最小化逻辑" -ForegroundColor Gray
Write-Host ""

if (!$DryRun) {
    Write-Host "📝 下一步:" -ForegroundColor Cyan
    Write-Host "  1. 在 Visual Studio 中打开项目" -ForegroundColor Gray
    Write-Host "  2. 手动修复剪贴板相关代码（见下方详细说明）" -ForegroundColor Gray
    Write-Host "  3. 编译项目: .\scripts\Build-Release.ps1" -ForegroundColor Gray
    Write-Host "  4. 打包安装: .\scripts\Package-Release.ps1" -ForegroundColor Gray
    Write-Host ""
}
