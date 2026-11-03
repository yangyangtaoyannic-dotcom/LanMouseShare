# 快速打包脚本（简化版）
# 在 Visual Studio 中编译完成后运行此脚本

$version = "0.2.0-beta"
$root = "C:\Users\yangyang\Desktop\局域网键盘鼠标共享"
$dist = "$root\dist\LanMouseShare-$version"

Write-Host "开始打包 $version ..." -ForegroundColor Cyan

# 创建目录
Write-Host "创建目录..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $dist -Force | Out-Null
New-Item -ItemType Directory -Path "$dist\Agent" -Force | Out-Null
New-Item -ItemType Directory -Path "$dist\Service" -Force | Out-Null
New-Item -ItemType Directory -Path "$dist\Scripts" -Force | Out-Null
New-Item -ItemType Directory -Path "$dist\Docs" -Force | Out-Null

# 复制文件
Write-Host "复制 Agent..." -ForegroundColor Yellow
Copy-Item "$root\src\Desktop.Agent\bin\Release\*" "$dist\Agent\" -Recurse -Force

Write-Host "复制 Service..." -ForegroundColor Yellow
Copy-Item "$root\src\Host.Service\bin\Release\*" "$dist\Service\" -Recurse -Force

Write-Host "复制 Scripts..." -ForegroundColor Yellow
Copy-Item "$root\scripts\*" "$dist\Scripts\" -Recurse -Force

Write-Host "复制 Docs..." -ForegroundColor Yellow
Copy-Item "$root\docs\*" "$dist\Docs\" -Recurse -Force

Write-Host "复制根文档..." -ForegroundColor Yellow
Copy-Item "$root\PROJECT_GUIDE.md" "$dist\" -Force -ErrorAction SilentlyContinue

# 创建版本说明
Write-Host "创建版本说明..." -ForegroundColor Yellow
@"
LanMouseShare $version

新功能：
- 新控制器架构
- 改进的边缘返回检测（90px 阈值 + 1.5s 宽限期）
- 文件传输优化
- 功能开关（UseNewControllers）

安装方法：
1. 阅读 Docs\MANUAL-BUILD-GUIDE.md
2. 停止旧版本的 Agent 和 Service
3. 复制文件到安装目录
4. 启用 UseNewControllers = true
5. 重启 Agent

测试方法：
- Scripts\Test-NewControllers.ps1
- Scripts\Test-EdgeReturn.ps1

构建日期: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
基于版本: 0.1.64

详细文档请查看：
- Docs\USAGE-GUIDE.md - 使用指南
- Docs\MANUAL-BUILD-GUIDE.md - 安装指南
- Docs\CONTROLLER-INTEGRATION.md - 集成说明
"@ | Out-File "$dist\VERSION-NOTES.txt" -Encoding UTF8

Write-Host ""
Write-Host "✅ 打包完成！" -ForegroundColor Green
Write-Host ""
Write-Host "位置: $dist" -ForegroundColor Yellow
Write-Host ""
Write-Host "文件大小:" -ForegroundColor Cyan
$agentExe = Get-Item "$dist\Agent\LanMouseShare.Agent.exe" -ErrorAction SilentlyContinue
$serviceExe = Get-Item "$dist\Service\LanMouseShare.Service.exe" -ErrorAction SilentlyContinue
if ($agentExe) {
    Write-Host "  Agent.exe:   $([math]::Round($agentExe.Length / 1MB, 2)) MB" -ForegroundColor Gray
}
if ($serviceExe) {
    Write-Host "  Service.exe: $([math]::Round($serviceExe.Length / 1MB, 2)) MB" -ForegroundColor Gray
}
Write-Host ""
Write-Host "下一步：阅读 Docs\MANUAL-BUILD-GUIDE.md 了解安装方法" -ForegroundColor Yellow
