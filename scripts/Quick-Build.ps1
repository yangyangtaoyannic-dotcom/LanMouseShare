# 快速编译和打包脚本 (0.2.0-beta-fix24)
# 修复：MSBuild 探测（含 VS2022 Build Tools / 非默认安装盘）、Service 输出目录名、版本号参数化

param(
    [string]$Version = "0.2.0-beta-fix24",
    [switch]$SkipBuild,
    [switch]$OpenFolder
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path $PSScriptRoot -Parent

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  快速编译和打包 - $Version" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

function Find-MSBuild {
    # 1) vswhere：覆盖任意安装盘符，也能命中 Build Tools
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" 2>$null
        if ($found) {
            foreach ($candidate in @($found)) {
                if ($candidate -and (Test-Path $candidate)) { return $candidate }
            }
        }
    }

    # 2) 常见固定路径兜底
    $msbuildPaths = @(
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "D:\Software\Visual Studio\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
    )

    foreach ($path in $msbuildPaths) {
        if (Test-Path $path) { return $path }
    }

    return $null
}

$msbuild = Find-MSBuild

if (!$msbuild) {
    Write-Host "找不到 MSBuild" -ForegroundColor Red
    Write-Host ""
    Write-Host "请使用以下方式之一编译：" -ForegroundColor Yellow
    Write-Host "1. 打开 Visual Studio，加载 LanMouseShare.sln 后编译" -ForegroundColor Gray
    Write-Host "2. 安装 Visual Studio Build Tools" -ForegroundColor Gray
    Write-Host ""
    exit 1
}

Write-Host "找到 MSBuild: $msbuild" -ForegroundColor Green
Write-Host ""

# 编译项目
if (!$SkipBuild) {
    Write-Host "[1/2] 编译项目..." -ForegroundColor Yellow

    Push-Location $projectRoot
    try {
        & $msbuild "LanMouseShare.sln" /t:Clean /p:Configuration=Release /nologo /v:q
        & $msbuild "LanMouseShare.sln" /t:Build /p:Configuration=Release /nologo /v:m

        if ($LASTEXITCODE -ne 0) {
            throw "编译失败，退出码 $LASTEXITCODE"
        }

        Write-Host "编译完成" -ForegroundColor Green
        Write-Host ""
    }
    catch {
        Write-Host "编译失败: $_" -ForegroundColor Red
        Pop-Location
        exit 1
    }
    finally {
        Pop-Location
    }
} else {
    Write-Host "[1/2] 跳过编译..." -ForegroundColor Gray
    Write-Host ""
}

# 打包
Write-Host "[2/2] 打包安装文件..." -ForegroundColor Yellow

$distDir = Join-Path $projectRoot "dist\LanMouseShare-$Version"
$agentBin = Join-Path $projectRoot "src\Desktop.Agent\bin\Release"
$serviceBin = Join-Path $projectRoot "src\Host.Service\bin\Release"

# 清理旧的打包目录
if (Test-Path $distDir) {
    Remove-Item $distDir -Recurse -Force
}

# 创建目录结构
New-Item -ItemType Directory -Path "$distDir\Agent" -Force | Out-Null
New-Item -ItemType Directory -Path "$distDir\Service" -Force | Out-Null
New-Item -ItemType Directory -Path "$distDir\Scripts" -Force | Out-Null
New-Item -ItemType Directory -Path "$distDir\Docs" -Force | Out-Null

# 复制 Agent 文件（不带 pdb 调试符号，减少安装目录里的多余文件）
if (Test-Path $agentBin) {
    Copy-Item "$agentBin\*.exe","$agentBin\*.dll","$agentBin\*.config" "$distDir\Agent\" -Force -ErrorAction SilentlyContinue
    Write-Host "  Agent 文件已复制" -ForegroundColor Green
} else {
    Write-Host "  Agent 编译输出不存在: $agentBin" -ForegroundColor Red
}

# 复制 Service 文件（同样不带 pdb）
if (Test-Path $serviceBin) {
    Copy-Item "$serviceBin\*.exe","$serviceBin\*.dll","$serviceBin\*.config" "$distDir\Service\" -Force -ErrorAction SilentlyContinue
    Write-Host "  Service 文件已复制" -ForegroundColor Green
} else {
    Write-Host "  Service 编译输出不存在: $serviceBin" -ForegroundColor Red
}

# 复制测试脚本
$scripts = @(
    "Test-NewControllers.ps1",
    "Test-EdgeReturn.ps1",
    "Debug-Remote.ps1"
)

foreach ($script in $scripts) {
    $source = Join-Path $projectRoot "scripts\$script"
    if (Test-Path $source) {
        Copy-Item $source "$distDir\Scripts\" -Force
    }
}
Write-Host "  测试脚本已复制" -ForegroundColor Green

