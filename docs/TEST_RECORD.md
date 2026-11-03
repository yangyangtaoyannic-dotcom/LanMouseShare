# 测试与变更记录

每次做完一轮测试，在这里追加一条记录：改了什么、怎么验证的、结果如何、还剩什么问题。
格式固定，方便日后对照版本和现场日志。

---

## 2026-10-19 · 0.1.43 · 调试日志（主控端/被控端 + 日志实时汇总）

### 这一轮改了什么

- 新增角色字段 `ShareRole`：`Controller`（主控端）/ `Agent`（被控端），存于 `%ProgramData%\LanMouseShare\config.json`。
- 新增日志网络传输：被控端把 `live` 目录的日志实时推到主控端 TCP `47540`，主控端按 `设备名_设备ID前8位` 分目录落盘。
- 日志目录重构：本机日志统一写 `%ProgramData%\LanMouseShare\Logs\live`，汇总日志写 `remote\`，保留 3 天（可改 1~30 天），启动时和每 6 小时清理。
- 日志行格式加入结构化字段：`role=`、`ev=`、`details=`，异常追加 `exception=类型: 消息` 和堆栈，便于按事件名过滤。
- 新增开关：`VerboseLogging`（详细日志）、`DebugToken`（可选口令，两端一致才接收）。
- Agent 界面新增“调试日志”区域：角色选择、主控端地址、调试口令、保留天数、详细日志开关、日志状态、打开本机日志、打开汇总日志。
- 版本号从 0.1.42 升到 0.1.43，新增 `BuildInfo.Version` 作为单一版本来源，并加测试保证 Agent、安装器、打包脚本、日志头一致。
- 安装器与防火墙脚本补上 TCP `47540`。

### 改动的文件

```text
src\Shared.Core\BuildInfo.cs                      新增，统一版本号
src\Shared.Core\Diagnostics\AppLog.cs             重写：live/remote 目录、3 天清理、结构化行、Debug/Event
src\Shared.Core\Diagnostics\LogProtocol.cs        新增：HELLO/OK/DENY 握手、#PING、#ORIGIN
src\Shared.Core\Configuration\AppConfig.cs        新增 5 个字段 + ShareRoleValues
src\Shared.Core\Configuration\ConfigStore.cs      默认值与归一化
src\Shared.Core\Ipc\AgentSettingsDto.cs           新增 5 个字段
src\Shared.Core\Ipc\PeerStateDto.cs               新增 9 个字段（角色、状态、日志目录）
src\Host.Service\Diagnostics\LogCollector.cs      新增：主控端监听、校验口令、分目录落盘
src\Host.Service\Diagnostics\LogForwarder.cs      新增：被控端 tail live 目录并推送
src\Host.Service\Diagnostics\LogHub.cs            新增：按角色启停收集器/推送器
src\Host.Service\PeerCoordinator.cs               接线 LogHub、设置保存、状态输出
src\Desktop.Agent\MainWindow.xaml                 新增“调试日志”区域
src\Desktop.Agent\MainWindow.xaml.cs              读写新设置、打开日志目录
src\Setup\Program.cs                              防火墙放行 47540
scripts\Configure-Firewall.bat                    新增 47540 规则
tests\Shared.Core.Tests\Program.cs                新增 4 组测试
docs\*、PROJECT_GUIDE.md                          补充调试日志说明和手动测试步骤
```

### 怎么验证的

构建：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe LanMouseShare.sln /t:Build /p:Configuration=Release`
结果：6 个工程全部通过，无警告。

自动测试：`tests\Shared.Core.Tests\bin\Release\LanMouseShare.Tests.exe`
结果：`All tests passed.`（共 24 组用例，含新增的 `ShareRoleAndDebugSettingsWork`、`LogProtocolHelpersWork`、`DebugLogUiExists`、`BuildVersionIsConsistent`）。

回环冒烟（单机，服务以 `--console` 启动，模拟两端）：

| 场景 | 操作 | 结果 |
| --- | --- | --- |
| 主控端接收 | 角色 Controller，模拟被控端发 HELLO | 回复 `OK controller=SMOKE-AGENT` |
| 汇总落盘 | 被控端发一行 `ev=smoke.marker` | 写入 `remote\SMOKE_AGENT_smoke-ag\*.log`，可按关键字检索到 |
| 口令校验 | 错误口令发 HELLO | 回复 `DENY reason=bad-token`，主控端继续服务 |
| 被控端推送 | 角色 Agent，地址 `127.0.0.1:47540`，本地起监听 | 收到 HELLO（含 `version=0.1.43`），随后连续收到日志行 |
| 3 天保留 | 造一个 10 天前的 `live` 日志再启动服务 | 旧文件被删除，当天新文件保留 |

补充说明：冒烟测试会临时改写 `config.json`，脚本已在 `finally` 中还原。

### 还没做 / 已知限制

- 跨真实双机的钩子、注入、边缘切屏仍需按 `docs\MANUAL_TEST.md` 手动验证；本轮只在单机回环验证了日志链路。
- 汇总日志按设备分目录，但同一被控端重装后设备 ID 会变，会新开目录，属预期行为。
- 日志传输是明文 TCP，口令只做简单校验，仅用于局域网调试，不是安全通道。
- 安装器还没有“一键打包日志”的按钮，目前靠界面按钮打开两个目录后手动打包。

---

## 2026-10-19 · 0.1.43 · 打包（发布目录 + 安装器）

### 这一轮改了什么

