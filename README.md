# LanMouseShare 局域网键鼠共享

两台 Windows 电脑（A / B）连在同一个局域网里，装好之后鼠标滑过屏幕边缘就能接管另一台：键盘、剪贴板、文件传输都跟着走，不需要 KVM 硬件。

当前版本：`0.2.0-beta-fix19`（.NET Framework 4.8 + WPF）

## 项目简介

**解决什么问题**：两台电脑摆在一起办公时，要么换一套键鼠，要么买 KVM 切换器，要么忍受远控软件的延迟。LanMouseShare 让两台机器像一台的多个屏幕那样用——鼠标推出屏幕边缘就接管另一台，键盘、剪贴板、文件跟着当前控制目标走。

**两端怎么分工**：每台电脑跑一个 `Host.Service`（后台服务，负责局域网发现、配对、加密通信、文件流、日志收集）和一个 `Desktop.Agent`（桌面窗口，负责键鼠捕获、跨屏判断、快捷键、把远端输入注入本机）。两者通过本机 IPC 通信，跨机走 UDP/TCP。

**控制权怎么定**：只有 `Idle` / `Owner` / `Target` 三种角色，靠「租约 + 确认」维持——主控端 50ms 级租约、700ms 收不到确认就回空闲；被控端 600ms 租约到期自动松手，且**永远不自封主控**。任一端掉线、崩溃、拔网线，另一端 1 秒内回到空闲，不会两端互锁。

**当前版本能做什么**：跨屏键鼠（四方向 + 边角阻挡 + fix19 的返回手势四道闸）、文本/图片双向剪贴板（fix16/17 已把 OLE 调用全部移出 UI 线程）、文件拖拽/右键直发并落到对端桌面、配对码 + 加密通道、托盘常驻与开机自启、三个卸载入口（fix13）、整套统一图标（fix18）。

## 能做什么

- **跨屏鼠标键盘**：鼠标碰到屏幕物理边缘并继续外推即切换控制目标，支持上下左右四个方向与边角阻挡；控制权用「租约 + 确认」模型，任一端掉线、崩溃或拔网线都会在 1 秒内回到空闲，不会两端同时抢主控。
- **剪贴板共享**：文本与图片双向同步。所有剪贴板 OLE 调用都在一条常驻 STA 线程上执行（fix16 起），不会堵住低级鼠标钩子所在的 UI 线程；写入失败的重试收敛到 3 次。
- **文件传输**：把文件拖进窗口，或在资源管理器里右键「传输到对端」，文件直接落到对方桌面。
- **配对与加密**：配对码 + 加密通道；日志可在界面上关闭推送。
- **桌面集成**：托盘常驻、开机自启、开始菜单/设置/托盘三处可用的卸载入口、统一应用图标（fix18）。

## 目录结构

| 路径 | 内容 |
| --- | --- |
| `src/Desktop.Agent` | 桌面端：窗口、托盘、快捷键、键鼠捕获与注入、控制通道（UDP 47534） |
| `src/Host.Service` | 后台服务：发现、配对、加密通信、文件流、日志收集与推送 |
| `src/Input.Native` | Windows 输入钩子、SendInput、边缘切屏检测 |
| `src/Shared.Core` | 协议、配置、IPC DTO、控制状态机、日志协议 |
| `src/Setup` | 单文件安装器 |
| `scripts` | 编译打包、防火墙、诊断与回退清理脚本 |
| `docs` | 使用说明、状态关系书、修复总结、测试记录 |
| `tests` | Shared.Core 控制逻辑测试 |

`dist/`（本地生成的安装包）与 `bin/`、`obj/` 不进仓库。

## 构建

```powershell
# 便携包：编译 + 收集到 dist\LanMouseShare-<版本>
.\scripts\Quick-Build.ps1 -Version "0.2.0-beta-fix19"

# 单文件安装包（在便携包基础上再打包）
.\scripts\Build-Installer.ps1 -Version "0.2.0-beta-fix19"
```

需要 .NET Framework 4.8 Developer Pack 与 MSBuild（VS 2022 Build Tools 即可），脚本会自动探测 MSBuild 路径。

测试：`tests\Shared.Core.Tests\bin\Release\LanMouseShare.Tests.exe`，输出 `All tests passed.` 即通过。

## 文档

- [使用说明-先看我](docs/使用说明-先看我.md)
- [修复总结（逐版本根因与做法）](docs/FIX-SUMMARY.md)
- [控制状态关系书](docs/CONTROL-STATE-MODEL.md)
- [手动构建指南](docs/MANUAL-BUILD-GUIDE.md) / [手动测试说明](docs/MANUAL_TEST.md)

## 许可证

本项目采用 MIT 许可证，详见 [LICENSE](LICENSE)：可以自由使用、修改、商用，保留版权声明即可。