# 复制文档
$docs = @(
    "MANUAL-FIX-GUIDE.md",
    "FIX-SUMMARY.md",
    "USAGE-GUIDE.md",
    "MANUAL-TEST.md",
    "MANUAL-BUILD-GUIDE.md",
    "PACKAGE_README.md"
)

foreach ($doc in $docs) {
    $source = Join-Path $projectRoot "docs\$doc"
    if (Test-Path $source) {
        Copy-Item $source "$distDir\Docs\" -Force
    }
}
Write-Host "  文档已复制" -ForegroundColor Green

# 创建版本说明
$versionNotes = @"
LanMouseShare $Version
========================================

包内结构：
  LanMouseShareSetup.exe   单文件安装程序（推荐）
  Agent\                   桌面客户端（便携）
  Service\                 后台服务（便携）
  Scripts\                 测试脚本
  Docs\                    文档
  source\                  本版本的完整源码
  LanMouseShare-$Version-source.zip  源码压缩包

修复内容：
1. fix24：跨屏来回手感对称——从对端回到本机不再需要「先推 140 像素、停在边缘 260 毫秒」，指针越过边缘就立刻回来；同时把进场落点从贴着返回边的 8 像素挪到 40 像素，快速扫过不会再误触
2. fix23：跨屏提示两边都能看到——控制权从对端回到本机时本机也会出现水波纹；同时加了 600 毫秒去抖，来回跨屏只出现一圈，不会重叠
3. fix22：跨屏提示不再在主控端边缘多画一圈波纹；文件传输的接收目录可自定义（「文件传输」里「更改接收目录…」，不可写时自动退回桌面）
4. fix21：跨屏提示改成水波纹并跟着鼠标走——出现即全亮、贴住指针扩散，约 1 秒后消失；控制权从对端回到本机时也会出现，来回跨屏都能一眼看到指针落在哪
5. fix20：跨屏切到另一台后，被控端会在鼠标落点画一圈向外扩散的黄色波纹（约 0.6 秒后自动消失），一眼就能看到指针落在哪；「输入设置」里新增「跨屏后高亮鼠标落点」勾选框，随时可以关掉，勾选状态随配置保存
6. fix19：修复“跨到对端后点右键，右键菜单弹在本机”——在对端左缘附近快速左扫（几十毫秒扫过 200 多像素）时，返回手势被误判成“要回本机”，控制权在跨屏后 0.4~2 秒就被收回，之后所有点击都落在本机。现在返回手势要求：先真正进到对端 40 像素以上、在返回边连续驻留 260 毫秒、累计推力 140 像素，回切后还有 700 毫秒冷却，快速扫一下不会再被收回；真想回本机时压住边缘继续推即可
7. fix18：整套界面图标统一——新增多尺寸应用图标（16/32/48/256），任务栏、窗口左上角、系统托盘、开始菜单与桌面快捷方式、安装包与卸载项现在都显示同一枚图标，不再是空白/系统默认图标
8. fix17：修复"一复制文本，复制那一端的 Agent 直接退出"——fix16 把剪贴板搬到专用线程后，读剪贴板前的开关判断仍去读 WPF 复选框（依赖属性只能 UI 线程读），在剪贴板线程上抛异常把整个进程带走；现在开关状态改为线程安全镜像，剪贴板任务出错也只记日志不再连坐程序
9. fix16：跨屏复制到剪贴板不再卡鼠标——文本、图片（含 PNG 编码）与文件列表的剪贴板读写全部从 UI 线程搬到一条常驻 STA 线程，低级鼠标钩子不再被剪贴板的 OLE 往返堵住；写入失败的重试也从 5 次收敛到 3 次，不会再多叠加数秒
10. fix16：剪贴板操作新增耗时日志（clipboard.io 与 clipboard.remote.applied 带 cost=毫秒），以后再卡能直接看出时间花在哪一段
11. fix15：跨屏边角阻挡恢复有效——贴着对端屏幕的上下角往回推，不会再直接从角落穿回来（返回路径上的边角检查重新加回来了）
12. fix15：边角阻挡带改成按屏幕比例计算（约屏幕高度/宽度的 7.5%，最小 80 像素），高分辨率那一端不再形同虚设——以前只有低分辨率那台机器上才感觉得到阻挡
13. fix14：跨屏鼠标逻辑回退到 0.2.0-beta-fix1 的手感——去掉“进入对端后必须先往里走 80 像素才开始算返回推挤”这个前置条件，去掉“对端交还控制权后要离开边缘 140 像素、冷却 1.2 秒才能再次跨过去”的限制，返回路径也不再受边角阻挡影响。之前这些限制会造成靠近边缘的一小片区域“怎么都跨不过去”
14. fix14：键盘防误触修复保留——键盘事件不会被当成“指针在屏幕左上角”而误把控制权交给对端（fix10 这条修复仍在）
15. fix14：鼠标侧键（前进/后退）转发保持可用（fix12），不受本次回退影响
16. fix13：新增完整卸载功能——开始菜单「卸载 LAN Mouse Share」（原有）、Windows 设置→应用列表里的卸载项、软件窗口「卸载」按钮与托盘菜单「卸载 LAN Mouse Share…」三处入口都能卸载
17. fix13：卸载时弹窗询问是否连配置、日志与配对记录一起删除（默认只删程序）；卸载会顺手清掉资源管理器右键菜单、防火墙规则、开机自启与快捷方式，结束时给出结果提示
18. fix12：鼠标侧键（前进/后退）跨屏后不再失效——侧键按下/抬起与“哪一颗”都按原样转发到被控端，释放控制权时也会补发侧键抬起，避免卡住
19. fix11：传输速度提升——读写块从 1MB 放到 4MB，socket 收发缓冲 1MB，落盘改成 1MB 顺序缓冲（大文件吞吐更稳、更快）
20. fix11：传快捷方式不再把快捷方式指向的程序本体传过去——快捷方式单独注册右键菜单项
21. fix11：接收到的文件直接放在桌面本身，不再套 Mouse传输文件 子目录
22. fix10：传文件改成“资源管理器右键 → 传输到对端”——选中文件或文件夹，右键点“传输到对端”，文件直接传到对方电脑桌面，不再需要先复制再粘贴
23. fix10：彻底关闭“复制文件 → 对端粘贴”这条链路（复制文件不再公告、不再写虚拟剪贴板，避免粘贴时毫无反应）
24. fix10：修复键盘方向反了——键盘事件不再被当成“指针在屏幕左上角”而误把控制权交给对端；控制权反复易手的问题一并修掉
25. fix9：进度条不再卡在 100%，取消传输真的会中断并清理 .part，打开接收文件夹真的会打开；支持文件夹（自动打 zip 传过去再解回）
26. fix8 的 Agent↔Agent 独立通道（TCP 47541）与 fix7 的全部修复保留
27. 版本号更新为 0.2.0-beta-fix24