- 重新生成 `dist\LanMouseShare-0.1.43\`，目录结构和 0.1.42 保持一致：`Agent\`、`Service\`、`Scripts\`、`Docs\`、`README.md`、`PROJECT_GUIDE.md`、`使用说明-先看我.md`、`LanMouseShareSetup.exe`、`LanMouseShare-0.1.43-source.zip`。
- 新增 `docs\使用说明-先看我.md`（0.1.43 版简明说明，含调试日志一节），打包时复制到发布目录根。
- 打包脚本不再把几十个文件逐个 `/resource` 塞进 Setup.exe，改成先把 `Agent\ Service\ Scripts\ Docs\` 和根目录 md 压成一个 `payload.zip`，只嵌入这一个资源，安装器用 `System.IO.Compression` 解压安装。
- 打包脚本不再把开发用的 `Test-Releases.ps1`、`Upload-Releases.ps1` 复制进发布包的 `Scripts\`。

### 为什么要改资源方式

第一版按老办法逐个嵌入 27 个文件，Setup.exe 生成后 2 秒内就被火绒安全软件删掉（`C:\ProgramData\Huorong\Sysdiag\Quarantine` 在构建对应时间被写入，Windows Defender 实时防护当时是关闭的）。逐项排查结论：无资源、单个 exe 资源、8 个 exe 资源、带 `requireAdministrator` 清单都能存活，只有 27 个资源 / 853KB 那一版被删，判定是“自解压式多可执行文件载荷”启发式命中。改成单个 zip 后体积从 853KB 降到 285KB，构建后文件不再被删。

### 改动的文件

```text
src\Setup\Program.cs           ExtractPayload 优先读 Root.payload.zip，保留旧的逐资源分支
src\Setup\Setup.csproj         增加 System.IO.Compression / FileSystem 引用
scripts\Build-Installer.ps1    用临时目录压缩 payload.zip 后只嵌入一个资源
docs\使用说明-先看我.md         新增
docs\TEST_RECORD.md            本条记录
```

### 怎么验证的

- 构建：`MSBuild.exe LanMouseShare.sln /t:Build /p:Configuration=Release` → 6 个工程通过。
- 打包：`scripts\Build-Installer.ps1` → 输出 `LanMouseShareSetup.exe`（285,184 字节）和 `LanMouseShare-0.1.43-source.zip`（124,870 字节），等待 4 秒后文件仍在。
- 载荷校验：把 Setup.exe 复制到临时目录，反射读出唯一资源 `Root.payload.zip`（269,424 字节），解压后确认 27 个条目齐全，含 `Agent\`、`Service\`、`Scripts\`、`Docs\` 和 3 个根目录 md，中文文件名正常。
- 源码包校验：92 个条目，含新增的 `Diagnostics\*.cs`、`使用说明-先看我.md`，不含 `bin`、`obj`、`dist`。

### 还没做 / 已知限制

- 没有实际运行安装器（会装服务、改防火墙、需要 UAC 提权），装完后的服务启动、防火墙规则、卸载流程仍需手动跑一遍。
- 安装器依赖 .NET Framework 4.8 自带的压缩库，Win7 SP1 上同样可用。

## 2026-10-20 · 0.1.44 · 修复双主控互锁 + 四类排查日志 + 系统信息文件

### 这一轮改了什么

- 修掉现场出现的“两端键鼠全死”：被控端在光标退回屏幕边缘时不再把自己设成主控端（去掉 `SetPeerTarget()` 与 `SendControlTarget(..., "RemoteReturn")`），改为 `SetLocalTarget()` 并且只通知服务释放控制。
- 边缘切换加冷却锁存：切换或退回后必须离开边缘 12px 且满 500ms 才重新武装（`_edgeSwitchArmed`、`EdgeSwitchCooldownMs`、`ArmEdgeSwitchIfMovedAway`）。
- `EdgeTransitionMapper.EdgeInset` 由 2 改为 8，退回光标不再落回触发带；测试断言同步更新。
- 服务端把“自己仍是目标、却收到对端把目标改回本机”的情况记为 `control.link.arbitrated` WARN（含对端 ID、peerReason 和提示），便于下次现场直接定位；真正修掉互锁的是退回路径本身的三处改动。
- Agent 启动及收到状态后调用 `AppLog.SetRole`，日志不再出现 `role=Unknown`。
- 新增日志：切换屏幕（`screen.switch.out`/`screen.switch.return`/`screen.switch.back`/`edge.switch.arm`/`edge.switch.reset`/`edge.switch.blocked`）、鼠标位置（`mouse.local.position`/`mouse.remote.position`/`mouse.screen.change`）、网络延时（新增 `HeartbeatPayload`，每 5 秒一次，`net.rtt` 记录往返毫秒与结果）、剪贴板（`clipboard.send`/`clipboard.apply`/`clipboard.image.ignored`/`clipboard.failed`）。
- 新增 `SystemInfoReport`：启动写 `Logs\SystemInfo-Service.txt` 与 `Logs\SystemInfo-Agent.txt`（系统版本、CPU/内存、显示器分辨率与 DPI、网卡 MAC/IPv4/IPv6/网关、进程位数、47532/47540 监听状态、角色与主控端地址，不含密钥）；被控端以 `#SYSINFO` 块推给主控端，主控端落到 `remote\<设备>\SystemInfo-Agent.txt`。
- 降噪：被丢弃的远端输入改为 2 秒汇总一次 WARN；界面线程停顿超过 800ms 才记 `ui.stall`。
- 版本号 0.1.43 → 0.1.44（`BuildInfo.cs`、Agent、安装器、打包脚本、测试断言）。

### 改动的文件

```text
src\Shared.Core\Diagnostics\SystemInfoReport.cs   新增：系统信息采集与落盘
src\Shared.Core\Protocol\HeartbeatPayload.cs      新增：心跳负载
src\Shared.Core\Diagnostics\LogProtocol.cs        新增 #SYSINFO-BEGIN / #SYSINFO / #SYSINFO-END 与 #SYSINFO-END
src\Shared.Core\Diagnostics\AppLog.cs             版本跟随 BuildInfo
src\Shared.Core\BuildInfo.cs                      版本 0.1.44
src\Input.Native\EdgeTransitionMapper.cs          EdgeInset 2 → 8
src\Desktop.Agent\MainWindow.xaml.cs              退回改本地目标、冷却锁存、位置/切换/剪贴板/卡顿日志
src\Desktop.Agent\ClipboardSyncService.cs         剪贴板收发成功与失败日志
src\Host.Service\PeerCoordinator.cs               心跳 RTT、单主控仲裁、日志收集启动
src\Host.Service\Diagnostics\LogForwarder.cs      推送系统信息块
src\Host.Service\Diagnostics\LogCollector.cs      接收系统信息块并写 SystemInfo-<role>.txt
src\Host.Service\Program.cs                       Service 启动写系统信息文件
src\Desktop.Agent\App.xaml.cs                     Agent 启动写系统信息文件
src\Setup\Program.cs                             安装器版本 0.1.44
scripts\Build-Installer.ps1                       打包版本 0.1.44
tests\Shared.Core.Tests\Program.cs                新增断言并更新版本/EdgeInset 期望值
```

### 怎么验证的

- 构建：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe LanMouseShare.sln /t:Build /p:Configuration=Release` → 6 个工程 0 error。
- 自动测试：仓库根目录运行 `tests\Shared.Core.Tests\bin\Release\LanMouseShare.Tests.exe` → `All tests passed.`
- 冒烟（主控端）：本机临时切成 Controller，用 TCP 客户端发 `HELLO proto=1 ... token=smoke-token version=0.1.44` 收到 `OK controller=SMOKE-AGENT`，随后发一行 `ev=smoke.marker` 可在 `remote\SMOKE-AGENT_<id8>\20261020-083332_Agent.log` 检索到；同一连接推送 `#SYSINFO` 块后，主控端生成 `SystemInfo-Agent.txt`（日志记 `log.collector.sysinfo lines=3`）；错误 token 收到 `DENY reason=bad-token`。
- 冒烟（被控端）：本机临时切成 Agent 指向假主控端，握手为 `HELLO proto=1 id=4afc2588... name=DESKTOP-ASR9L4K role=Agent token=smoke-token version=0.1.44`，随后收到 84 行，含 `#SYSINFO-BEGIN file=SystemInfo-Agent.txt`、`OSVersion: Microsoft Windows 10 Pro` 与普通日志行。
- 冒烟（保留策略）：在 `live` 放一份 10 天前的日志，启动 Service 5 秒后被删除，当天文件保留。
- 冒烟后已把 `config.json` 还原、删除测试目录与伪造的 `SystemInfo-Agent.txt`。

- 打包：`scripts\Build-Installer.ps1` → `dist\LanMouseShare-0.1.44\LanMouseShareSetup.exe`（325,632 字节，等待 4 秒仍未被杀软拦截）与 `LanMouseShare-0.1.44-source.zip`（约 141 KB，94 个条目，含 `SystemInfoReport.cs`、`HeartbeatPayload.cs`、`TEST_RECORD.md`，不含 bin/obj）；反射确认安装器唯一资源 `Root.payload.zip`（309,629 字节）含 27 个条目（Agent/Service/Scripts/Docs + 3 个根 md，中文文件名正常）。

