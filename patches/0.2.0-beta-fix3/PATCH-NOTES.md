# 0.2.0-beta-fix3 补丁说明（PATCH-NOTES）

日期：2026-10-29
基线：0.2.0-beta-fix2

## 本次修复的三项问题

### 1. 剪贴板文件：复制后立即传输 → 粘贴时才传输（双向）

**现象**：电脑 A 上复制文件，B 还没粘贴就开始传输（且 B 侧看到的是文件已就绪）；期望 A 复制、B 按 Ctrl+V 粘贴那一刻才开始传输，B→A 同理。

**根因**：`src/Desktop.Agent/MainWindow.xaml.cs` 的 `OnRemoteFilesPending` 中，虚拟剪贴板（`VirtualClipboardEnabled=false`，恒不可用）失败后回退执行了 `BeginPasteFiles(e.FilePaths, false)`——收到对端文件公告就立即预取全部文件。UDP 通道（`OnClipboardFilesAnnounced`）没有这个回退，是懒的，导致 TCP/UDP 两路行为不一致。

**修复**：删除预取回退，TCP 路径与 UDP 路径一致变懒——只登记文件清单（`RegisterRemoteFiles`）并提示状态"对端已复制 N 个文件，可按 Ctrl+V 或点'拉取并粘贴'"。真正的传输由既有粘贴拦截链触发：
- 主控端（Owner）按 Ctrl+V 且本机剪贴板是文件 → 吞键，发 `ClipboardPasteRequest` 请被控端拉取并粘贴；
- 被控端（Target）收到注入 Ctrl+V 或本机物理 Ctrl+V 且有对端待拉文件 → 拦截、`BeginPasteFiles` 拉取（`FetchClipboardFile`，空闲 30s 超时）、写剪贴板、重放 Ctrl+V；
- UI 上的"拉取并粘贴"按钮仍然可用。

### 2. 自动启动设置失效

**现象**：勾选"开机自动启动"后重启电脑，Agent 没有启动。

**根因**（两处）：
1. Agent 以管理员身份运行（或勾选"以管理员启动"）时，启动项走计划任务。`StartupTaskManager.ConfigureAdminStartupTask` 用 `schtasks /Create ... /SC ONLOGON` 但不带 `/IT`——在 Win7（以及部分 Win10 配置）上创建 ONLOGON 任务会要求交互密码而失败；而 `ApplyStartup` 是先删 Run 键再建任务，任务失败后 Run 键也没了，启动项完全丢失。
2. Agent 启动时从服务加载设置（`ApplySettingsToUi`）后从不重放 `ApplyStartupOptions`，注册表/计划任务的陈旧状态无法自愈。

**修复**：
1. 计划任务命令加 `/IT`（交互式登录触发，免密创建，Win7/Win10 均可用）。
2. `ApplyStartup` 重排：先尝试创建计划任务，成功后才删除 Run 键；创建失败（异常）时保留/回写 Run 键兜底。
3. `MainWindow.ApplySettingsToUi` 加载设置后一次性后台重放 `ApplyStartupOptions`，让启动项与配置保持一致（自愈）。

### 3. 边角阻挡：A→B 生效、B→A 失效

**现象**：两台机器都勾选"跨屏边角阻挡"，但只有一台出边时被阻挡。

**根因**：`BlockScreenCorners` 只保存在各自机器的 config.json 里（`PeerCoordinator.ApplySettings` 只写本机 `_config.BlockScreenCorners`，从不通知对端）。两端勾选状态不一致时（例如 A=true、B=false），B 出边不阻挡。检测代码本身（`EdgeSwitchDetector.IsInBlockedCorner`，四个方向对称）没有问题。

**修复**：复用既有的布局同步协议——
- `LayoutConfig` 增加可空字段 `BlockScreenCorners`（DataMember Order=2，兼容旧协议）；
- `SendLayoutUpdateToPeer` 把本机勾选状态透传给对端；
- 对端收到 `LayoutUpdate` 后若字段有值则写入自己的 config（不二次转发，防止回环）；
- `ApplySettings`（勾选框变化 → UpdateSettings 命令）保存后向对端发送一次带勾选状态的布局更新，最后写者胜；
- 对端 Agent 靠 2 秒状态轮询收敛，勾选框会自动同步。

## 版本

- 客户端/服务/构建脚本：0.2.0-beta-fix3
- 安装器（Setup）版本号保持 0.2.1（纯数字，升级比较用，无变化）

## 安装方法（两台机器都装）

1. 停止旧版本：
   - `taskkill /F /IM LanMouseShare.Agent.exe`
   - `net stop LanMouseShare`
2. 备份 `C:\Program Files\LanMouseShare`（改名为 LanMouseShare-backup）
3. 覆盖安装：把包内 `Agent\` 复制到 `C:\Program Files\LanMouseShare\Agent\`，`Service\` 复制到 `C:\Program Files\LanMouseShare\Service\`
   （或用 LanMouseShareSetup.exe 安装器）
4. 启动：`net start LanMouseShare`，然后运行 `C:\Program Files\LanMouseShare\Agent\LanMouseShare.Agent.exe`

## 验证要点

1. 文件复制：A 复制文件 → B 状态栏提示"对端已复制 N 个文件" → B 按 Ctrl+V 才开始传输并粘贴。
2. 自动启动：勾选"开机自动启动"→ 重启 → Agent 自动出现在托盘（静默）；`schtasks /Query /TN LanMouseShare.Agent.Admin` 或注册表 Run 键二选一存在。
3. 边角阻挡：任意一端勾选后，两端四角出边都被阻挡；取消勾选后两端都恢复。
