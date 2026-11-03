# AGENTS.md · 局域网键盘鼠标共享（LanMouseShare）

> 这台电脑上历史最贵的项目：单会话 2,146 次工具调用、2.8 亿缓存读 token。
> 先读 `PROJECT_GUIDE.md`（版本、状态机、模块职责都在里面），本文件只写「怎么高效改它」。

## 概览
- 版本 `0.2.0-beta-fix24`（AssemblyFileVersion 0.2.1.9），.NET Framework 4.8 + WPF，**不是 .NET Core**（没有 `dotnet build`）。
- 核心：鼠标滑过屏幕边缘接管另一台电脑的键鼠/剪贴板/文件。控制权是「租约 + 确认」状态机（`Idle` / `Owner` / `Target`），被控端不能自封主控。

## 目录与入口
- `src/Desktop.Agent/` 桌面端（WPF）。**`MainWindow.xaml.cs` 4,440 行 / 173KB —— 绝对不要整读**；先 `ops_grep` 找事件名/方法名，再用 `ops_read_files` 带 `offset/limit` 读 60–150 行。
- `src/Host.Service/` 后台服务（发现、配对、网络、文件流、日志）。协议与端口：UDP 47534（Agent 直连），Service 中继通道。
- `src/Input.Native/` 输入钩子、`SendInput`、边缘切屏检测。
- `src/Shared.Core/` 协议、配置、加密、IPC DTO、**控制状态机**、日志协议。
- `src/Setup/` 单文件安装器；`tests/Shared.Core.Tests/` 控制逻辑测试（控制台程序，`Program.cs` 驱动）。
- `docs/` 有 `CONTROL-STATE-MODEL.md`、`FIX-SUMMARY.md`、`MANUAL-BUILD-GUIDE.md`、`MANUAL-TEST.md`、`TEST_RECORD.md` —— 改状态机/修 bug 前先看对应的那份。
- `scripts/` 有 `Quick-Build.ps1`（探测 msbuild 并编译）、`Apply-Fixes.ps1`、`Package-Release.ps1`、`Build-Installer.ps1`、`Install-Service.bat`。

## 构建与测试
- 编译：优先 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Quick-Build.ps1`。
- 直接 msbuild（`Quick-Build.ps1` 会自动按顺序探测，含 `D:\Software\Visual Studio\...`）：
  `MSBuild.exe LanMouseShare.sln /t:Build /p:Configuration=Release /nologo`
- 测试：构建后跑 `tests/Shared.Core.Tests` 产物；`docs/MANUAL-TEST.md` 是人工验收清单。
- 输出在 `dist/`（不进 git）；服务安装/卸载走 `scripts/Install-Service.bat` / `Uninstall-Service.bat`。

## 改代码的规矩
- `edit` 只在「刚读过该文件」时用；任何脚本/shell 侧写入都会让 edit 报 `file changed since it was read`（本项目历史踩了 45 次）。批量改就 write 全量替换。
- 涉及控制权/租约/心跳的改动，必须同步更新 `docs/CONTROL-STATE-MODEL.md`，否则下个会话会按旧模型理解。
- 修完 bug 在 `docs/FIX-SUMMARY.md` 追加一条（版本号 + 症状 + 根因 + 改动点），版本号用 `beta-fixNN` 递增。
- 剪贴板：所有 OLE 调用必须在 UI 线程之外；跨线程读界面开关会直接让进程退出（fix17 的教训）。
- 不要用 `git clean -xdf`（dist/ 与 config 会被清掉）；不要把 `patches/`、`github-upload/` 当源码改。

## 只跑必要的历史包袱
- `scripts/*.exe`（`FileLinkSmoke.exe`、`PipeProbe.exe`）是编译产物，不是源码，别读也别改。
- `dist/` 下是大文件发布包，搜索一律加 `-i` 排除或用 `ops_grep` 的 glob 排除。
- `patches/` 里是历史补丁文本，只在排查回归时翻，不参与构建。