### 还没做 / 已知限制

- 双机真实按键/注入/边缘切屏仍需按 `docs\MANUAL_TEST.md` 的 0.1.44 清单手动跑一遍（本机只有一台，无法验证两端互锁是否真正消失）。
- 心跳 RTT 只统计 TCP 控制通道，UDP 直连输入通道仍无确认，丢包只能从 `input.ignored` 与延时日志间接判断。
- 系统信息文件里的网卡信息在 Win7 上可能缺少部分字段（.NET Framework 4.8 的 `NetworkInterface` 限制），不影响日志推送。

## 2026-10-20 · 0.1.45 · 控制层按状态关系书重构（租约 + 确认）

### 这一轮改了什么

- 先写规范：`docs\CONTROL-STATE-MODEL.md`，定义 `Idle` / `Owner` / `Target` 三态、租约（50 ms 一发、600 ms 过期、确认 700 ms 超时）、`epoch` 递增与两端同时申请的稳定裁决、释放保护窗口，以及“被控端永不自封主控”的硬约束。
- 按规范实现 `src\Shared.Core\Control\ControlLinkMachine.cs`：唯一控制权状态机，负责 `BeginOwner` / `BeginTarget` / `ApplyLease` / `ApplyAck` / `RequestRelease` / `ApplyRelease` / `Tick` / `ArbitrateAgainst`，超时自动回 `Idle` 并给出释放原因（`AckTimeout`、`LeaseExpired`、`OwnerStopped`、`TargetRequestedRelease`、`EpochConflict`、`Manual`）。
- UDP 直连通道升级为 v3 帧：`magic(4) + version=3(1) + type(1)`，`type=0` 输入批次（旧 v2 仍接受）、`type=1` 租约、`type=2` ack；新增 `src\Shared.Core\Control\ControlChannelCodec.cs` 负责编解码，老的输入路径行为不变。
- `src\Desktop.Agent\MainWindow.xaml.cs` 全面改为“状态机唯一权威”：删掉 `_remoteControlActive` / `_controlledByPeer` / `_remoteReturnReleasePending` 这套分散布尔值，输入路由、注入判定、控制目标显示全部读 `_link.Role`；`ApplyPeerControlTarget` 降级为纯显示镜像，不再翻转任何控制状态。
- 释放流程改为“先发请求、由主控端决定落点”：被控端退回只发 `ControlRelease(远端坐标/方向)`，主控端在保护窗口后把光标放回本地边缘；`ReleaseControl(false)` 也会通知服务，不再出现服务端不知道已释放的状态。
- 日志新增 `control.link.role` / `control.link.begin` / `control.link.released` / `control.link.lease` / `control.link.ack.lost` / `control.link.lease.stale` / `control.link.timeout` / `control.link.arbitrated` / `control.link.stale` / `control.link.stale.ignored` / `control.link.ack.sent`。
- 版本号统一升到 `0.1.45`（`BuildInfo.cs`、Agent 窗口两处、Setup、打包脚本、测试断言）。

### 改动的文件

- `docs\CONTROL-STATE-MODEL.md`（新建）
- `src\Shared.Core\Control\ControlLinkMachine.cs`（新建）
- `src\Shared.Core\Shared.Core.csproj`（加入状态机与心跳载荷）
- `src\Shared.Core\Control\ControlChannelCodec.cs`（新建）
- `src\Desktop.Agent\DirectInputChannel.cs`（v3 帧、租约与 ack 收发）
- `src\Desktop.Agent\MainWindow.xaml.cs`（状态机接线、删除分散布尔值、释放流程、日志）
- `tests\Shared.Core.Tests\Program.cs`（新增 `ControlLinkMachineWorks`、`ControlChannelCodecWorks`）
- 文档：`PROJECT_GUIDE.md` 两份、`docs\MANUAL_TEST.md`、`docs\PACKAGE_README.md`、`docs\使用说明-先看我.md`、`src\Shared.Core\BuildInfo.cs`、`src\Setup\Program.cs`、`scripts\Build-Installer.ps1`

### 怎么验证的

- 构建：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe LanMouseShare.sln /t:Build /p:Configuration=Release` → 6 工程 0 error。
- 自动测试：仓库根目录运行 `tests\Shared.Core.Tests\bin\Release\LanMouseShare.Tests.exe` → `All tests passed.`（新增两组：状态机迁移/租约超时自动释放/两端同时申请裁决/被控端自封主控被拒；v3 帧编解码往返与 v2 兼容）。
- 静态核对：`MainWindow.xaml.cs` 里 `_controlledByPeer` / `_remoteControlActive` / `_remoteReturnReleasePending` 已彻底删除，输入路由、注入判定、边沿返回、界面文本全部只读 `_link.Role`（`grep` 结果为 0）；被控端在本机边缘退回时改为 `Release(TargetRequestedRelease)` 立即停止注入并请求主控端放回光标，主控端若没收到请求会在 700 ms 后按 `AckTimeout` 自行回收（新增测试断言覆盖这条链路）。
- 打包：`scripts\Build-Installer.ps1`（`$Version = "0.1.45"`）产出 `dist\LanMouseShare-0.1.45\LanMouseShareSetup.exe`（单资源 `Root.payload.zip`，28 个条目，含 `Docs\CONTROL-STATE-MODEL.md`）与 `LanMouseShare-0.1.45-source.zip`（97 个条目，含状态机与编解码源码），等待数秒未被杀毒软件拦截。

### 还没做 / 已知限制

- 双机真实场景仍需按 `docs\MANUAL_TEST.md` 的 `## Version 0.1.45 control-model checks` 14 步手动验证（本机只有一台，无法端到端验证拔网线、两端同时切屏等场景）。
- 租约/ack 走 UDP，极端丢包时会靠 600/700 ms 超时回空闲，表现为短暂失去远端控制而不是卡死；重连后需重新推边缘切屏。
- 升降级混用：0.1.45 的 Agent 与 0.1.44 的 Agent 不能互通租约，必须两台电脑同时升级。
- 收尾修正：文档中遗留的 control.conflict.release 全部改为实际事件名 control.link.arbitrated（MANUAL_TEST.md / 本记录），并重新打包 dist\\LanMouseShare-0.1.45\\LanMouseShareSetup.exe（354,816 字节）。

---

## 2026-10-20 · 0.1.46 · 租约/确认补中继通道（修复“完全切不过去”）

### 这一轮改了什么

