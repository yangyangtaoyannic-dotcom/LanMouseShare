# LanMouseShare 功能梳理与状态审计（0.1.58）

本文件把软件按「功能 → 代码位置 → 当前状态 → 已知限制」逐项列清楚，作为重构与排障的索引。
控制权的正式规范见 `docs\CONTROL-STATE-MODEL.md`。

## 1. 架构总览

| 组件 | 进程 | 职责 |
| --- | --- | --- |
| `LanMouseShare.Agent` | Agent.exe（用户会话，管理员） | 全局键鼠钩子、边缘检测、注入远端输入、剪贴板与文件、界面 |
| `LanMouseShare.Service` | Service.exe（LocalSystem） | 设备发现、TCP 信令、UDP 输入中继、日志收集、防火墙与安装辅助 |
| `LanMouseShare.Core` | 类库 | 协议、配置、控制状态机、日志、系统信息 |
| `LanMouseShare.Input` | 类库 | 低级钩子、注入、边缘检测、DPI 映射 |

数据面（agent ↔ agent，UDP 47534）：输入帧、控制租约、控制确认、剪贴板文件公告。
控制面（agent ↔ 本机 Service ↔ 对端 Service ↔ agent，TCP 47532 + UDP 47533 中继）：配对、控制命令、租约中继、剪贴板清单、文件通知。
日志面（agent → 主控端，TCP 47540）：可关闭，关闭后只留本机。

## 2. 功能矩阵

| 功能 | 代码位置 | 状态 |
| --- | --- | --- |
| 设备发现 / 配对 | `Networking\UdpDiscoveryService.cs`、`PeerCoordinator.PairingRequest/Confirm` | 可用 |
| 角色设置（主控 / 被控） | `AppConfig.ShareRole`、`PeerStateDto.ShareRole`、界面「调试日志」卡片 | 可用 |
| 控制权状态机（Idle/Owner/Target） | `Control\ControlLinkMachine.cs` | 可用；租约 100ms、超时 2500ms |
| 租约与确认双通道 | `DirectInputChannel`（UDP）+ `AgentCommands.SendControlLease(Ack)`（TCP 中继） | 可用 |
| 边缘切屏（主控 → 被控） | `MainWindow.ActivateRemoteControl`、`EdgeSwitchDetector` | 可用；锁定 500ms、离边 12px 重新武装 |
| 被控端返回主控 | `RemoteEdgeReturnDetector` | 0.1.58 修：阈值 90px、≥2 个样本、300ms 窗口、离边 120px 才武装 |
| 被控端取回控制 | `HandleTargetLocalInput` → `ReleaseControl` 请求 | 可用（只请求，不自封主控） |
| 鼠标绝对坐标注入 | `MainWindow.OnRawMouseMoved`（发给远端屏幕坐标）、`InputInjector` | 可用；已按远端分辨率做映射 |
| 键盘注入 | `InputInjector.InjectKey`（ScanCode 优先） | 可用；控制权丢失时补发 KEYUP |
| 输入帧压缩 | `DirectInputChannel` v4 紧凑帧（相对 2B/事件、绝对 4B/事件）、v5 序号 | 可用；丢旧帧防回跳 |
| 输入优先级 | `Networking\TrafficPriority.cs`（DSCP 46 输入 / 8 日志） | 可用；需交换机支持 QoS |
| 心跳与 RTT | `PeerCoordinator.HeartbeatLoop`、`PeerState.NetworkRttMs` | 可用；≥100ms 记 WARN |
| 剪贴板文本 / 图片 | `ClipboardSyncService` | 可用 |
| 剪贴板文件（懒加载 + 请求式粘贴） | `ClipboardSyncService`、`MainWindow.TryHandleClipboardPaste` | 0.1.58 修：Ctrl+V 时重新读取本机剪贴板 |
| 拖拽文件传输 | `FileDropSender`、`PeerCoordinator.BeginIncomingFile` | 可用；落公共桌面 `Mouse传输文件` |
| 文件夹传输（zip） | `FileDropSender.CreateFolderArchive`、`MainWindow.OnPasteFileArrived` | 可用；粘贴时解压为同名目录 |
| 传送取消 | `AgentCommands.CancelTransfers`、界面「取消传输」 | 可用 |
| 日志（本机） | `Diagnostics\AppLog.cs` | 可用；保留 3 天 |
| 日志转发与开关 | `LogHub`/`LogForwarder`/`LogCollector`、界面「推送日志到主控端」 | 可用 |
| 系统信息文件 | `Diagnostics\SystemInfoReport.cs` | 可用；`SystemInfo-<组件>.txt` |
| 热键（暂停 / 停止 / 退出） | `MainWindow.OnInputCaptured` 热键前置分支 | 可用；钩子被吞时仍生效 |
| 开机自启 / 提权 | `StartupTaskManager`、`App.manifest` | 可用 |
| 安装 / 防火墙 | `src\Setup\Program.cs`（sc.exe + netsh） | 可用；开放 47531/47532/47533/47534/47540 |