安装方法（推荐）：
========================================

双击 LanMouseShareSetup.exe，按提示完成安装（需要管理员权限）。
安装完成后：
  - 服务：LanMouseShare（安装器首次安装后自动启动）
  - 客户端：C:\Program Files\LanMouseShare\Agent\LanMouseShare.Agent.exe

手动覆盖安装（便携方式）：
========================================

2. 停止旧版本
   - taskkill /F /IM LanMouseShare.Agent.exe
   - net stop LanMouseShare

3. 备份（推荐）
   - 将 "C:\Program Files\LanMouseShare" 重命名为 "LanMouseShare-backup"

4. 安装新版本
   - 复制 Agent\ 到 "C:\Program Files\LanMouseShare\Agent\"
   - 复制 Service\ 到 "C:\Program Files\LanMouseShare\Service\"

5. 启动服务
   - net start LanMouseShare
   - 运行 "C:\Program Files\LanMouseShare\Agent\LanMouseShare.Agent.exe"

测试方法：
========================================

运行测试脚本：
  cd Scripts
  .\Test-NewControllers.ps1 -ControllerHost <IP1> -AgentHost <IP2>

详细文档请查看 Docs\ 目录

版本: $Version
日期: $(Get-Date -Format "yyyy-MM-dd")
"@

Set-Content "$distDir\README.txt" -Value $versionNotes -Encoding UTF8

Write-Host "  版本说明已创建" -ForegroundColor Green
Write-Host ""

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  打包完成！" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "输出目录: $distDir" -ForegroundColor Green
Write-Host ""
Write-Host "文件清单:" -ForegroundColor Yellow
Write-Host "  Agent\         - 桌面客户端" -ForegroundColor Gray
Write-Host "  Service\       - 后台服务" -ForegroundColor Gray
Write-Host "  Scripts\       - 测试脚本" -ForegroundColor Gray
Write-Host "  Docs\          - 文档" -ForegroundColor Gray
Write-Host "  README.txt     - 安装说明" -ForegroundColor Gray
Write-Host "  source\        - 完整源码（Build-Installer 生成）" -ForegroundColor Gray
Write-Host ""

if ($OpenFolder) {
    explorer $distDir
}

Write-Host "下一步:" -ForegroundColor Cyan
Write-Host "  1. 运行 .\scripts\Build-Installer.ps1 -Version $Version 生成单文件安装 exe" -ForegroundColor Gray
Write-Host "  2. 复制整个文件夹到目标机器" -ForegroundColor Gray
Write-Host ""