- 现象：升级 0.1.45 后用户报告“完全切换不了屏幕控制”。本机日志 `live\Agent_*.log` 与对端 `remote\PC-20180425ZHAZ_7de48092\*.log` 显示本机反复 `control.link.timeout reason=AckTimeout`，对端从未出现 `control.link.role to=Target`；本机 `control.lease.send.fail path=udp hasPeer=1` 说明租约在本机发出去了，但对端始终没收到——即 Agent 之间的 UDP 47534 直连被防火墙/安全软件挡住，而 Service 通道（输入、心跳、日志推送）一直是好的。
- 根因：0.1.45 把租约/ack 只放在 UDP 47534 直连上，这条路径一旦被挡，主控端 700 ms 拿不到确认就回到 `Idle`，于是每次推边缘都会“刚切进就退出”，表现为完全无法控制，但本机键鼠不会卡死。
- 修复：给租约/确认加一条与直连**语义相同**的中继通道——`AgentCommands.SendControlLease` / `SendControlLeaseAck`（新增）→ 本机 Service → 对端 Service（复用已配对的 TCP 47532 / UDP 47533）→ 对端 Agent 的 `PollEvents`。UDP 直连保留为快路径，两条路的重复/迟到消息都按 epoch 幂等丢弃。
- 新协议载荷：`ControlLeasePayload`、`ControlLeaseAckPayload`（`src\Shared.Core\Protocol\ControlPayload.cs`），消息类型 `ProtocolMessageTypes.ControlLease` / `ControlLeaseAck`。
- Agent 侧把租约/ack 的发送与接收抽成 `SendLeaseNow` / `SendAckNow` / `ApplyIncomingLease` / `HandleRelayedLease` / `HandleRelayedAck`，`OnControlLeaseReceived` 与 `ApplyEnvelope` 的中继分支共用同一套状态机调用；事件轮询间隔由 100 ms 收紧到 50 ms，保证“中继 + 轮询”的最坏一个来回仍在 700 ms 的 ack 预算内。
- Service 侧：`IsNoisyCommand` / `IsNoisyEnvelope` / `IsCrossScreenEnvelope` 加入租约/确认，避免 10 次/秒的日志刷屏，并让暂停语义对租约同样生效。
- 版本号统一升到 `0.1.46`（`BuildInfo.cs`、Agent 窗口两处、Setup、打包脚本、测试断言）。

### 改动的文件

- `src\Shared.Core\Protocol\ControlPayload.cs`（新增 `ControlLeasePayload`、`ControlLeaseAckPayload`）
- `src\Shared.Core\Protocol\ProtocolEnvelope.cs`（新增 `ControlLease`、`ControlLeaseAck`）
- `src\Shared.Core\Ipc\AgentCommand.cs`（新增 `SendControlLease`、`SendControlLeaseAck`）
- `src\Host.Service\PeerCoordinator.cs`（命令映射、跨屏/噪音集合、`QueueEvent` 中继）
- `src\Host.Service\NamedPipeCommandServer.cs`（`IsNoisyCommand` 降噪）
- `src\Desktop.Agent\MainWindow.xaml.cs`（`SendLeaseNow`/`SendAckNow`/`ApplyIncomingLease`/`HandleRelayedLease`/`HandleRelayedAck`，事件轮询 50 ms）
- `tests\Shared.Core.Tests\Program.cs`（新增 `ControlLeaseRelayPayloadWorks`）
- 文档：`docs\CONTROL-STATE-MODEL.md`、`PROJECT_GUIDE.md` 两份、`docs\MANUAL_TEST.md`、`docs\PACKAGE_README.md`、`docs\使用说明-先看我.md`、`docs\TEST_RECORD.md`
- 版本：`src\Shared.Core\BuildInfo.cs`、`src\Desktop.Agent\MainWindow.xaml`、`src\Setup\Program.cs`、`scripts\Build-Installer.ps1`

### 怎么验证的

- 构建：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe LanMouseShare.sln /t:Build /p:Configuration=Release` → 6 工程 0 error。
- 自动测试：仓库根目录运行 `tests\Shared.Core.Tests\bin\Release\LanMouseShare.Tests.exe` → `All tests passed.`（28 组；新增 `ControlLeaseRelayPayloadWorks` 验证中继 JSON 往返，`ControlLinkMachineWorks` / `ControlChannelCodecWorks` 覆盖状态机与 v3 帧）。
- 版本一致性：`BuildVersionIsConsistent` 断言 `BuildInfo.Version`、Agent 窗口、Setup、打包脚本、测试断言全部为 `0.1.46`。
- 文档一致性：`PROJECT_GUIDE.md` 与 `docs\PROJECT_GUIDE.md` 字节一致（`SequenceEqual` 为 True，15,014 字节）；`docs\CONTROL-STATE-MODEL.md` 的日志事件表已改为代码里真实存在的事件名，并补上中继事件与 `path=udp|relay`。
- 打包：`scripts\Build-Installer.ps1`（默认 `$Version = "0.1.46"`）产出 `dist\LanMouseShare-0.1.46\LanMouseShareSetup.exe`（361,472 字节，单资源 `Root.payload.zip`，28 个条目，含更新后的 `Docs\CONTROL-STATE-MODEL.md`）与 `LanMouseShare-0.1.46-source.zip`（163,801 字节，97 个条目，含 `ControlLinkMachine.cs` / `ControlChannelCodec.cs` / `ControlPayload.cs`），放置 6 秒后未被杀毒软件删除。

### 还没做 / 已知限制

- 本机只有一台电脑，中继通道的端到端效果必须在两台机器上按 `docs\MANUAL_TEST.md` 的 `## Version 0.1.46 relay checks` 验证：两端阻断 47534 后仍能切屏、只停对端 Service 时回落到直连、以及连续 20 轮切换。
- 中继的单程延迟约 50～100 ms（管道 + UDP 中继 + 50 ms 事件轮询），比直连路径慢；不影响“卡死上限 1 秒”的兜底语义。
- 两台电脑必须同时升级到 0.1.46：0.1.46 的 Agent 与 0.1.45 的 Agent 不能互通中继命令（旧 Agent 不认识 `SendControlLease`），会退化为“直连可用就用、否则回空闲”。

---

## 2026-10-20 · 0.1.47 · 鼠标莫名退回主控端 + 远程指针与键盘互通

### 这一轮改了什么

- 修掉“鼠标莫名退回主控端”的两个直接原因：
  - 被控端把“相对注入后指针没动”当成注入失败（`MainWindow.xaml.cs` 的 `HandleInjectionResult`）。指针贴在屏幕边上被 Windows 钳位时本来就不会动，于是每几次鼠标移动就触发 `ReleaseForInjectionBlocked`，把控制权丢回主控端。现在只记 `mouse.clamp.suspected` 调试日志，不再释放控制。
  - `RemoteEdgeReturnDetector` 原本无状态：只要在物理边缘出现一次向外增量就返回主控端（日志里的 `reason=RealEdgeOutward`）。现在改成有状态：250 ms 内累计向外推 ≥12 像素才判定 `EdgePush`，判定后进入 500 ms 冷却，单次抖动不再触发。
- 远程指针改成**绝对坐标注入**：`OnRawMouseMoved` 在远端屏幕范围内直接发 `Kind=MouseMove` 绝对位置；只有顶到远端屏幕边界时改发相对增量，把“继续往外推”的意图传给被控端用于边缘返回。原因：被控端按它自己的指针加速曲线缩放 `MOUSEEVENTF_MOVE`，累计误差让远端指针爬得慢、发飘，最后被上面的失败判定踢回主控端。
- 键盘互通加固：
  - 注入的按键记入 `_injectedHeldKeys`，控制权丢失/暂停/释放时统一补发 KEYUP，避免按住 Ctrl/Shift 切回来以后本机键盘“粘住”。
  - 新增 `key.forward` / `key.inject` 日志（按键、扫描码、扩展位、按下/抬起、注入结果），便于核对两端是否一一对应。
- 日志降噪：每 2 秒一次的 `Pipe connected. Command=GetPeerState` 由 INFO 降为 DEBUG（`pipe.send`）；被控端忽略无用输入的 warn 降为 `input.ignored` DEBUG（仍带 2 秒节流计数）。
- 版本号统一为 `0.1.47`。

