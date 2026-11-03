# 打包 0.2.0-beta 版本（包含新控制器）
# 使用说明：以管理员身份运行 PowerShell，然后执行此脚本

param(
    [string]$Version = "0.2.0-beta"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "打包 LanMouseShare $Version" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# 1. 查找 MSBuild
Write-Host "[1/5] 查找 MSBuild..." -ForegroundColor Yellow
$msbuild = $null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"

if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if ($vsPath) {
        $msbuild = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
        if (-not (Test-Path $msbuild)) {
            $msbuild = Join-Path $vsPath "MSBuild\15.0\Bin\MSBuild.exe"
        }
    }
}

if (-not $msbuild -or -not (Test-Path $msbuild)) {
    Write-Host "   未找到 Visual Studio MSBuild，尝试使用 .NET Framework MSBuild..." -ForegroundColor Yellow
    $msbuild = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
    if (-not (Test-Path $msbuild)) {
        $msbuild = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
    }
}

if (-not (Test-Path $msbuild)) {
    Write-Host "[错误] 找不到 MSBuild.exe" -ForegroundColor Red
    Write-Host "请安装 Visual Studio 2017 或更高版本，或者 .NET Framework 4.8 SDK" -ForegroundColor Red
    exit 1
}

Write-Host "   找到 MSBuild: $msbuild" -ForegroundColor Green
Write-Host ""

# 2. 编译项目
Write-Host "[2/5] 编译项目..." -ForegroundColor Yellow
$sln = Join-Path $root "LanMouseShare.sln"

& $msbuild $sln /t:Clean /p:Configuration=Release /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "[错误] 清理失败" -ForegroundColor Red
    exit $LASTEXITCODE
}

& $msbuild $sln /t:Build /p:Configuration=Release /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "[错误] 编译失败" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "   编译成功" -ForegroundColor Green
Write-Host ""

# 3. 创建发布目录
Write-Host "[3/5] 创建发布目录..." -ForegroundColor Yellow
$dist = Join-Path $root "dist\LanMouseShare-$Version"

if (Test-Path $dist) {
    Remove-Item -Path $dist -Recurse -Force
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null
foreach ($name in @("Agent", "Service", "Scripts", "Docs")) {
    New-Item -ItemType Directory -Path (Join-Path $dist $name) -Force | Out-Null
}

Write-Host "   目录创建成功: $dist" -ForegroundColor Green
Write-Host ""

# 4. 复制文件
Write-Host "[4/5] 复制文件..." -ForegroundColor Yellow

# Agent
Write-Host "   复制 Agent..." -ForegroundColor Gray
$agentBin = Join-Path $root "src\Desktop.Agent\bin\Release"
Copy-Item -Path (Join-Path $agentBin "LanMouseShare.Agent.exe") -Destination (Join-Path $dist "Agent") -Force
Copy-Item -Path (Join-Path $agentBin "LanMouseShare.Agent.pdb") -Destination (Join-Path $dist "Agent") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $agentBin "LanMouseShare.Core.dll") -Destination (Join-Path $dist "Agent") -Force
Copy-Item -Path (Join-Path $agentBin "LanMouseShare.Core.pdb") -Destination (Join-Path $dist "Agent") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $agentBin "LanMouseShare.Input.dll") -Destination (Join-Path $dist "Agent") -Force
Copy-Item -Path (Join-Path $agentBin "LanMouseShare.Input.pdb") -Destination (Join-Path $dist "Agent") -Force -ErrorAction SilentlyContinue

# Service
Write-Host "   复制 Service..." -ForegroundColor Gray
$serviceBin = Join-Path $root "src\Host.Service\bin\Release"
Copy-Item -Path (Join-Path $serviceBin "LanMouseShare.Service.exe") -Destination (Join-Path $dist "Service") -Force
Copy-Item -Path (Join-Path $serviceBin "LanMouseShare.Service.pdb") -Destination (Join-Path $dist "Service") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $serviceBin "LanMouseShare.Core.dll") -Destination (Join-Path $dist "Service") -Force
Copy-Item -Path (Join-Path $serviceBin "LanMouseShare.Core.pdb") -Destination (Join-Path $dist "Service") -Force -ErrorAction SilentlyContinue

# Scripts
Write-Host "   复制 Scripts..." -ForegroundColor Gray
Copy-Item -Path (Join-Path $root "scripts\*") -Destination (Join-Path $dist "Scripts") -Recurse -Force
foreach ($devScript in @("Test-Releases.ps1", "Upload-Releases.ps1", "Build-Installer.ps1")) {
    $devPath = Join-Path $dist "Scripts\$devScript"
    if (Test-Path $devPath) {
        Remove-Item -LiteralPath $devPath -Force
    }
}

# Docs
Write-Host "   复制 Docs..." -ForegroundColor Gray
Copy-Item -Path (Join-Path $root "docs\*") -Destination (Join-Path $dist "Docs") -Recurse -Force