## 3. 0.1.58 修了什么

1. **鼠标约两秒弹回主控端**：`CheckRemoteReturnToController` 对注入后的单次大位移没有任何抑制，一次快速甩动就能把「返回边」判定打满。现在：
   - 刚拿到控制权的 1.5 秒内不响应返回请求（`TargetReturnGraceMs`）；
   - 单个样本最多贡献 60px（`MaxReturnSamplePixels`）；
   - 累计阈值 90px、至少 2 个样本、300ms 窗口；且必须离开返回边 120px 才「武装」。
2. **切过去马上被弹回（要跨两次）**：同上；另加落点内缩 40px。
3. **跨屏粘贴文件不生效**：Ctrl+V 由主控端拦截后查 `LocalHasFiles`，而该标志来自剪贴板监听的防抖回调，常常还是 false。现在按 V 的瞬间直接 `Clipboard.ContainsFileDropList()` 重新判定。
4. **控制心跳挂在 UI 线程**：已改为独立线程（0.1.57），最小化/读剪贴板不再中断控制权。

## 4. 和 LanCtrl 的差别（为什么它看起来更"跟手"）

LanCtrl 是**屏幕流 + 坐标映射**：它把远端画面拉过来，鼠标事件在本地画面上移动，再按比例换算成远端坐标，主观延迟只取决于画面帧率，且画面本身会"掩盖"注入延迟。

本软件是**系统级输入注入**：主控端的物理鼠标事件 → 边缘判定 → UDP → 被控端 `SendInput` → 系统处理。链路上没有画面缓冲，理论上更跟手，但任何一环卡住都会直接体现在光标上（心跳、钩子线程、注入线程、UDP 丢包）。

因此优化方向不是"映射坐标"（已经做了绝对映射），而是：
- 减少每帧处理的工作量（已做帧压缩、跳过过期相对位移）；
- 保证注入线程不被 UI 阻塞（已做）；
- 控制租约不再把整条链路拖慢（已做独立线程 + 双通道）。

## 5. 坐标映射现状

- 切屏时用 `EdgeTransitionMapper.MapEntryToRemote` 把本地边缘点换算成远端像素坐标；
- 之后每一帧都发**远端绝对坐标**（`InputKinds.MouseMove`），由被控端 `SendInput` 绝对移动落地；
- 混合 DPI 时用 `DpiCoordinateMapper` 把物理像素换算成 DIP 再注入；
- 因此两边分辨率/缩放不同也能一对一映射，不需要额外坐标系。

## 6. 已知限制 / 待办

1. 真实双机稳定性只能由用户物理操作验证（注入型输入会被对端钩子过滤，脚本无法模拟"人"）。
2. 日志转发关闭后，主控端看不到对端日志，只能在被控端本机查。
3. DSCP 标记需要交换机/路由器支持 QoS，普通家用路由器会忽略。
4. Win7 目标机：`GetDpiForMonitor` 不可用，回退到 GDI 比例。
5. 剪贴板文件依赖 `CF_HDROP`；某些程序（如部分压缩软件）复制的是私有格式，不会触发。