### 改动的文件

- `src\Desktop.Agent\MainWindow.xaml.cs`：绝对坐标注入与边界相对增量、移除注入失败判定、按键跟踪与释放、`key.forward`/`key.inject`/`mouse.clamp.suspected`/`mouse.remote.send`/`input.ignored` 日志、`_lastInjectedMouseX/Y` 复位。
- `src\Input.Native\RemoteEdgeReturnDetector.cs`：改为有状态检测器（`PushThreshold=12`、`PushWindowMs=250`、`CooldownMs=500`，`Update` 返回 `RemoteEdgeReturnResult`）。
- `src\Host.Service\NamedPipeCommandServer.cs`：`GetPeerState` 归入静音命令。
- `tests\Shared.Core.Tests\Program.cs`：`RemoteEdgeReturnDetectionWorks` 按新语义重写（单次推送不释放、两次推送 → `EdgePush`、冷却期内不释放、角落屏蔽、垂直方向武装）。
- 版本号：`src\Shared.Core\BuildInfo.cs`、`src\Desktop.Agent\MainWindow.xaml`（两处）、`src\Setup\Program.cs`、`scripts\Build-Installer.ps1`、测试断言。

### 怎么验证的

- 构建：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe LanMouseShare.sln /t:Build /p:Configuration=Release` → 6 工程 0 error。
- 自动测试：仓库根目录运行 `tests\Shared.Core.Tests\bin\Release\LanMouseShare.Tests.exe` → `All tests passed.`（28 组）。
- 远端注入实测（LanCtrl CLI 在被控端 Win7 上执行命令，主控端用合成 UDP 帧模拟被控端输入通道）：
  - 绝对注入：发 `MouseMove(900,700)` 后远端光标精确读到 `cursor=900,700`，日志 `ev=mouse.remote.position kind=MouseMove cursor=900,700`。
  - 边缘返回：光标先移到 `(6,700)`，再连发 10 次相对 `-40`，远端记 `ev=screen.switch.return direction=Left reason=EdgePush`（旧版第一次就会触发），本机随后记 `control.link.released reason=PeerReleased remotePoint=0,700 detail=EdgePush` 并回到 `Idle`。
  - 反向边界不误触发：屏幕右边缘连续外推不会释放（方向不匹配）。
  - 键盘注入：合成 `Key` 帧后远端 `GetAsyncKeyState` 读到按下状态。
- 部署：0.1.47 已装到两台机器（本机与远端 Win7 均 `C:\Program Files\LanMouseShare`，经 LanCtrl + 本机临时 HTTP 分发；远端用 `sc start LanMouseShare` 起服务、WMI 方式起 Agent）。

### 还没做 / 已知限制

- 两台机器必须同时升级到 0.1.47；混合版本时旧版仍会在边缘抖动。
- 真实双机手感（连续切屏、键盘长按、Ctrl+C 这类组合键）仍需人工按 `docs\MANUAL_TEST.md` 跑一遍；合成帧只能证明注入链路本身正确。
- 绝对坐标注入依赖被控端上报的 `ScreenWidth/ScreenHeight` 准确，配置错误会导致远端指针落点偏移。
- 主控端进入远端时首个绝对坐标由本地累计位移推得，远端分辨率刚变化时可能有一次小幅跳动。

---

## 2026-10-20 · 0.1.50 · 输入带宽、传输优先级、日志开关与剪贴板文件

### 这一轮改了什么
- 直连输入通道使用紧凑帧：相对移动每事件 2 字节（`sbyte dx,dy`），绝对定位每事件 4 字节（`ushort x,y`），一帧最多 200 个事件；含按键、滚轮或坐标超出范围时自动回退通用帧。
- 交互流量打 `DSCP 46`（EF）：主控端 UDP 47534 直连套接字、Service 侧 UDP 47533 中继套接字都套用；日志流量打 `DSCP 8`（后台）。
- 日志推送限流：输入活跃（250 ms 内有输入）时 20 行 / 60 ms，空闲时 200 行 / 120 ms。
- 新增“推送日志到主控端”开关（`AppConfig.LogTransferEnabled`，Order 24，并用 `ConfigRevision=50` 做一次性迁移）。关闭后日志仍写本机，只是不推送。
- 剪贴板支持跨屏复制文件：`ClipboardPayload.FilePaths`、`FileCompletePayload.SavedPath`；`ClipboardSyncService` 检测到文件剪贴板后触发文件通道，文件落地后把列表写回剪贴板。两个通道到达顺序无关（`_recentSavedPaths` 120 秒缓冲）。
- 接收目录改为 `C:\Users\Public\Desktop\Mouse传输文件`：服务以 LocalSystem 运行，用户桌面路径对服务不可见，公共桌面在登录用户桌面上同样可见。
- 主控端状态行显示实测网络延时（心跳 RTT）。

### 改动的文件
- `src\Shared.Core\Networking\TrafficPriority.cs`（新增）
- `src\Shared.Core\Configuration\AppConfig.cs`、`Configuration\ConfigStore.cs`、`Ipc\AgentSettingsDto.cs`、`Ipc\PeerStateDto.cs`、`Protocol\ClipboardPayload.cs`、`Protocol\FileTransferPayload.cs`、`BuildInfo.cs`
- `src\Desktop.Agent\DirectInputChannel.cs`、`ClipboardSyncService.cs`、`RemoteInputSender.cs`、`MainWindow.xaml`、`MainWindow.xaml.cs`
- `src\Host.Service\PeerCoordinator.cs`、`Diagnostics\LogHub.cs`、`Diagnostics\LogForwarder.cs`
- `tests\Shared.Core.Tests\Program.cs`、`scripts\Build-Installer.ps1`、`src\Setup\Program.cs`，以及 docs 与 PROJECT_GUIDE。

### 怎么验证的
- 构建：`MSBuild.exe LanMouseShare.sln /t:Build /p:Configuration=Release` → 0 error。
- 自动测试：仓库根目录跑 `LanMouseShare.Tests.exe` → `All tests passed.`（29 组）。
- 实测（本机 Win10 + 远端 Win7，经 LanCtrl 部署）：合成 v3 帧 9 字节携带 2 条相对事件，远端光标按预期移动；日志出现 `DirectInputChannel listening on UDP 47534 qos=dscp46` 与 `net.input.socket udp=47533 qos=dscp46`。
- 日志开关：远端关闭推送后主控端收集器断开，远端 `live` 目录仍持续写入。
- 剪贴板文件双向：远端复制 → 本机 `C:\Users\Public\Desktop\Mouse传输文件\clip-A.txt` 落地且本机剪贴板含该文件；本机复制 → 远端剪贴板含 `...\lms-clipB.txt`。

### 还没做 / 已知限制
- 真实切屏动作仍需用户物理鼠标：Agent 以管理员运行，普通权限进程的 `SendInput` 会被 UIPI 丢弃，自动化只能用合成 UDP 帧验证注入链路。
- DSCP 只在启用 QoS 的交换/路由器上生效，普通家用路由器按先到先发处理。
- 剪贴板文件走既有文件通道，超大文件会占满上行带宽，仍建议直接拖拽或共享目录。

## 模板（下一轮直接复制）

```text
## YYYY-MM-DD · 版本号 · 主题