# 根目录文档
Copy-Item -Path (Join-Path $root "docs\PACKAGE_README.md") -Destination (Join-Path $dist "README.md") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $root "PROJECT_GUIDE.md") -Destination (Join-Path $dist "PROJECT_GUIDE.md") -Force -ErrorAction SilentlyContinue

# 使用说明
$quickStart = Get-ChildItem -Path (Join-Path $root "docs") -File -Filter "*.md" |
    Where-Object { $_.Name -notmatch '^[A-Za-z0-9_.-]+\.md$' } |
    Select-Object -First 1
if ($quickStart) {
    Copy-Item -Path $quickStart.FullName -Destination (Join-Path $dist $quickStart.Name) -Force
}

Write-Host "   文件复制完成" -ForegroundColor Green
Write-Host ""

# 5. 创建版本说明
Write-Host "[5/5] 创建版本说明..." -ForegroundColor Yellow
$versionNotes = @"
# LanMouseShare $Version - 新控制器测试版

## 🆕 新功能

### 核心改进
✅ **新控制器架构** - 重构控制逻辑，职责分离
✅ **改进的边缘返回检测** - 解决 2 秒弹回问题
  - 90px 阈值（旧版 12px）
  - 1.5 秒宽限期
  - 滑动窗口采样
  - 单样本限制 60px
✅ **文件传输优化** - 小文件立即传输，大文件后台队列
✅ **功能开关** - 可在新旧实现之间切换

### 新增控制器
- ControlFlowController - 控制流程总协调器
- EdgeDetectionController - 边缘检测控制器
- RemoteControlController - 远程控制会话管理
- InputForwardingController - 输入转发与注入
- AdaptiveEdgeReturnDetector - 自适应边缘返回检测
- FileTransferManager - 文件传输管理器

## 📋 安装说明

### 方法 A：使用安装脚本（推荐）
1. 解压到任意目录
2. 以管理员身份运行：`Scripts\Install-Agent.bat`
3. 启动 Agent

### 方法 B：手动安装
1. 停止旧版本的 Agent 和 Service
2. 复制 `Agent\*` 到 `C:\Program Files\LanMouseShare\Agent\`
3. 复制 `Service\*` 到 `C:\Program Files\LanMouseShare\Service\`
4. 重启 Service 和 Agent

## ⚙️ 启用新控制器

编辑 `C:\ProgramData\LanMouseShare\config.json`：
``````json
{
  "UseNewControllers": true
}
``````

重启 Agent 生效。

## 🧪 测试

使用提供的测试脚本：
``````powershell
# 完整测试
.\Scripts\Test-NewControllers.ps1 -ControllerHost <IP1> -AgentHost <IP2>

# 边缘返回测试
.\Scripts\Test-EdgeReturn.ps1 -Host <IP>
``````

详细使用说明请查看：`Docs\USAGE-GUIDE.md`

## 🔄 回滚

如果遇到问题，设置 `UseNewControllers: false` 并重启 Agent。

## 📝 版本信息

- **版本**: $Version
- **构建日期**: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
- **基于版本**: 0.1.64

## ⚠️ 注意事项

这是测试版本，建议：
1. 先在非生产环境测试
2. 保留旧版本以便回滚
3. 报告任何问题到项目 GitHub

---
**重要**: 新控制器默认**已启用**，如需使用旧实现，请手动设置 UseNewControllers = false
"@

$versionNotes | Out-File -FilePath (Join-Path $dist "VERSION-NOTES.txt") -Encoding UTF8
Write-Host "   版本说明已创建" -ForegroundColor Green
Write-Host ""

# 完成
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "✅ 打包完成！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "发布包位置: $dist" -ForegroundColor Yellow
Write-Host ""
Write-Host "包含文件:" -ForegroundColor Cyan
Write-Host "  Agent\        - 桌面客户端程序" -ForegroundColor Gray
Write-Host "  Service\      - 后台服务程序" -ForegroundColor Gray
Write-Host "  Scripts\      - 测试和调试脚本" -ForegroundColor Gray
Write-Host "  Docs\         - 完整文档" -ForegroundColor Gray
Write-Host "  VERSION-NOTES.txt - 版本说明" -ForegroundColor Gray
Write-Host ""
Write-Host "下一步:" -ForegroundColor Yellow
Write-Host "1. 将整个文件夹复制到目标机器" -ForegroundColor White
Write-Host "2. 阅读 VERSION-NOTES.txt 了解安装方法" -ForegroundColor White
Write-Host "3. 使用 Scripts\ 中的测试脚本验证功能" -ForegroundColor White
Write-Host ""

# 显示文件大小
$agentSize = (Get-Item (Join-Path $dist "Agent\LanMouseShare.Agent.exe")).Length / 1MB
$serviceSize = (Get-Item (Join-Path $dist "Service\LanMouseShare.Service.exe")).Length / 1MB
Write-Host "文件大小:" -ForegroundColor Cyan
Write-Host "  Agent.exe:   $($agentSize.ToString("F2")) MB" -ForegroundColor Gray
Write-Host "  Service.exe: $($serviceSize.ToString("F2")) MB" -ForegroundColor Gray