### 这一轮改了什么
- 

### 改动的文件
- 

### 怎么验证的
- 构建：
- 自动测试：
- 手动/冒烟：

### 还没做 / 已知限制
- 
```
## 2026-10-20 · 0.1.52 · 控制信令持久化 + 切屏锁存

### 现象

- 0.1.51 双机实测：跨屏延时明显变大，偶尔「鼠标直接跨屏」（手一停就跳回去），偶尔「跨屏不过」。
- 本机（主控端）日志出现密集 `net.send.slow`、`control.link.timeout reason=AckTimeout`；主控端每 200 ms 一次心跳、被控端每 100 ms 一次租约中继，每条报文都新建一次 TCP 连接，三次握手完不成即超时。
- 被控端日志出现连续 `control.lease.send.fail path=udp`，随后 `LeaseExpired` 回收控制权。

### 根因

1. `src\Host.Service\PeerCoordinator.cs` 的 `SendEnvelope` 对每条报文 `new TcpClient()`：200 ms 心跳 + 100 ms 中继 = 每 50–100 ms 一次连接，50 ms RTT 的链路上握手长期超时。
2. 租约/确认同时走 UDP 直连和 Service 中继，UDP 侧被防火墙挡住后失败刷屏，中继侧又因连接风暴超时，`AckTimeout`/`LeaseExpired` 反复回收控制权。
3. `DirectInputChannel` 的输入帧没有序号，链路抖动时重复包会把指针多推一次；被控端一松手，主控端边缘检测立刻再次触发切屏，形成来回弹跳。
4. 切屏状态机在「链路回到 Idle / 被对端接管」后没有进入真正的锁存，光标停在边缘时下一步就地触发。

### 改动

- `src\Host.Service\PeerCoordinator.cs`：新增持久出站连接（`_outboundClient`/`_outboundStream`/`_outboundEndpoint`/`_outboundLock`、`EnsureOutboundLocked`、`CloseOutboundLocked`、`SendEnvelopeOnStream`），连接 5 s 超时、单次重试；心跳线程仅在无持久连接时工作；入站连接结束后对端 Service 保持可达性判断。新日志 `net.outbound.open`、`net.outbound.close`。
- 控制租约与确认：被控端 `MainWindow.SendLeaseNow/SendAckNow` 一律经 Service 中继（`SendControlLease`/`SendControlLeaseAck`，100 ms 一次），UDP 直连只发输入帧。
- `src\Desktop.Agent\DirectInputChannel.cs`：帧格式新增 16 位序号（`VersionSequenced`），`AcceptSequence` 丢弃重复/倒退包，日志 `input.stale`。
- `src\Desktop.Agent\MainWindow.xaml.cs`：切屏锁存 `_edgeArmed`，链路角色变化后必须先离边 **16 px** 且 **500 ms** 才重新武装；`edge.latch` 记录每次锁存/解除原因。

### 验证

- `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe LanMouseShare.sln /t:Build /p:Configuration=Release`：6 工程 0 error。
- 仓库根目录运行 `tests\Shared.Core.Tests\bin\Release\LanMouseShare.Tests.exe`：`All tests passed.`
- 两端（本机 + 远端 Win7）部署 0.1.52，日志 `net.outbound.open` 只出现一次、`net.send.slow` 消失、`input.frame` 不再每帧刷屏。
- 合成 UDP 帧验证：中心帧被接受、重复/倒退序号按 `input.stale` 丢弃。

### 还没做 / 已知限制

- 真实手感（延时、来回切屏）仍需用户物理鼠标验收，脚本无法触发 raw input。
- 本机 Wi-Fi 到路由器的 ping 在 3–40 ms 波动，跨屏延时下限受此影响。
## 2026-10-20 · 0.1.56 · 剪贴板按需传输、文件夹打包、取消传输

### 这轮改了什么

- 复制文件/文件夹后不再立刻整包发送，只广播一条「剪贴板里有文件」的公告；等你**粘贴**时才向源端拉取，并且只传你真正要用的内容。
- 公告优先走 agent 与 agent 之间的 UDP 控制通道（通常 1 秒内到对端），失败再回退已配对 TCP 中继；此前公告排在 poll 队列里，最慢 5 秒才到端，粘得太快就表现为「剪贴板没更新」。
- 文件夹：源端自动压成 zip 再传，接收端解压出原文件夹并放进剪贴板，可以直接粘贴到资源管理器。解压先落到临时目录再把根目录提升出来，避免出现双层同名文件夹。
- 读剪贴板偶发 `CLIPBRD_E_CANT_OPEN`（被别的程序占用）时重试 6 次（180ms 递增），不再一次失败就漏掉整次复制。
- 新增「取消传输」按钮，随时取消正在接收的传输。
- 粘贴到不支持文件的目标（记事本等）不再弹提示，静默即可。

### 改动的文件

- `src\Shared.Core\Protocol\ProtocolEnvelope.cs`、`src\Shared.Core\Protocol\ControlPayload.cs`、`src\Shared.Core\Protocol\ClipboardPayload.cs`、`src\Shared.Core\Protocol\FileTransferPayload.cs`、`src\Shared.Core\Ipc\AgentCommand.cs`
- `src\Host.Service\PeerCoordinator.cs`、`src\Host.Service\NamedPipeCommandServer.cs`
- `src\Desktop.Agent\MainWindow.xaml`、`src\Desktop.Agent\MainWindow.xaml.cs`、`src\Desktop.Agent\ClipboardSyncService.cs`、`src\Desktop.Agent\FileDropSender.cs`、`src\Desktop.Agent\DirectInputChannel.cs`、`src\Desktop.Agent\RemoteInputSender.cs`
- `tests\Shared.Core.Tests\Program.cs`（新增 1 组）

### 怎么验证的

- 构建：6 工程 0 error；测试：30 组 `All tests passed.`
- 双机实测（本机 Win10 主控 / 远端 Win7 SP1 被控）：
  - 复制文本文件后立刻粘贴：约 1 秒内取回，剪贴板拿到文件并可正常粘贴。
  - 复制含子目录的文件夹后粘贴：远端得到 `lms-folder-55-135059`，内含 `a.txt` 与 `sub\b.txt`；时间戳后缀只是因为接收目录已存在同名文件夹。
  - 在资源管理器里真实 Ctrl+V：文件落地，未被 Agent 拦截。

### 还没做 / 已知限制

- 「取消传输」只取消正在接收的传输，已完成的文件不会回滚。
- 大文件夹首次压缩需要时间，期间粘贴会等待（12 秒后提示超时）。
- 远端 Win7 的 PowerShell 是 2.0，没有 `Get-Clipboard`，验证脚本只能用 WinForms 的 Clipboard API。

## 2026-10-20 · 0.1.56 · 跨屏粘贴文件改为请求式、注入搬离 UI 线程

### 改了什么

1. 跨屏粘贴文件改成主控端发起请求。以前靠被控端「看见」注入进来的 Ctrl+V 去拦截，但两端的低级钩子都会丢弃注入型按键（`InputHook` 里对 LLKHF_INJECTED / LLMHF_INJECTED 直接 return），所以被控端永远收不到这个信号。现在主控端在控制状态里检测到自己按下 Ctrl+V、且本机剪贴板是文件时，吞掉这个 V（含抬起）并发 `ClipboardPasteRequest`；被控端收到后自己拉文件、写剪贴板、再本地重放 Ctrl+V。
2. 远端注入不再经过 UI 线程。`OnDirectInputBatchReceived` 原来用 `Dispatcher.BeginInvoke` 再注入，窗口最小化或 UI 忙时注入被推迟，表现就是「控制不了鼠标 + 延迟大增」。现在直接在 UDP 接收线程注入（SendInput 不需要窗口句柄）。
3. 写剪贴板改在独立 STA 线程并 `Clipboard.Flush()`。`Clipboard.SetFileDropList` 会阻塞（别的进程占着剪贴板时），放在 UI 线程会拖住钩子回调；而写完不 Flush 时，写数据的临时线程一退出文件列表就消失，看起来就是「剪贴板没更新」。
4. 新增日志事件 `clipboard.paste.request`、`clipboard.paste.request.nofiles`，剪贴板检测持续输出 `clipboard.files.copied` / `clipboard.files.announced`。

### 改动的文件

- `src\Shared.Core\Protocol\ClipboardPayload.cs`（`ClipboardPasteRequestPayload`）
- `src\Shared.Core\Protocol\ProtocolEnvelope.cs`（`ClipboardPasteRequest`）
- `src\Shared.Core\Ipc\AgentCommand.cs`（`RequestClipboardPaste`）
- `src\Host.Service\PeerCoordinator.cs`（命令映射、暂停守卫、噪声与跨屏过滤）
- `src\Desktop.Agent\ClipboardSyncService.cs`（`LocalHasFiles`、`PutFilesOnClipboardAsync`、`Clipboard.Flush`）
- `src\Desktop.Agent\MainWindow.xaml.cs`（Ctrl+V 拦截、`BeginPasteFiles`、`RequestRemoteFilePaste`、`OnClipboardPasteRequested`、注入改线程）
- `tests\Shared.Core.Tests\Program.cs`（粘贴请求载荷往返）

### 怎么验证的

- `MSBuild.exe LanMouseShare.sln /t:Build /p:Configuration=Release` → 6 工程 0 error；仓库根目录跑 `LanMouseShare.Tests.exe` → `All tests passed.`
- 本机合成测试：本机剪贴板放一个文件 → 日志出现 `clipboard.files.copied` + `clipboard.send format=Files`；向本机 Agent 管道发 `RequestClipboardPaste` → 本机 `clipboard.pull.serve`、远端 `clipboard.pull.request` → `clipboard.files.applied` → `clipboard.files.pasted`，链路逐段都有日志。
- 远端剪贴板内容无法用 LanCtrl 远程读取（该环境下子进程的剪贴板读写均返回空，属调试工具限制），所以最终粘贴效果需要真实按键验证。

### 还没做 / 已知限制

- 「点最小化后控制不了鼠标」的修复只做了「注入不经 UI 线程」这一半；如果最小化本身还伴随 WPF 渲染停顿，需要真实环境复测确认。
- 跨屏粘贴目前只处理 Ctrl+V 直按；Ctrl+Shift+V、右键菜单粘贴、拖放仍不支持。

## 2026-10-20 · 0.1.57 · 跨屏粘滞、粘贴失效、拖拽与传输后卡顿

### 这次改了什么
- 控制心跳（租约/ack）从 WPF DispatcherTimer 搬到独立后台线程（LinkLoop），UI 卡顿不再直接掐断控制权；同时修掉心跳线程跨线程访问界面元素导致的 `Link beat failed` 刷屏。
- 远端返回检测改为「先武装、再累计」：光标必须先离返回边 120px 才算武装，之后 250ms 内累计外推 40px 才释放。修「要跨两次才拿到控制」和「一进去就弹回」。
- 返回落点从离边 8px 改为 40px，避免落点仍在边缘触发带里。
- 文件传输完成后不再自动打开资源管理器、不再自动打开收到的文件（那会抢前台并起 Office，是「传完文件后鼠标延迟大增」的直接原因），只在状态栏提示。
- 拖拽发送改为后台线程打包（文件夹 zip 可能耗几秒），并在日志里记录 `file.drop`，不再卡 UI 线程。
- 跨屏粘贴改为请求式：主控端按 Ctrl+V 时把「复制的路径清单」直接放进 `ClipboardPasteRequest` 发过去，被控端不再依赖「先收到公告再粘贴」，修掉公告晚到导致 `pending` 为空、粘贴没反应。
- 注入绝对坐标（0.1.57 前一直用相对位移，被 Windows 指针加速吞掉）。

### 改动的文件
- `src\Desktop.Agent\MainWindow.xaml.cs`、`src\Desktop.Agent\FileDropSender.cs`、`src\Desktop.Agent\ClipboardSyncService.cs`
- `src\Input.Native\RemoteEdgeReturnDetector.cs`
- `src\Shared.Core\Protocol\ClipboardPayload.cs`
- `tests\Shared.Core.Tests\Program.cs`（返回检测用例按新语义重写）

### 怎么验证的
- `MSBuild.exe LanMouseShare.sln /t:Build /p:Configuration=Release` 6 工程 0 error；仓库根目录跑 `tests\Shared.Core.Tests\bin\Release\LanMouseShare.Tests.exe` → `All tests passed.`
- 两端部署 0.1.57（本机 Agent 488 / Service 11808；远端 Win7 Agent 22288 / Service 15944）。
- 远端日志确认角色 Target、`mouse.remote.position` 持续更新（控制链路通）。

### 还没做 / 已知限制
- 剪贴板文件「A 复制 B 粘贴」与拖拽发送需由本人用真实鼠标复测（注入的按键会被钩子过滤，脚本无法模拟）。
- 双机都在 Wi-Fi/有线混合网络下，RTT 有 3–40ms 抖动；DSCP 标记需要交换机支持才生效。
## 2026-10-20 · 0.1.58 · 切屏被弹回 / 跨屏粘贴文件

### 现象
- 主控端切到被控端后，约每 1–3 秒自动弹回主控端，必须连续跨两次；鼠标"莫名退回主控端"。
- 主控端复制文件，被控端 Ctrl+V 没有任何反应，剪贴板没更新。

### 根因
- `CheckRemoteReturnToController` 没有冷却，也没有限制单次样本位移；一次快速甩动（几百像素）就能把"返回边"的累计阈值打满，于是刚拿到控制权就判成"用户要退回"。
- 粘贴拦截依赖 `ClipboardSyncService.LocalHasFiles`，该标志由剪贴板监听的防抖回调更新；用户在监听到之前按 Ctrl+V 时标志仍为 false，请求根本没发出去。

### 改动
- `src\Desktop.Agent\MainWindow.xaml.cs`：新增 `TargetReturnGraceMs = 1500`（刚成为被控端的 1.5 秒内忽略返回请求）与 `MaxReturnSamplePixels = 60`（单样本位移截断）；`CheckRemoteReturnToController` 先做这两道过滤。
- `src\Input.Native\RemoteEdgeReturnDetector.cs`：`PushThreshold` 40→90、`PushWindowMs` 250→300，新增 `MinPushSamples = 2`（单样本永不释放）；冷却时清零样本计数。
- `src\Desktop.Agent\ClipboardSyncService.cs`：新增 `RefreshLocalHasFiles()`，按 V 的瞬间直接读 `Clipboard.ContainsFileDropList()`，异常时保留旧值。
- `src\Desktop.Agent\MainWindow.xaml.cs`：Ctrl+V 拦截改用 `RefreshLocalHasFiles()`。
- `tests\Shared.Core.Tests\Program.cs`：`RemoteEdgeReturnDetectionWorks` 按新阈值（90px / 2 样本）重写。
- 版本 0.1.57 → 0.1.58（6 处）。
- 新增 `docs\FEATURE-AUDIT.md`（功能矩阵 + 与 LanCtrl 的差别 + 坐标映射现状）。

### 验证
- `MSBuild.exe LanMouseShare.sln /t:Build /p:Configuration=Release` → 0 error。
- 仓库根目录 `tests\Shared.Core.Tests\bin\Release\LanMouseShare.Tests.exe` → `All tests passed.`
- 两端部署 0.1.58：本机 Agent 15220 / Service 8596；Win7 `192.168.0.69` Agent 10936 / Service 运行中，日志 `Version=0.1.58`。
- 启动后心跳 RTT 仅启动瞬间出现一次 1222ms（WARN），随后无 ≥100ms 记录（即 <100ms）。

### 还没做 / 已知限制
- 真实双机"连续来回切 10 次 + 跨屏粘贴文件"需用户物理操作验证（注入型输入会被对端钩子过滤）。
- 未打 0.1.58 安装包。

## 2026-10-20 · 0.1.60 · 返回边缘、跨屏粘贴文件（带自测证据）

### 改了什么

- 返回边缘：`src\Input.Native\RemoteEdgeReturnDetector.cs` 阈值下调到 `PushThreshold=30`、`ArmDistance=40`、`PushWindowMs=250`（`MinPushSamples` 仍为 2），被控端在返回边缘连续两次外推即可松手，不再「蹭半天过不去」；切出侧仍是 `EdgeArmDistance=120` + 700ms 冷却，避免回到主控端后立刻又被弹走。
- 跨屏粘贴文件（双侧拦截）：
  - 主控端 Owner 态按 Ctrl+V：`clipboard.paste.intercept` → 发 `RequestClipboardPaste`（带 `FilePaths`）；
  - 被控端用自己键盘按 Ctrl+V：新增 `TryInterceptLocalFilePaste`，直接在本机拉取文件、写剪贴板、重放 Ctrl+V（`clipboard.paste.local-intercept`）；
  - 文件传输卡片新增「拉取并粘贴」按钮（`OnPullPasteClicked`），对端公告到达后状态栏显示「对端已复制 N 个文件」；
  - 所有环节都有 INFO 日志：`clipboard.files.copied/announced/pending`、`clipboard.pull.request/serve`、`clipboard.files.applied/pasted`、`clipboard.paste.intercept/skip/local-intercept/manual`。

### 怎么验证的（我这边实测，不是推断）

- 构建：`MSBuild.exe LanMouseShare.sln /t:Build /p:Configuration=Release` → 0 error；`LanMouseShare.Tests.exe` → `All tests passed.`（30 组）。
- 部署：本机 Agent 28216 / 远端 Win7 Agent 21196，两端日志均 `Version=0.1.60`。
- 返回边缘（合成 v3 UDP 帧，做成本机为 Target）：先发 lease 使本机进入 Target，等过 2000ms 宽限期，再把光标放到 2200,700，然后连发 80px 外推 → 本机日志出现
  `screen.switch.return direction=Right point=2559,700 delta=60,0 reason=EdgePush`，随即 `control.link.released`。即「被控端往返回边推两次」确实会松手。
- 跨屏粘贴（本机作为主控端跑完整链路）：设剪贴板为文件 → 命名管道发 `RequestClipboardPaste` → 本机 `clipboard.pull.serve count=1 requester=7de4809…` → 远端 `clipboard.pull.request` → `clipboard.files.applied first=C:\Users\Public\Desktop\Mouse传输文件\paste-test-060 (1).txt` → `clipboard.files.pasted`。全过程约 160ms。

### 还没做

- 真实物理鼠标的「边缘返回手感」和「物理 Ctrl+V 拦截」仍需你按键确认（我的进程发的都是注入输入，钩子会过滤，触发不了真实路径）；日志里会留下 `clipboard.paste.intercept` / `clipboard.paste.local-intercept` / `screen.switch.return`，出问题把时间点告诉我即可定位。
- 安装包 `dist\LanMouseShare-0.1.60` 尚未打（代码与部署已就绪，等你这次实测通过后再打，避免又白打一版）。

## 2026-10-20 · 0.1.64 · 大文件传输、进度条、拖拽兜底

### 改了什么
- **大文件被中途掐断（真因）**：粘贴/预取链路用的是固定 20 秒超时。335 MB 的文件在 2 MB/s 下需要约 2 分 40 秒，20 秒一到主控端就发 CancelTransfers，对端在写文件的同时被 Dispose 了文件流 → NullReferenceException → 连接被重置（日志里的“远程主机强迫关闭了一个现有的连接”）。改为**空闲 30 秒才超时**（总上限 30 分钟），并在收到 FileProgress 时续期。
- **取消时的竞态**：CancelIncomingTransfers 现在先 lock (transfer.Sync) 再把 Stream 置空，读循环里也做了空检查，不再在写入过程中被释放。
- **进度条**：新增 FileProgressPayload + ProtocolMessageTypes.FileProgress；服务端在发送/接收循环里每 250 ms 推一条进度（已传字节、总字节、方向、速度、是否完成）；界面「文件传输」卡片新增 ProgressBar 和一行状态文本（正在发送/正在接收 文件名 -> 45% (40 MB / 90 MB) 2.8 MB/s）。
- **拖拽兜底**：ScrollViewer、根 Grid、拖拽区 Border 都加了 AllowDrop 与 DragOver；新增「发送文件…」按钮（文件选择框）。原因：Agent 以管理员权限运行，Explorer 是中完整性，UIPI 会拦掉跨完整性级别的 OLE 拖放，拖拽在提权进程里天然不可靠。
- 版本 0.1.63 → 0.1.64。

### 怎么验证的（本机 → 远端 Win7 192.168.0.69，两端 0.1.64）
- 60 MB 文件经 SendFiles：26 秒传完，FileComplete sent，对端落地 62,914,560 字节。
- 之前失败的 **335,712,376 字节 zip**：完整传完（FileStream sent ... Sent=335712376/335712376 → FileComplete sent），对端目录可见同样大小文件。修复前该文件在 51 MB 处被掐断。
- 60 MB 文件走「复制 → 粘贴」：对端 clipboard.files.applied 成功，用时约 70 秒，未再出现 20 秒超时。
- 进度事件实测：ile.progress dir=send pct=86 ... pct=100，界面进度条随之上行。
- 速度：本机 → 远端约 2.0–2.8 MB/s；同一链路上 LanCtrl 的 --put 为 1.34 MB/s，说明瓶颈在链路（本机为 Wi-Fi），不是传输实现。

### 还没做 / 已知限制
- 进度条只在传输进行时更新，完成后停在 100%，下一次传输会重置。
- 拖拽若被 UIPI 拦截（Agent 提权时），请用「发送文件…」按钮；这两条都会在 ile.drop.ui / ile.pick 里留下日志。
- 未打 0.1.64 安装包，等实测确认后再打 dist\LanMouseShare-0.1.64。