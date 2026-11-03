# 修复总结 - 0.2.0-beta-fix24（含 fix1～fix23）

## 🆕 fix24：跨屏来回对称，返回不再需要“多推一段”（2026-11-03 傍晚）

### 需求

- 用户：「从 a 到 b 一瞬间过去，从 b 到 a 则是要多走一段距离才能跨屏 这是为什么能改吗」，随后要求两边完全对称、返回接近瞬间。

### 原因

- fix19 为修“右键菜单弹在 A 上”，在返回路径上加了三道**等待式**门槛：先推进 40px 武装、在返回边累计驻留 260ms、累计推力 140px，回切后还有 700ms 冷却；而 A→B 走的是普通越边路径，没有任何门槛，于是两个方向手感差一截。

### 改动

- `src/Desktop.Agent/MainWindow.xaml.cs`：`OwnerReturnPushThreshold` 140 → **8**、`OwnerReturnArmMs` 400 → **0**、`OwnerReturnArmDistance` 40 → **0**、`OwnerReturnDwellMs` 260 → **0**、`OwnerReturnCooldownMs` 700 → **200**；`TrackOwnerReturnPush` 去掉武装判定与驻留判定（保留推力累计与日志）。
- `src/Input.Native/EdgeTransitionMapper.cs`：`RemoteInset` 8 → **40**。进场落点离返回边 40px，快速扫过 / 手抖不再立刻弹回——fix19 那个右键落错端的根因是“落点贴着返回边”，这里把根因挪走，而不是用计时器拖慢用户。
- `tests/Shared.Core.Tests/Program.cs`：`MapEntryToRemote` 的进场断言 8 → 40。
- 版本 0.2.0-beta-fix24，`AssemblyFileVersion 0.2.1.9`，`SetupVersion 0.2.7`。

### 验证

- `MSBuild LanMouseShare.sln` 0 error（仅既有 `CS0162` / `CS0067`）。
- `LanMouseShare.Tests.exe` → `All tests passed.`
- 待实机：两个方向推过边缘应立刻回切；`mouse.return.push` 的 `total` 不再累积到上百像素。

## 🆕 fix23：返回本机也画波纹，600 毫秒去抖（2026-11-03 晚）

### 症状

- fix22 之后两个方向不对称：从本机跨到对端有波纹，**从对端返回本机时本机没有任何提示**。

### 根因

- fix22 为了去掉“主控端边缘多画一圈”，把 `ReleaseControlToLocalEdge` 里的 `ShowScreenEntryRipple("local")` 整个删掉了。而“控制权回到本机”正是本机唯一会给指针画波纹的时机，删掉之后这个方向就没有任何提示。

### 改动

- `src/Desktop.Agent/MainWindow.xaml.cs`：恢复 `ReleaseControlToLocalEdge` 里的 `ShowScreenEntryRipple("local")`（指针刚被设回本机屏幕之后）。
- `src/Desktop.Agent/MainWindow.xaml.cs`：新增 `RippleMinGapMs = 600` 与 `_lastRippleUtc`；`ShowScreenEntryRipple` 在距上一次不足 600ms 时直接跳过并记 `ripple.skip`（带 `gap=`），保证一次跨屏只出现一圈，不会 target 与 local 两次触发叠在一起。
- `src/Desktop.Agent/ScreenEntryRippleWindow.cs`：`ShowAtCursor` 改为返回 `bool`（取不到光标位置时返回 false），只有真正画出波纹才更新去抖时间戳。
- 版本 0.2.0-beta-fix24，`AssemblyFileVersion 0.2.1.9`，`SetupVersion 0.2.6`。

### 验证

- `MSBuild LanMouseShare.sln` 0 error（仅既有 `CS0162` / `CS0067`）。

## 🆕 fix22：接收目录可自定义，返回方向不再多画波纹（2026-11-03 傍晚）

### 症状

- 用电脑 A 控制电脑 B、再从 B 返回 A 时，**B 自己屏幕的边缘也会概率性出现一圈波纹**——B 是主控端，物理指针从没离开过 B，这圈提示没有意义。
- 文件接收目录写死在桌面，想放到 `D:\接收` 或移动硬盘上做不到。

### 根因

- fix21 在 `ReleaseControlToLocalEdge`（主控端把控制权收回本机时走的路）里也调了 `ShowScreenEntryRipple("local")`。主控端的物理指针一直在自己屏幕上，只有“刚被接管”的那一端才需要提示。
- 日志证据：本机（主控端）当天 20+ 条 `ripple.show trigger=local`，对端（被控端）41 条 `trigger=target` 且显示正常。

### 改动

- `src/Desktop.Agent/MainWindow.xaml.cs`：删掉 `ReleaseControlToLocalEdge` 里的波纹调用，只保留“变成 Target”边沿的 `trigger=target`。（**该做法在 fix23 被修正**：返回本机这个方向仍需提示，改为恢复调用 + 600ms 去抖。）
- `src/Shared.Core/Configuration/AppConfig.cs`：新增 `IncomingDirectory`（Order 28，空 = 默认桌面）。
- `src/Shared.Core/Ipc/AgentSettingsDto.cs` / `PeerStateDto.cs`：各加 `IncomingDirectory`（Order 18 / 38）下发与回填。
- `src/Host.Service/PeerCoordinator.cs`：`ApplySettings` 落 `config.json`（空串归一化，日志带 `incomingDir=`）；`ResolveIncomingFolder()` 优先用自定义目录，`TryPrepareDirectory()` 写探针失败则退回公共桌面并记 `transfer.dir.custom.invalid`。
- `src/Desktop.Agent/MainWindow.xaml` + `.xaml.cs`：文件传输卡片显示当前接收目录，新增「更改接收目录…」（`FolderBrowserDialog`）与「恢复默认」；`GetTransferDirectory()` 优先用配置目录，探针失败记 `transfer.dir.custom.invalid` 后退回原候选链（桌面 → 公共桌面 → 下载 → LocalAppData）。

### 验收

- `MSBuild LanMouseShare.sln -t:Build -p:Configuration=Release` → 0 error（仅既有 `CS0162` / `CS0067`）。
- 配置链路：`config.json` 出现 `IncomingDirectory`，重启保留；「恢复默认」清空回桌面。
- 日志：改后本机不再出现 `ripple.show trigger=local`，对端仍为 `trigger=target`。

## 🆕 fix21：水波纹跟着鼠标走，1 秒消失，回来也提示（2026-11-03 傍晚）

### 症状

- fix20 的波纹画在“跨屏落点”上（进场点固定在对端 x=8），用户要的是**跟着鼠标走**的提示；
- 从 B 推回 A 时 A 上完全不显示——因为 A 走的是“主控端 → 空闲”的返回路径，
  从来不进入 `Target` 状态，而 fix20 只在“刚变成 Target”的边沿触发。

### 改动

- `src/Desktop.Agent/ScreenEntryRippleWindow.cs` 重写为跟随式水波纹覆盖层：窗口出现即全亮（无淡入），
  `DispatcherTimer`（16ms、`DispatcherPriority.Render`）每拍重新 `GetCursorPos` 并移动窗口；
  同时 4 圈波纹按 190ms 错开出生，各自从 10px 扩到 82px（缓出）并随扩散降低透明度，
  整条提示 1.0 秒后自关、最后 180ms 整体淡出；沿用 `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`。
- `src/Desktop.Agent/MainWindow.xaml.cs`：`ShowScreenEntryRipple(string trigger)` 多一个日志触发源参数；
  除“变成 Target”外，`ReleaseControlToLocalEdge` 在 `CursorController.SetPosition(...)` 之后也调一次（`trigger=local`），
  所以控制权回到本机时同样有提示。

### 验收

- `Desktop.Agent` Release 编译 0 error（仅既有 `CS0162` / `CS0067`）。
- 反射烟测（水波纹版）：t=150ms 只有第 1 圈可见（直径 81px、透明度 0.69）；t=400ms 三圈同时可见（148/103/25px，透明度 0.23/0.56/0.98）；t=800ms 三圈接近扩满（164/164/151px）且透明度趋 0；窗口位置随光标变化（1071,940 → 792,959 → 1004,905，跟随生效）；t=1150ms 窗口已关闭（总时长 1 秒）。



## 🆕 fix20：跨屏后给鼠标落点画一圈波纹（2026-11-03 下午）

需求：“增加一个按钮打开功能就是跨屏后的鼠标有波纹，这样跨屏后好更快的注意到鼠标。”

### 做法

- 新增 `src/Desktop.Agent/ScreenEntryRippleWindow.cs`：无边框、全透明、置顶，`ShowActivated=false`、
  `IsHitTestVisible=false`，并在 `GWL_EXSTYLE` 上加 `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，
  所以它既不抢焦点也不吃点击；金黄（#FFD640）+ 白两圈在 620ms 内从 0.18 倍扩散到 1 倍并淡出，
  播完自动 `Close()`，另有 1200ms 兜底定时器防止残留透明窗口。
- `src/Desktop.Agent/MainWindow.xaml`：输入设置里新增「跨屏后高亮鼠标落点」勾选框 `ScreenEntryRippleBox`。
- `src/Desktop.Agent/MainWindow.xaml.cs`：`SyncLinkFlags` 里用 `_wasTargetRole` 识别“刚刚变成 Target”的边沿，
  调 `ShowScreenEntryRipple()`；非 UI 线程会先 `Dispatcher.BeginInvoke` 回来，再按 `VisualTreeHelper.GetDpi`
  换算 DPI 后用 `GetCursorPos` 定位，因此只在被控端出现、且位置就是指针落点。
- 配置链路：`AppConfig.ScreenEntryRippleEnabled`（默认 true，`Order = 27`）→ `AgentSettingsDto.Order = 17`
  → `PeerCoordinator.ApplySettings` 落盘（设置日志多了 `ripple=`）→ `PeerStateDto.Order = 37` 回传界面回填勾选框。

### 验收

- `LanMouseShare.sln` Release 编译 0 error（只有既有的 `CS0162` / `CS0067` 两个 warning）。
- 反射烟测：窗口 `visible=True topmost=True showActivated=False hitTestVisible=False`；离屏渲染出 2554 个
  非透明像素、外沿 118×118（144×144 窗口内居中），约 600ms 后自动关闭。
- `AppConfig` / `AgentSettingsDto` / `PeerStateDto` 三个类型的 `ScreenEntryRippleEnabled` JSON 往返均为 true。



## 🆕 fix19：跨屏后点右键，右键菜单弹在本机（2026-11-03 上午）

现象：从 A 跨屏到 B 之后点鼠标右键，菜单不在 B 上出现，而是弹在 A 上；左键看起来正常，鼠标移动也正常。
用户的原话是“跟跨屏后鼠标位置没关系，不管怎么移动，右键都在 A 上”。

### 根因

不是右键本身有问题（项目里没有任何右键专有逻辑，左右键走的是同一条转发路径），而是**控制权在跨屏后
一两秒就被自动收回本机**，之后所有点击都在本机执行：

- 进场落点恒在对端 `x=8`（`EdgeTransitionMapper.RemoteInset`），离返回边只有 8px；
- owner 侧返回手势 `TrackOwnerReturnPush` 的门槛是“320ms 内累计 70px 向外”，而在对端左缘附近
  一次快速左扫实测 **254px→0 只用 56ms**，本来就远超门槛；
- 于是一次“扫一下”就被判成“要回去”，日志里 10:40 一轮里四次跨屏分别在 1.8s / 1.9s / **0.37s** / 2.3s
  后触发 `screen.return.owner`；
- 回切后 `role=Idle`，Idle 分支对鼠标按键不做抑制，点击直接由本机执行 → 右键菜单出在 A。
  诊断统计：78 次按键里 **72 次是 `role=Idle suppress=0`**，只有 6 次真的转发到了 B。

注：fix14 当时正是为了“靠近边缘也能顺利跨过去”，主动删掉了“必须先往里走 80px 才开始算返回推挤”
这个前置条件（见 `docs/FIX-SUMMARY.md` 里 fix14 那条），fix19 用更温和的方式把它补回来。

### 修法（`src/Desktop.Agent/MainWindow.xaml.cs`）

- **武装门槛** `OwnerReturnArmDistance = 40`：每次接管对端都从未武装开始，指针必须先真正进到对端
  40px 以上，才允许把边缘的推动判成返回；
- **驻留判定** `OwnerReturnDwellMs = 260`：必须在返回边连续驻留 260ms 才计入，快速左扫（几十毫秒）
  直接被挡掉，离开边缘即清零；
- **阈值收紧** `OwnerReturnPushThreshold` 70 → **140**；
- **冷却** `OwnerReturnCooldownMs = 700`：回切后 700ms 内不再判返回，避免“回去—又进来—又回去”。

真想回本机时，压住边缘继续推即可：累计推力与驻留时间会一起达标，手感只是比“扫一下”稍用力。

### 验证

- 本机实测：跨到 B 后在左缘快速左扫不再触发 `screen.return.owner`；压住左缘 ≥300ms 仍能正常回切
- `mouse.return.push` 日志新增 `dwell=` 字段，可以直接看出驻留了多久

## 🆕 fix18：整套图标统一（2026-10-30 上午）

之前没有图标资源：exe 用默认空白图标，主窗口没有 `Icon`，托盘直接拿 `System.Drawing.SystemIcons.Application`
顶上，安装包和卸载项也是同一个空白。

### 做法

- 新增 `src/Desktop.Agent/Resources/app.ico`：16/32/48/256 四帧，每帧按目标尺寸单独重绘（不是一张母图缩到底），
  16px 只保留「一块屏幕 + 鼠标指针」，保证托盘里还认得出；安装包通过 `<ApplicationIcon>` 复用同一份文件
- `<Window Icon="/Resources/app.ico">` 管窗口与任务栏；托盘在 `MainWindow.LoadTrayIcon()` 里从
  `pack://application:,,,/Resources/app.ico` 取 **16×16 那一帧**（不能拿 256 帧缩，会糊），读不到时退回系统图标
- 快捷方式与卸载项的 `IconLocation`/`DisplayIcon` 本来就指向 `LanMouseShare.Agent.exe`，exe 有图标后自动跟着变

### 验证

- 从 `LanMouseShare.Agent.exe` 与 `LanMouseShareSetup.exe` 提取图标，四个尺寸都在
- 运行时任务栏、窗口左上角、托盘三处显示同一枚图标

## 🆕 fix17：复制文本后，复制那一端的 Agent 直接退出（2026-10-30 上午）

fix16 把剪贴板读写搬到专用 STA 线程之后，读剪贴板前那句“开关是否打开”的判断仍然去读 WPF 的复选框
（`ClipboardEnabledBox.IsChecked`）。WPF 依赖属性只能在 UI 线程上读，从剪贴板线程或管道线程上读会抛
`InvalidOperationException`；这条线程上的异常没有人接，默认行为是直接终结整个进程。

于是：只要复制一次文本，复制那一端的 Agent 立刻消失。从对端看就是“鼠标键盘突然完全没反应、画面还在”
——被控端还在等输入，注入端已经死了。

### 修法

1. `MainWindow.IsClipboardEnabled()` 改成只读 `volatile bool _clipboardEnabledCache`，开关状态在 UI 线程上
   （`OnSettingsChanged` 与配置下发处）镜像进去，任何线程读它都不碰 WPF 控件
2. `ClipboardWorker.Post` 投递的任务体统一包一层 `SafeRun`，并给剪贴板线程的 Dispatcher 挂
   `UnhandledException` 兜底——剪贴板任务出错只记日志，不再连坐整个程序

### 验证

- 本机连续复制文本：Agent 进程存活，日志出现 `clipboard.local.detected` 与 `clipboard.send`
- Windows 事件日志不再出现 `Application Error` / `.NET Runtime` 的 `LanMouseShare.Agent.exe` 崩溃记录
- 崩溃栈（fix16 的现象）：`Dispatcher.VerifyAccess() ← DependencyObject.GetValue ← ToggleButton.get_IsChecked ← MainWindow.IsClipboardEnabled() ← ClipboardSyncService.ReadLocalClipboard()`

## 🆕 fix16：跨屏复制到剪贴板必卡鼠标（2026-10-30 上午）

用户反馈：「现在项目改的差不多了 就有一个问题，跨屏复制东西到剪切板，必会卡顿鼠标」。
现象很具体：**任意文本**都会卡（长短都卡），被控端在剪贴板被写入的那一刻卡一下，之后拖鼠标还会持续卡好几秒。

### 根因：三条叠在一起

1. 低级键鼠钩子（`WH_MOUSE_LL` / `WH_KEYBOARD_LL`）装在 WPF UI 线程上 ——
   `src/Desktop.Agent/MainWindow.xaml.cs:195` 的 `_hook.Start();`，钩子回调只能在这条线程的消息泵里跑，
   UI 线程被占住，整台机器的鼠标输入就跟着停。
2. 剪贴板的**所有** `System.Windows.Clipboard.*` 调用（读文本/图片、PNG 编码、写文本/图片、
   `SetFileDropList` + `Flush`、Ctrl+V 转发前的文件探测）也都在 UI 线程上 ——
   `WM_CLIPBOARDUPDATE` 由 `ClipboardMonitor` 的 WndProc 派发，处理函数直接在 UI 线程读剪贴板。
3. 写入端雪上加霜：`System.Windows.Clipboard` 内部对 `COMException` 自带 **10 次 × 100ms** 的
   `Thread.Sleep` 重试，外面 `TryApply` 再套 5 次 `DispatcherTimer` 重试 ——
   于是"写入那一刻卡一下"和"之后拖鼠标卡好几秒"正是这两层重试叠加出来的。

### 修法

- 新增 `src/Desktop.Agent/ClipboardWorker.cs`：一条常驻后台 **STA** 线程 + 完整 WPF `Dispatcher` 消息泵 +
  `OleInitialize`，对外只暴露 `Post` / `PostDelayed` / `RunInline`，调用方永不阻塞；
  线程常驻，剪贴板数据写完不会随线程退出而失效。
- `ClipboardSyncService` 里所有剪贴板 I/O 搬进 worker：`OnClipboardChanged` 只做守卫、去抖与投递，
  真正读取在 `ReadLocalClipboard`；`ApplyRemote` 投递到 `ApplyOnWorker`；
  `PutFilesOnClipboard`、`RefreshLocalHasFiles` 同理（Ctrl+V 转发路径只回缓存值，不再同步读剪贴板）。
- 去抖从 `DispatcherTimer` 改成 worker 线程上的延迟投递（单飞标志）。
- 写入重试 5 次 → 3 次（`MaxApplyAttempts`），间隔收敛到 `Math.Min(800, 90 * attempt)`。
- 跨线程状态改安全写法：`_applyingRemote` 用 `volatile`，抑制窗口用 `Interlocked.Read/Exchange` 操作 ticks。
- 日志新增 `cost=毫秒`（`clipboard.io` / `clipboard.remote.applied`），以后卡顿能直接看出时间花在哪一段。

### 验证

- 全解决方案 Release 编译 0 error；
- 反射加载真实 `LanMouseShare.Agent.exe` 跑冒烟：worker 线程上 `SetText` / `GetText` 成功；
  worker 写剪贴板耗时 **1076ms** 的那一次，调用线程最长停顿只有 **44ms**；`PostDelayed(150)` 按时触发；
- 现场还发现这台机器的剪贴板被其他进程频繁占用（单次写入要烧掉约 1 秒），
  所以旧版在这台机器上必卡，新版只是把这 1 秒挪出了 UI 线程；
- 出厂包 `dist/LanMouseShare-0.2.0-beta-fix16` 的 Agent.exe 里能搜到 `0.2.0-beta-fix16`，
  Agent/Service 的 FileVersion 为 `0.2.1.1`（AssemblyVersion 保持 `0.2.1.0`，不影响程序集绑定）。

---

## 🆕 fix15：边角阻挡又失效了，而且只有低分辨率那一端有效（2026-10-29 深夜）

用户反馈（fix14 之后）：「跨屏边角阻挡，现在又失效了，就只有低分辨率的那一端可以做跨屏阻挡」。

这是**两个问题叠在一起**：

### 1. 返回路径上的边角检查被 fix14 一起删掉了

fix14 回退跨屏逻辑时，把 `TrackOwnerReturnPush` 里 fix4 的边角阻挡一并删了。结果：

- 从本机往对端推出去（前进方向）时，`EdgeSwitchDetector` 的边角阻挡还在，角落是拦得住的；
- 但从对端往回推（返回方向）时，那个判断在**拥有控制权的那台机器**上做，正好是被删掉的那段，
  于是贴着对端屏幕的上下角往回推，会直接从角落穿回来。

现在把这段按 fix4 的原样加回来了：用 `OppositeDirection(direction)` 判断"穿回来的那条边"
（进入边的对面），坐标换算与 `RemoteScreenWidth/Height` 一致。

### 2. 阻挡带是固定 80 像素，两台机器分辨率不同就完全不是一回事

`CornerBlockPixels = 80` 是绝对像素：

| 机器 | 屏幕 | 80px 占高度 |
| --- | --- | --- |
| 高分辨率那台 | 2880×1920 | 4.2% |
| 低分辨率那台 | 1920×1080（100% 缩放） | 7.4% |

高分辨率那台往往还带 150%~200% 的 DPI 缩放（程序是 system-DPI aware，拿到的是物理像素），
80 物理像素在视觉上只剩 40~53 个逻辑像素——**看起来就像没有阻挡**。这就是"只有低分辨率
那一端能做跨屏阻挡"的来源。

修法：新增 `CornerBlockFor(direction, width, height)`，按屏幕尺寸取比例：

```
阻挡带 = max(80, round(参考尺寸 × 0.075))
参考尺寸：Left/Right 边看高度，Up/Down 边看宽度
```

2880×1920 上算出 144px（约等于原来 200% 缩放下的 72 逻辑像素），1920×1080 上算出 81px，
两台机器看到的是差不多一样大的一圈角落禁区。四处判定（Idle 前进、Target 取回、Target 返回、
Owner 返回）现在都用同一个函数。

### 验证

- 全解决方案编译 0 error（只剩 `ClipboardSyncService` 的两条既有警告）；
- `All tests passed.`（版本断言同步到 `0.2.0-beta-fix15` / `SetupVersion 0.2.4`）；
- 出厂二进制核对：`dist/LanMouseShare-0.2.0-beta-fix15` 的 Agent.exe 里能搜到
  `0.2.0-beta-fix15`，安装器里能搜到 `0.2.4`。

---

## fix14：跨屏鼠标逻辑回退到 0.2.0-beta-fix1（2026-10-29 深夜）

用户反馈：「最新版的鼠标操作逻辑…有时候会出现有一部分达不到位置的神奇的现象，之前的版本
没有这种问题」，要求把鼠标/键盘的跨屏逻辑回退到 `0.2.0-beta-fix1`，同时保留 fix12 的侧键转发。

### 回退的依据

用 `dist/LanMouseShare-0.2.0-beta-fix1/source/`（旧包里带完整源码）逐方法对比，fix1 之后
真正改过跨屏判定的只有 fix10 的三处，其余（`OnRawMouseMoved`、`ReleaseControlToLocalEdge`、
`RequestReleaseFromOwner`、`ApplyPeerControlRelease`、`InitializeRemoteVirtualPosition`、
`EdgeSwitchDetector`、`RemoteEdgeReturnDetector` 及各超时/阈值常量）与 fix1 完全一致。

被回退的三处：
1. `TrackOwnerReturnPush` 里 fix10 加的「进入对端后必须先在屏幕里走进 `EdgeArmDistance`(80px)
   才开始累计返回推挤」（`_ownerReturnArmed`）——刚跨过去想立刻回来时，这 80px 内怎么推都不算；
2. `ArmEdgeSwitch` 的 fix10 重载 + `ApplyPeerControlRelease` 用 `1200ms` 冷却、`140px`
   重新武装距离——对端把控制权还回来之后，指针停在相邻边附近的那 140px 里，**怎么都切不过去**，
   这也是"有一部分位置达不到"最难解释的一种；
3. `TrackOwnerReturnPush` 里 fix4 的边角阻挡（`_blockScreenCorners` + `IsInBlockedCorner`）——
   返回路径上顶/底角带被拦掉，那两个角就"回不来"。现在返回路径与 fix1 一样不再受它影响
   （前进方向跨屏的边角阻挡由 `EdgeSwitchDetector` 各自负责，未受影响）。

对照结果：`TrackOwnerReturnPush`、`ArmEdgeSwitch`、`ArmEdgeSwitchIfMovedAway`、
`ActivateRemoteControl`、`ApplyPeerControlRelease` 五个方法与 fix1 **逐字节一致**（diff 为 0 行），
相关常量也全部回到 fix1 的值（`EdgeSwitchCooldownMs=400`、`EdgeArmDistance=80`、
`OwnerReturnArmMs=400`、`OwnerReturnPushWindowMs=320`、`OwnerReturnPushThreshold=70`）。
删掉的成员：`_ownerReturnArmed`、`_edgeArmDistance`、
`EdgeSwitchCooldownAfterReleaseMs`、`EdgeArmDistanceAfterRelease`、`OppositeDirection()`。

### 特意保留的两处（不与 fix1 一致）

- **键盘防误触**（fix10）：`OnInputCaptured` 的 Idle 分支与 `HandleTargetLocalInput` 开头的
  `if (e.Payload.Kind != InputKinds.MouseMove) return;`。键盘/滚轮事件的 `X/Y` 是 0，
  旧逻辑会把它当成"指针在屏幕左上角"，敲一下键盘就可能把控制权丢给对端——这正是上一轮
  "在本机打字却出现在另一台电脑"的直接原因。它只影响"谁触发跨屏"，不影响能到达哪些位置，
  所以保留。
- **侧键转发**（fix12）：`WM_XBUTTONDOWN/UP` + `mouseData` 高位经 `payload.Delta` 传递，
  以及 `InjectMouseButton` 的 XBUTTON 分支、`ReleaseAllMouseButtons` 补发两颗侧键抬起。
  用户明确要求"在此基础上保留侧键"。

`InputHook.cs` / `InputInjector.cs` 与 fix1 的差异，经逐行对比确认**只有**侧键这些行。

---

## fix13：补齐卸载功能（2026-10-29 夜）

用户原话：「还有最后一步，你一定要加一个卸载功能」。

其实安装器一直带 `/uninstall`，也能在「设置 → 应用」里看到，但缺口很明显：
- 卸载不会清 HKCU 下 fix10 加的右键菜单（`Software\Classes\*\shell\LanMouseShareSend`、
  `Directory\shell\...`、`lnkfile\shell\...`），装完再卸，"传输到对端"还留在资源管理器里；
- `C:\ProgramData\LanMouseShare`（配置、日志、配对记录）被无条件永久保留，用户完全不知情；
- 卸载结束后没有任何提示，用户不知道删干净了没有；
- 只认 `/uninstall` 一种写法；
- 「设置 → 应用」里显示的是安装器的编号（0.2.1），不是产品版本。

### 现在的三个入口
1. 开始菜单「LAN Mouse Share → 卸载 LAN Mouse Share」（快捷方式参数改成 `--uninstall`）；
2. Windows「设置 → 应用 → 已安装的应用」里的 LAN Mouse Share（`UninstallString` 走 `--uninstall`）；
3. 软件本体里：主窗口文件传输区多了「卸载」按钮，托盘右键菜单也有「卸载 LAN Mouse Share…」——
   从这两个入口点下去会 `runas` 拉起安装器的卸载流程（安装器带管理员清单，会弹 UAC），
   拉起成功后 Agent 自己退出。

### 卸载流程（`src/Setup/Program.cs` 的 `Uninstall()`）
先摘开机自启（否则刚杀进程就被 Run 项拉回来）→ 弹窗问「要不要连配置、日志与配对记录一起删」
（默认按钮是"否"：只删程序；"取消"表示整件事作废，什么都不动）→ 结束 Agent/Service 进程并卸服务 →
删防火墙规则 → 删三个右键菜单键并 `SHChangeNotify(SHCNE_ASSOCCHANGED)` 让外壳立即刷新 →
删快捷方式 → 删卸载注册项 → 按用户选择删 `C:\ProgramData\LanMouseShare` → 删安装目录
（`DeleteDirectoryWithRetry`：6 次 × 400ms，处理仍被占用的文件）→ 弹结果提示，
说明程序是否删净、配置是否保留、桌面上的传输文件没动。

每一步都包在 `TryRun(Action)` 里，重复卸载不会抛异常。

### 版本号为什么分成两个
- `SetupVersion = "0.2.2"`：纯数字、单调递增，只用于「本机装的包是否已经同等或更新」的判断，
  因为 `0.2.0-beta-fix13` 这种字符串 `Version.TryParse` 会失败，拿它比较会误判成"已最新"而跳过升级；
- `AppVersion = "0.2.0-beta-fix13"`：对外展示（「设置 → 应用」里的显示版本、`DisplayVersion`）。

注册表项同时写入 `DisplayVersion`、`SetupVersion`、`InstallDate`、`NoModify=1`、`NoRepair=1`、`HelpLink`。

---

## fix12：跨屏后鼠标侧键失效（2026-10-29 夜）

现象：鼠标移到对端之后，左/右/中键与键盘都正常，只有侧键（前进 XBUTTON1 / 后退 XBUTTON2）没反应。

根因是两头都只认左/右/中三个键：
- 采集端 `src/Input.Native/InputHook.cs` 的 `MouseCallback` 把 `WM_LBUTTON*/RBUTTON*/MBUTTON*`
  之外的一切硬编码成 `InputKinds.MouseButton`，且 `IsDown` 只在左/右/中按下时为 true。
  `WM_XBUTTONDOWN(0x020B)` / `WM_XBUTTONUP(0x020C)` 因此被当成"一个未知键的抬起事件"，
  具体按的是前进还是后退（在 `mouseData` 高位）也直接丢掉。
- 注入端 `src/Input.Native/InputInjector.cs` 的 `InjectMouseButton` 只映射左/右/中，
  其余 `flags` 保持 0，函数直接 `return Succeeded()`——于是"发了、但什么都没发生"，
  而且因为返回成功，上层连日志都不会报错。

修法：
1. `NativeMethods` 补 `WM_XBUTTONDOWN/WM_XBUTTONUP`、`MOUSEEVENTF_XDOWN/XUP`、`XBUTTON1/2`、`VK_XBUTTON1/2`。
2. 采集端把侧键纳入 `IsDown` 判定，并把 `mouseData >> 16`（哪一颗侧键）借 `InputPayload.Delta`
   一起转发——`Delta` 在 UDP 二进制通道与 JSON 通道里本来就序列化，协议不用改、新旧版本互不破坏。
3. 注入端支持 `MOUSEEVENTF_XDOWN/XUP`，`mouseData` 填 `XBUTTON1/XBUTTON2`。
4. `ReleaseAllMouseButtons` 补发两颗侧键的抬起，避免"按住侧键时切换控制权"导致对端键位卡住；
   `DesktopEnvironment.IsAnyMouseButtonPhysicallyDown` 也把侧键算进去。

回归：全解决方案 0 error（仅既有 CS0162/CS0067 警告）、`LanMouseShare.Tests.exe` → `All tests passed.`
侧键的真实效果需要两台机器实机验证（本机注入会真的触发资源管理器的前进/后退）。

## 🆕 fix11：提速 + 快捷方式传错 + 落地目录改桌面（2026-10-29 夜）

用户反馈三条：传输速度还能再快；传快捷方式时对面收到的是快捷方式指向的程序本体；
文件不要放在 `Mouse传输文件` 子目录里，直接放桌面。

### 1) 传输速度

三处放大 I/O 尺寸（`src/Desktop.Agent/FileLink/FileLinkProtocol.cs`）：
`BufferSize` 1MB → **4MB**（每 4MB 才一次 syscall 与一次进度回调），
新增 `SocketBufferSize = 1MB` 并在客户端/服务端连接建立后显式设置
`SendBufferSize` / `ReceiveBufferSize`（避免大块写入被当成慢速应用而降窗），
新增 `FileBufferSize = 1MB` 并给四处 `FileStream` 加 `FileOptions.SequentialScan`
（收发两端落盘都是顺序写，原来只有 64KB 缓冲）。
`NoDelay` 早就是开的，SHA256 仍在流式搬运里边读边算，不额外读一遍盘。

### 2) 传快捷方式却传了程序本体

原因在 Windows 的动词解析：菜单只挂在 `Software\Classes\*\shell` 时，Explorer 会先把
`.lnk` 解析成它的目标，再把"文件动词"套到目标上，于是右键 `LAN Mouse Share` 快捷方式
出来的 `%1` 是 `LanMouseShare.Agent.exe` 本体。
修法（`src/Desktop.Agent/ShellContextMenu.cs`）：额外注册
`Software\Classes\lnkfile\shell\LanMouseShareSend`，让快捷方式走自己的动词，
`%1` 就是 `.lnk` 路径，传过去也还是快捷方式。

### 3) 落地目录直接放桌面

`MainWindow.GetTransferDirectory()` 的候选顺序去掉 `UiText.TransferFolderName` 子目录：
当前用户桌面 → `%PUBLIC%\Desktop` → 公共桌面 → 下载 → LocalAppData → Temp 兜底。
文件名冲突仍由 `FileLinkClient.UniquePath` 加 ` (1)` 序号解决，所以不会覆盖桌面已有文件。

### 4) 回归验证

- 全解决方案编译 0 error；`LanMouseShare.Tests.exe` → `All tests passed.`
- `scripts/FileLinkSmoke.cs` 用新缓冲重跑：push/pull/文件夹打 zip/取消/未配对拒绝全部通过 → `SMOKE PASS`
- `scripts/SendChannelSmoke.cs`：三次 `--send` 转交聚合为一批 4 个路径 → `SEND CHANNEL PASS`

## 🆕 fix10：传文件改成右键直传 + 修键盘方向反了（2026-10-29 深夜）

用户的两条明确要求：
1. 传过来的文件一律放桌面；放弃"复制 → 对端粘贴"的方式，改成**选中文件 → 右键 → 传输到对端**，点一下就自动传到对方电脑。
2. 键盘方向反了：鼠标在哪台电脑上操作，键盘却必须在另一台上敲才有反应。

### 1) 右键"传输到对端"（替代复制粘贴传文件）

新增 `src/Desktop.Agent/ShellContextMenu.cs`：在 HKCU 注册
`Software\Classes\*\shell\LanMouseShareSend` 与 `Software\Classes\Directory\shell\LanMouseShareSend`，
菜单文字"传输到对端"，命令为 `"<Agent.exe>" --send "%1"`，`MultiSelectModel=Player`
（多选时 Windows 会逐项起进程）。注册是幂等的（值没变不写），失败只记日志。

新增 `src/Desktop.Agent/SendRequestChannel.cs`：Agent 是单实例程序，右键点出来的
第二个进程不能自己干活，于是用命名管道 `LanMouseShare.Agent.Send` 把路径转交给
正在运行的实例；服务端按 **700ms 去抖聚合**（多选时多次进程调用合成一批），
聚合完回调 `MainWindow.OnSendRequestBatch` → `SendFilesViaFileLink`。
管道不设自定义 ACL——同用户进程默认可互连，而自定义 ACL 会让非提权进程建不出实例（fix7 的坑）。

`App.xaml.cs`：解析 `--send <路径>`；抢不到单实例时 `SendRequestChannel.TrySend(paths)` 后退出；
抢到则把路径通过 `PendingSendPaths` 交给主窗口立即发送。

被"干掉"的旧链路：
- `ClipboardSyncService.OnDebounceTick` 的 `Clipboard.ContainsFileDropList()` 分支整段删除，
  遇到文件列表直接 `clipboard.files.ignored reason=disabled-fix10` 返回 —— 复制文件不再公告、
  不再写虚拟剪贴板。
- 删除界面上的「拉取并粘贴」按钮与 `OnPullPasteClicked`；
- 删除 `_fileLink.OfferReceived` 订阅与 `OnFileLinkOfferReceived`（预取+写剪贴板那套）；
- `TryInterceptLocalFilePaste` 直接 `return false`，Ctrl+V 不再被改写成跨机文件粘贴（文本/图片剪贴板不受影响）。

接收目录：`GetTransferDirectory()` 候选顺序改为**当前用户桌面优先**
（`Desktop\Mouse传输文件` → `%PUBLIC%\Desktop\Mouse传输文件` → 公共桌面 → 下载 → LocalAppData → Temp），
因为公共桌面需要管理员权限，写不进去就会掉到 Temp，用户就"找不到文件"。

### 2) 键盘方向反了

日志证据（A 端 fix9，`Agent_20261029-194039_12508.log`）：
```
19:41:35 control.link.begin ... direction=Left entry=0,0 localScreen=0,0,2880,1920
19:41:35 screen.switch.out ... entry=0,0 ...
19:41:35 control.link.release reason=LocalRequest wasOwner=1 notifyPeer=1
19:41:41 control.link.begin ... entry=-92,1172
19:41:42 screen.switch.back ... remotePoint=1919,903 ... reason=ControllerEdge
19:41:43 control.link.arbitrated ... owner=bff1e52a2c714af3b7c0224cb15b4b84 wasOwner=0 path=udp
19:41:44 control.link.role ... to=Target
```
`entry=0,0` 说明触发切换的输入事件本身没有坐标——就是**键盘事件**（`InputPayload.X/Y` 默认 0）。
三个根因：

1. `OnInputCaptured` 的 Idle 分支与 `HandleTargetLocalInput` 只按坐标做边缘判定，键盘/滚轮/按键
   被当成"指针在屏幕左上角"，于是敲一下键盘就可能把控制权交给对端（或被当成要切走）。
   → 两处都加 `if (e.Payload.Kind != InputKinds.MouseMove) return;`。
2. `TrackOwnerReturnPush` 一进入对端就开始累计"往返回边推"的量；Left 布局下进入边和返回边是同一条边，
   进入点离边只有 7px，几百毫秒内就 `screen.switch.back`，控制权来回弹。
   → 新增 `_ownerReturnArmed`：指针必须先真正走进屏幕 `EdgeArmDistance` 才允许累计返回推挤；
   `ActivateRemoteControl` 里复位。
3. 控制权交还后只有 400ms 冷却、80px 重臂距离，指针正停在相邻边上，一点抖动就又被判成"要切过去"。
   → `ArmEdgeSwitch` 增加 `(reason, cooldownMs, armDistance)` 重载；`ApplyPeerControlRelease` 用
   `EdgeSwitchCooldownAfterReleaseMs = 1200` / `EdgeArmDistanceAfterRelease = 140`，
   `ArmEdgeSwitchIfMovedAway` 判定成功后复位成默认 80。

### 3) 本机冒烟（scripts/SendChannelSmoke.cs）

单独编译 `SendRequestChannel.cs` + `SendChannelSmoke.cs` 跑了一遍管道转交与聚合：

```
client ok=True,True,True
batch count=4 -> C:\a\one.txt,C:\a\two.txt,C:\a\three.txt,C:\a\four.txt
batches=1 paths=4
SEND CHANNEL PASS
```

三次转交（模拟多选右键同时起三个进程）被合并成同一批 4 个路径，说明去抖聚合与
`--send` 转交链路可用。

## 🆕 fix9：四个"看得见但没反应"的问题（2026-10-29 晚）

这一轮不是架构问题，是 fix8 上线后用户实测暴露的四个具体毛病。全部按日志证据定位。

### 1) 进度条卡在 100% 不结束
`MainWindow.OnFileLinkProgress` 把 `Done = false` 写死了，进度面板永远收不到"完成"信号，
所以字节数到 100% 之后就停在那里。现在按 `DoneBytes >= TotalBytes` 判定完成，
并在按文件名换档时用 `(delta * 1000.0 / ms)` 算实时速度。

### 2) 文件"传过去了"但桌面上找不到
对端日志实测：`filelink.inbox.create.fail path=C:\Users\Public\Desktop\Mouse传输文件
| UnauthorizedAccessException` → 回退 `Path.GetTempPath()`，文件落进
`C:\Users\Administrator\AppData\Local\Temp\`，用户在桌面自然什么也看不到，
观感就是"卡在 100%"。
`GetTransferDirectory()` 改为**可写探测回退链**，并加静态缓存：

1. `%PUBLIC%\Desktop\Mouse传输文件`
2. `Environment.SpecialFolder.CommonDesktopDirectory\Mouse传输文件`
3. `SpecialFolder.DesktopDirectory\Mouse传输文件`
4. `%USERPROFILE%\Downloads\Mouse传输文件`
5. `%LOCALAPPDATA%\LanMouseShare\Mouse传输文件`

每个候选真建目录 + 写删 `.lms-write-probe` 验证可写，第一个成功的就记住（日志 `transfer.dir.ready path=`）。

### 3) "打开接收文件夹"和"取消传输"点了没反应
- 打开目录：原来 `CreateDirectory` 的异常被 catch 后只写日志，界面什么都不改。
  现在先解析目录 → `Process.Start("explorer.exe", "\"路径\"")`（失败再退回 `Process.Start(路径)`）
  → 状态栏显示真实路径，异常时把异常消息一起显示。
- 取消传输：原来只发旧的 `AgentCommands.CancelTransfers`，而 fix8 的通道根本不吃这条命令。
  新增真正的取消：`FileLinkHub.CancelActive()` → 每个 `FileLinkClient.Cancel()`（置位 + 掐断 socket）
  + `FileLinkServer.CancelActive()`（关掉所有在途连接），服务端 `HandlePush` 的失败分支会删掉 `.part`。

### 4) A 复制文件、B 粘贴还是什么都没发生
fix8 走的是 `VirtualFileClipboard` 延迟渲染：B 的剪贴板里放的是**A 的路径**，
Explorer 粘贴时会去读这些字节，实测日志里只有 `clipboard.files.probe cf=15 lindex=-1 tymed=1`
（Explorer 确实来探 CF_HDROP 了），但没有任何 pull/fetch 发生，于是粘贴静默失败。
现在改成**先落地再上剪贴板**：对端一发 `offer`，本机立刻在后台把文件拉完，
然后用真实本地路径 `PutFilesOnClipboardAsync(...)` 写 CF_HDROP。粘贴变成纯本地操作，
不再依赖延迟渲染。`PutFilesOnClipboard` 内部已有 `_applyingRemote` + 2 秒抑制窗，
不会把我们自己写的剪贴板再公告回对端形成回环。

### 5) 复制文件夹
`FileLinkServer.HandlePull` 原来只认 `File.Exists`，目录一律回 `对端文件已不存在。`。
现在遇到目录按需打包成 `lms-copy-<目录名>-<guid8>.zip` 放在 temp（发完即删），
客户端以对端给的文件名落盘，`ExpandIfArchive` 去掉末尾 8 位序号解回原目录名。

### 6) 收尾读掉 summary 帧
客户端发完最后一个文件就关连接，对端写 summary 时撞上
`IOException: 远程主机强迫关闭了一个现有的连接`，日志里一片红。
现在 `PushFiles` / `PullFile` 末尾都 `TryReadSummary(stream)`，异常只记 `filelink.tail.skip`。

### 本机冒烟测试（scripts/FileLinkSmoke.cs，端口 47551，不打扰在跑的正式实例）

```
server port=47551
push ok=True okCount=2 failCount=0
push received=2 hello.txt,blob.bin
pull path=...\pulled\pull-me.dat
clipboard sent=True received=text:粘粘粘
stranger ok=0 fail=1 error=设备不匹配（未配对）。
folder pull path=...\pulled\lms-copy-资料-fec67c0b.zip
folder zip entries=2 a.txt,sub/b.txt
cancel finished=True elapsedMs=232 pushOk=False fail=1 error=已取消传输
cancel leftover .part=0
SMOKE PASS
```

`FileLinkServer` / `FileLinkClient` 都新增了可选端口参数（默认 47541），冒烟测试才能和正式实例并存。

### 版本
0.2.0-beta-fix9。安装器的防火墙规则不变（TCP 47532/47540/47541，UDP 47531/47533/47534）。

---


## 🆕 fix8：文件传输与剪贴板整条链路换掉（2026-10-29）

### 为什么不再继续修旧链路
fix7 已经把"暂停共享吞掉文件命令""重启后暂停不同步""拒绝时不做流抽干""命名管道 ACL"都修了，
但旧链路的失败点本质上还是三处状态耦合：**暂停共享状态、控制租约、IPC 命名管道**。
文件和剪贴板这两个功能必须穿过 Service 的命令队列才能出网，队列里任何一处卡住，
Agent 侧看到的就只是"没反应"，而且没有任何地方能告诉用户是哪一环挂了。
所以 fix8 按用户要求**不再使用旧的 Service 中介文件通道**，重写为 Agent 与 Agent 之间的独立通道。

### 新架构
- 每个 Agent 自己监听 **TCP 47541**（`src/Desktop.Agent/FileLink/FileLinkServer.cs`），
  直连对端 Agent 的同一端口，全程不经过 Service，因此不受暂停共享、控制租约、管道占用影响。
- 门面 `FileLinkHub`（`src/Desktop.Agent/FileLink/FileLinkHub.cs`）对外只暴露四件事：
  推文件、发清单、按需拉单个文件、发剪贴板。
- 认人：`hello` 里带本机设备 ID 与随机数，接收端要求它等于状态里的 `PeerDeviceId`，
  不相等的连接直接拒绝（本机冒烟测试里 `stranger` 用例覆盖）。

### 协议：一条连接只做一件事（`FileLinkProtocol.cs`）
- 帧格式：4 字节小端长度 + UTF-8 JSON（`DataContractJsonSerializer`），上限 4 MB。
- `push`：文件清单 → 逐个文件 `accept` → 原始字节 → `done` → `summary`；
  对端拒绝某个文件只会让那一个失败，不会把字节写进流里造成帧错位（旧链路的老毛病）。
- `pull`：请求 → `file`（带真实长度与 SHA256）→ 字节 → `ack`；接收端先写 `<目标>.part`，
  校验通过再原子改名，失败删残片并如实回报原因。
- `offer`：A 复制文件时只发清单，B 真正粘贴时才逐个 `pull`——大文件不会因为"复制"就灌满网络。
- `clipboard`：文本与图片 PNG 走同一条通道，避免再受 Service 命令队列影响。

### 界面与落地
- 拖拽、"发送文件"按钮、Ctrl+V 粘贴、"拉取并粘贴"按钮全部改走新通道。
- 落地目录仍是 `公共桌面\Mouse传输文件`；重名自动加 ` (1)`；文件夹传输时自动打包 zip，
  落地后自动解回文件夹。
- 失败可见：`PushCompleted` 会带 `ok/fail/error`，界面显示"文件传输失败：<原因>"，
  并在提示行说明需要确认对端已装同一版本、且 47541 未被防火墙拦截。

### 本机冒烟测试（不用第二台机器）
`scripts/FileLinkSmoke.cs` 直接拉起服务端 + 客户端，验证：
推送 2 个文件（含 3 MB 随机二进制）→ 逐字节比对拉回的文件 → 文本剪贴板 → 未配对连接被拒。
实测输出：

```
server port=47541
push ok=True okCount=2 failCount=0 error=
push received=2 hello.txt,blob.bin
pull path=...\pulled\pull-me.dat error=
clipboard sent=True received=text:粘粘粘 fix8
stranger ok=0 fail=0 error=设备不匹配（未配对）。
SMOKE PASS
```

### 安装器
防火墙规则补上 47541：`TCP localport=47532,47540,47541`（`src/Setup/Program.cs`）。

## fix7 历史记录

## 🆕 fix7 新增修复（2026-10-29）——"文件传不了 / 跨机粘贴文件没反应"

### 1. 根因：一次"暂停共享"把文件与剪贴板一起废掉，而且重启后永远等不到恢复
**现象**：拖拽文件传输失败（发送端只有 `IOException: 无法将数据写入传输连接: 远程主机强迫关闭了一个现有的连接`），A 机复制文件到 B 机粘贴时也传不动，界面没有任何提示。
**根因（双机日志实证）**：
- Agent 退出时 `ShutdownAndDispose()` → `StopControlEverywhere(true)` 会给对端发一次 `ControlPause{StopAll}`；对端 Service 于是 `Paused=True Reason=PeerPause`。
- 重启后的新 Agent 并不知道自己上一世暂停过谁，不会补发 `ControlResume`，对端就永久停在暂停状态。
- 而旧实现的暂停门禁覆盖面过大：`PeerCoordinator.HandleCommand` 会丢弃 `SendClipboard / RequestClipboardFiles / RequestClipboardPaste / SendFiles`，`HandleEnvelope` 丢弃 `FileOffer`，`IsCrossScreenEnvelope` 里还包含剪贴板与文件类型；Agent 侧 `ClipboardSyncService._isEnabled` 又带上了 `!_session.IsPaused`，连"对端公告了文件"都收不到 → 拖拽与粘贴两个症状同一个根因。
- 被暂态拒绝的那次传输还留下了脏数据：接收端因 `!paused` 跳过 `ReadIncomingFileStream`，文件字节留在 TCP 流里，下一次 `TryReadFrame` 把文件内容当长度前缀解析 → `InvalidDataException: Invalid TCP frame length`，整条连接报废（日志实证）。
**修复**：
- 暂停拆成两路：`_localPaused`（本机用户暂停）/ `_peerPaused`（对端通知），`SharingPaused = 两者或`；`SetSharingPaused` 按 reason 前缀分流，日志同时打印 Local/Peer/Effective。
- **暂停只拦输入与控制**：`HandleCommand` 门禁只留 Acquire/ReleaseControl、SetControlTarget、SendInput(Batch)、ControlLease(Ack)；`SendFiles`、`SendClipboard`、`RequestClipboardFiles/Paste` 与 `FileOffer` 不再受暂停影响；两侧 `IsCrossScreenEnvelope` 只保留输入/控制类型。
- 暂停状态可自愈：`HeartbeatPayload` 增加 `Paused`（旧版本不发该字段，保持兼容），每次心跳双向校准；心跳循环里对端不可达时 `DropStalePeerPause()` 自动解除对端暂停；`AnnouncePauseStateToPeer()` 在对端更换或本机暂停状态变化时补发一次。
- Agent 启动后主动发一次 `AgentCommands.ResumeControl`（最多重试 5 次），清掉上一会话遗留的暂停并把恢复同步给对端。
- 本机自己也暂停时，对端的 `ControlResume` 只解除"对端暂停"，不清掉界面上的"已暂停"。
- `ReadIncomingFileStream` 重写为"无论如何都把 `start.Length` 个字节读掉"：没有对应 FileOffer / 传输已被取消时进入 drain 模式，只读不写，结束后删掉 `.part` 并记 `file.transfer.rejected` / `file.transfer.drained`。

### 2. IPC 命名管道只跑得起 1 个实例（日志洪水 + 命令被静默丢弃）
**现象**：本机与对端 Service 日志各有 1200~3900 条 `IPC listener error. | UnauthorizedAccessException: 对路径的访问被拒绝。`，4 个监听线程里稳定 3 个每 100~200ms 失败一次。
**根因**：`NamedPipeCommandServer.CreatePipeSecurity()` 给 AuthenticatedUser 只授 `PipeAccessRights.ReadWrite`，不含 `FILE_CREATE_PIPE_INSTANCE(0x0004)`。非提权的 Service 因此只能建出第 1 个管道实例（其余 3 个 `new NamedPipeServerStream` 直接抛异常）→ 实际并发度 1，Agent 的并发命令会因"所有管道实例均忙碌"被丢弃，`RequestClipboardFiles` 这类命令丢得毫无痕迹。
**验证**：`scripts/PipeProbe.exe`（4 线程建 4 实例）——旧 ACL `created=1 failed=3`（全是 UnauthorizedAccessException），新 ACL `created=4 failed=0`。
**修复**：AuthenticatedUser 增加 `CreateNewInstance`，并把当前用户 `SetOwner` + FullControl；监听循环的异常日志加节流（前 3 次全量，之后每 60 秒一条并带累计次数）。

### 3. 失败不再假装成功
Agent 收到 `FileComplete{Success=false}` 或没有 `SavedPath` 时，不再显示"文件传输完成 已保存到桌面"，改为显示"文件传输失败：…"。

### 4. 版本
0.2.0-beta-fix7（Setup 仍为 0.2.1）。


## 🆕 fix6 新增修复（2026-10-29）

### 1. 粘贴不传输：剪贴板格式 ID 符号错位 + GetDataHere 未实现（fix5 装机实测暴露）
**现象**: fix5 后右键"粘贴"不灰了，但点粘贴没有任何传输（双机日志：发布成功、零 fetch/零 pull）。
**根因**: ① `FORMATETC.cfFormat` 是带符号 short，而 `RegisterClipboardFormat` 返回 0xC000-0xFFFF 区间的正 int；`GetData`/`QueryGetData` 拿负数比较正数格式 ID 恒为 false → 所有格式都被拒（QueryGetData 恒返回 DV_E_FORMATETC、GetData 恒抛"Unsupported clipboard format"）→ Explorer 拿不到任何数据。② Explorer 读取 FileContents 走 `GetDataHere`，旧实现恒抛未实现异常。③ `AllocHGlobal` 用 GMEM_MOVEABLE 分配但未 GlobalLock 就 Marshal.Copy，直接写坏全局堆 → Agent 进程崩溃隐患（本机复现程序 100% 复现进程死亡）。
**修复**: ① 格式 ID 比较统一归一化（负数 +65536）；② 完整实现 `GetDataHere`（HGLOBAL 与 ISTREAM 两分支，Explorer 真实粘贴路径）；③ HGLOBAL 分配改 GMEM_FIXED；④ Contents 的 lindex<0 请求直接拒绝——OLE flush/批量渲染会以 lindex=-1 请求内容，拒绝它才能保证"粘贴时才传输"不被 flush 触发（本机直调测试全部通过：QueryGetData/GetData/GetDataHere/惰性流均按预期）。

### 2. "打开接收目录"按钮路径与实际接收目录不一致
**现象**: 拖动传输实际保存在 C:\Users\Public\Desktop\Mouse传输文件，界面"打开"按钮却打开 C:\Users\Administrator\Desktop\Mouse传输文件。
**根因**: Agent 用 `Environment.SpecialFolder.DesktopDirectory`（当前用户桌面），而服务端 `PeerCoordinator.ResolveIncomingFolder()` 用公共桌面 `%PUBLIC%\Desktop`。
**修复**: Agent 的 `GetTransferDirectory()` 改为与服务端相同的公共桌面解析逻辑。

### 3. 版本
0.2.0-beta-fix6（Setup 仍为 0.2.1，升级判定用纯数字版本）。

## 🆕 fix6 新增修复（2026-10-29）

### 1. 粘贴不传输：剪贴板格式 ID 符号错位 + GetDataHere 未实现（fix5 装机实测暴露）
**根因**: ① `FORMATETC.cfFormat` 是带符号 short（如 -16172），而 `RegisterClipboardFormat` 返回 0xC000-0xFFFF 区间的正 int（如 49364）——GetData/QueryGetData 拿负数比正数恒 false，所有格式都被拒（QueryGetData 恒返回 DV_E_FORMATETC、GetData 恒抛 Unsupported clipboard format）→ Explorer 拿不到内容 → 粘贴静默失败。② Explorer 读 FileContents 的标准路径是 GetDataHere，旧实现恒抛。③ AllocHGlobal 用 GMEM_MOVEABLE 却未 GlobalLock 就 Marshal.Copy，写坏全局堆 → Agent 进程崩溃隐患（本地复现程序 100% 复现进程死亡；此前被符号 bug 掩护从未执行）。
**修复**: ① NormalizeCf 归一化（负值+65536）应用于 GetData/QueryGetData 全部格式比较；② 完整实现 GetDataHere（HGLOBAL 与 ISTREAM 两分支）；③ AllocHGlobal 改 GMEM_FIXED；④ Contents 的 lindex<0 请求（OLE flush/批量渲染）直接拒绝，保证"粘贴时才传输"不被 flush 触发（本地反射直调测试全绿：QueryGetData/GetData/GetDataHere/惰性流全部按预期）。

### 2. "打开接收目录"按钮与实际接收目录不一致
**根因**: Agent 界面用 `Environment.SpecialFolder.DesktopDirectory`（当前用户桌面 = Administrator），而服务端 `PeerCoordinator.ResolveIncomingFolder()` 用公共桌面 `%PUBLIC%\Desktop`（实际保存处 = Public）。
**修复**: Agent 的 `GetTransferDirectory()` 改为与服务端相同的公共桌面三级回退逻辑。

## 🆕 fix5 新增修复（2026-10-29）

### 1. 虚拟剪贴板发布失败 0x800401F0 → 粘贴仍然灰色（fix4 装机实测暴露）
**根因**: fix4 把剪贴板数据对象托管到 MTA 线程（`CoInitializeEx(COINIT_MULTITHREADED)`），但 `OleSetClipboard` 要求调用线程经 **OleInitialize（STA）** 初始化——MTA 线程上 `OleGetClipboard` 正常、`OleSetClipboard` 恒返回 `CO_E_NOTINITIALIZED 0x800401F0`（双机日志 `clipboard.files.virtual.failed hr=0x800401F0` 实证；本地最小复现程序锁定：STA+OleInitialize+Set(NULL)=S_OK，MTA+CoInitializeEx+Set(NULL)=0x800401F0）。发布失败后回退懒提示，剪贴板仍为空 → 右键"粘贴"还是灰的。
**修复**: 发布通道改进程级长驻 **STA 线程 + 消息泵**（`OleInitialize` + PeekMessage/DispatchMessage 循环）——对象创建与 OleSetClipboard 都在这条 STA 上执行；Explorer 等粘贴方读剪贴板时 GetData 回调被 COM 封送回该 STA 由泵分发，拉取大文件只阻塞这条线程，UI 与输入钩子保持响应。另把单文件拉取超时从 35s 提到 10 分钟（实测大文件单文件 69s，35s 会粘贴一半报错）。

### 2. 边角阻挡（fix4 已修，本轮实测确认）
**确认**: fix4 的 `TrackOwnerReturnPush` 边角检查在双机实测中生效（主控端日志出现连续 `edge.return.corner.blocked`，B 屏左下角推回被挡、屏幕中段正常放行），本轮未再改动。

## fix4 新增修复（2026-10-29）

### 1. 文件传输"只有第一次成功、之后粘贴是灰色的"
**根因**: 懒模式下收到对端文件公告后只登记清单、剪贴板刻意留空（`VirtualClipboardEnabled=false` 被禁用），Explorer 右键"粘贴"因剪贴板没有文件而置灰；只有物理 Ctrl+V 恰好在 Idle 态被拦截的那一次能触发拉取（主控端键盘粘贴还依赖本机剪贴板恰有文件，中间复制过文本就静默失效）。
**修复**: 重新启用延迟渲染虚拟剪贴板——对端复制后本机剪贴板立即出现文件名（右键粘贴不再灰、任何姿势都能粘贴），字节在粘贴时才拉取（严格"粘贴时才传输"，双向一致）。同时加固 `VirtualFileClipboard`：对象创建与发布移到进程级长驻 MTA 线程（GetData 回调跑 MTA 工作线程，拉取大文件不再卡死界面与输入钩子）；FileContents 改惰性 IStream（枚举只列 ISTREAM，本机随后新复制时 OleFlushClipboard 只拿流指针、不触发整批渲染；Win7 等要 HGLOBAL 的调用方粘贴时现取）；OleSetClipboard 对 CLIPBRD_E_CANT_OPEN 重试；多文件枚举补全全部 lindex；并发拉取加锁防双拉。Ctrl+V 拦截链路保留为快路径。

### 2. 边角阻挡仍然失败（B→A 方向）
**根因**: 切出路径（`EdgeSwitchDetector.ShouldSwitch` 带边角阻挡）正常=A→B 生效；但返回路径走主控端 `TrackOwnerReturnPush` 快路径（先于对端注入指针检测器释放控制），该函数此前没有任何边角阻挡检查 → 在 B 屏边角推回照样放行。
**修复**: `TrackOwnerReturnPush` 在累计推挤前加边角判定——复用 `EdgeSwitchDetector.IsInBlockedCorner`（对端屏幕坐标系 + 反方向），落在边角带时重置累计并拒绝返回，B→A 与 A→B 行为一致。

---

## 🆕 fix3 新增修复（2026-10-29）

### 1. 剪贴板文件传输：复制后不再立即传输
**根因**: `MainWindow.OnRemoteFilesPending` 在虚拟剪贴板不可用（`VirtualClipboardEnabled=false`）时回退到 `BeginPasteFiles(e.FilePaths, false)`，收到对端文件公告就立即预取全部文件。
**修复**: 删除预取回退，TCP 路径与 UDP 路径一致——只登记文件清单并提示"对端已复制 N 个文件，可按 Ctrl+V 或点'拉取并粘贴'"，粘贴时才触发传输（双向一致）。

### 2. 开机自动启动失效
**根因**: ① Agent 以管理员运行时走计划任务路径，`schtasks /Create` 不带 `/IT`，在 Win7 上创建 ONLOGON 任务必须密码，失败后 Run 键已被提前删除=启动项完全丢失；② 启动时加载设置后从不重放启动项注册，陈旧配置无法自愈。
**修复**: 计划任务加 `/IT`（免密、兼容 Win7/Win10）；改为"先建任务、成功后再删 Run 键，失败自动回退写 Run 键"；Agent 加载设置后后台重放一次启动项注册。

### 3. 边角阻挡 A→B 生效、B→A 失效
**根因**: `BlockScreenCorners` 是每台机器各自 config.json 里的值（`UpdateSettings` 只存本机、不同步对端），A 勾选、B 未勾选 → B 出边不阻挡。
**修复**: `LayoutConfig` 增加 `BlockScreenCorners`（可空），布局同步协议透传该字段；任一端勾选/取消都会同步到对端（最后写者胜，对端 Agent 2 秒状态轮询收敛）。

---

## 📋 需要修复的三个问题（fix2 时期）

### 1. ❌ 剪贴板文件传输方式错误
**问题描述**: 
- 当前：复制文件后立即传输，需要等待传输完成才能粘贴
- 期望：复制文件后在对端粘贴时才开始传输

**同时发现**: 文本和图片共享也失效了

### 2. ❌ 开机自启动不是静默启动
**问题描述**: 
- 开机启动时会显示主窗口
- 需要改成静默启动到托盘

### 3. ❌ 边角阻挡功能失效
**问题描述**: 
- 启用"跨屏边角阻挡"后不生效
- 仍然可以从角落切屏

---

## ✅ 修复方案

### 修复 1：剪贴板检测顺序和文件传输
**文件**: `src\Desktop.Agent\ClipboardSyncService.cs`

**根本原因**: 
- 检测顺序错误：文件检测在文本之前
- Windows 复制文件时同时设置文本格式，导致文本被忽略

**解决方案**:
改变检测顺序为：**文本 → 图片 → 文件**

```csharp
// 旧顺序（错误）
if (Clipboard.ContainsFileDropList()) { ... }
else if (Clipboard.ContainsText()) { ... }
else if (Clipboard.ContainsImage()) { ... }

// 新顺序（正确）
if (Clipboard.ContainsText()) { ... }
else if (Clipboard.ContainsImage()) { ... }
else if (Clipboard.ContainsFileDropList()) { ... }
```

**详细修改**: 见 `docs\MANUAL-FIX-GUIDE.md` 第一部分

---

### 修复 2：开机静默启动
**文件**: 
- `src\Desktop.Agent\StartupTaskManager.cs` - 添加 `/minimized` 参数
- `src\Desktop.Agent\App.xaml.cs` - 处理命令行参数
- `src\Desktop.Agent\MainWindow.xaml.cs` - 启动时最小化窗口

**解决方案**:
1. 任务计划和注册表启动项添加 `/minimized` 参数
2. App.xaml.cs 检测参数并设置 `StartMinimized` 属性
3. MainWindow 启动时检查属性，如果为 true 则隐藏窗口

**状态**: ✅ 已自动修复（运行 `Apply-Fixes.ps1`）

---

### 修复 3：边角阻挡功能
**文件**: `src\Input.Native\EdgeSwitchDetector.cs`

**状态**: ✅ 代码本身正确，无需修改

**验证方法**:
- 检查配置文件 `C:\ProgramData\LanMouseShare\config.json`
- 确保 `BlockScreenCorners: true`
- 重启 Agent 使配置生效

---

## 🚀 快速应用修复

### 方法 A：自动化（推荐）

```powershell
cd "C:\Users\yangyang\Desktop\局域网键盘鼠标共享"

# 1. 应用自动修复
.\scripts\Apply-Fixes.ps1

# 2. 手动修复剪贴板代码（见 docs\MANUAL-FIX-GUIDE.md）

# 3. 编译和打包
.\scripts\Quick-Build.ps1

# 4. 打开输出目录
explorer "dist\LanMouseShare-0.2.0-beta-fix1"
```

### 方法 B：手动修复

详细步骤见: `docs\MANUAL-FIX-GUIDE.md`

---

## 📦 编译和打包

### 使用自动化脚本
```powershell
.\scripts\Quick-Build.ps1
```

输出位置: `dist\LanMouseShare-0.2.0-beta-fix1\`

### 手动编译
1. 打开 Visual Studio
2. 加载 `LanMouseShare.sln`
3. 选择 **Release** 配置
4. **生成解决方案**

---

## 📥 安装测试版本

### 两台机器都需要安装

```powershell
# 1. 停止旧版本
taskkill /F /IM LanMouseShare.Agent.exe
net stop LanMouseShare

# 2. 备份（推荐）
Move-Item "C:\Program Files\LanMouseShare" "C:\Program Files\LanMouseShare-backup" -Force

# 3. 安装新版本
$source = "你的解压路径\LanMouseShare-0.2.0-beta-fix1"
Copy-Item "$source\Agent\*" "C:\Program Files\LanMouseShare\Agent\" -Recurse -Force
Copy-Item "$source\Service\*" "C:\Program Files\LanMouseShare\Service\" -Recurse -Force

# 4. 启动
net start LanMouseShare
Start-Process "C:\Program Files\LanMouseShare\Agent\LanMouseShare.Agent.exe"
```

---

## 🧪 测试修复结果

### 测试 1：文本共享
```
1. 电脑 A：复制一段文本
2. 电脑 B：Ctrl+V 粘贴
3. ✓ 文本应该立即出现
```

### 测试 2：图片共享
```
1. 电脑 A：截图（Win+Shift+S 或 Ctrl+C）
2. 电脑 B：在画图中 Ctrl+V
3. ✓ 图片应该立即出现
```

### 测试 3：文件传输
```
1. 电脑 A：复制一个文件
2. 电脑 B：Ctrl+V 粘贴
3. ✓ 文件应该开始传输并粘贴
```

### 测试 4：静默启动
```
1. 设置开机自启动
2. 注销并重新登录（或重启）
3. ✓ Agent 应该在托盘，不显示窗口
```

### 测试 5：边角阻挡
```
1. 启用"跨屏边角阻挡"
2. 从屏幕角落（80px 内）尝试切屏
3. ✓ 应该被阻止
4. 从屏幕中间切屏
5. ✓ 应该正常工作
```

---

## 📁 生成的文件清单

### 脚本文件
- ✅ `scripts\Apply-Fixes.ps1` - 自动应用修复
- ✅ `scripts\Quick-Build.ps1` - 快速编译和打包

### 文档文件
- ✅ `docs\MANUAL-FIX-GUIDE.md` - 详细手动修复指南
- ✅ `patches\0.2.0-beta-fix1\PATCH-NOTES.md` - 补丁说明

### 输出文件（编译后）
```
dist\LanMouseShare-0.2.0-beta-fix1\
├── LanMouseShareSetup.exe             # 单文件安装程序（内嵌 payload.zip）
├── LanMouseShare-0.2.0-beta-fix1-source.zip
├── source\                            # 完整源码（与本包同版本）
├── Agent\                             # 桌面客户端
├── Service\                           # 后台服务
├── Scripts\                           # 测试脚本
├── Docs\                              # 文档
├── README.md / README.txt             # 安装说明
└── PROJECT_GUIDE.md
```

---

## ⚠️ 注意事项

1. **剪贴板修复已自动完成**
   - `ClipboardSyncService.OnClipboardChanged` 已改为「文本 → 图片 → 文件」顺序
   - 文件分支设置 `IsLazy = true`，粘贴时才真正传输
   - `docs\MANUAL-FIX-GUIDE.md` 可留作核对参考

2. **两台机器都需要更新**
   - 主控端和被控端都要安装新版本
   - 确保版本一致

3. **测试前先备份**
   - 保留旧版本以便回滚
   - 配置文件会自动保留

4. **边角阻挡可能需要调整参数**
   - 默认角落阻挡区域：80px
   - 可在配置中调整

5. **0.2.0 新控制器架构暂不参与编译**
   - `ControllerIntegration.cs` 和 `Controllers\*.cs` 依赖尚未实现的
     `InputInjector.InjectMouseMove/InjectKey/...` 与 `AppLog.Info(category, message)` 重载
   - 主程序（`MainWindow.xaml.cs` / `App.xaml.cs`）没有任何调用点
   - 已在 `Desktop.Agent.csproj` 中以注释排除，源文件保留，重构完成后再启用

---

## 🆘 如果遇到问题

### 编译错误
- 检查是否安装了 Visual Studio 或 Build Tools
- 确保 .NET Framework 4.7.2+ 已安装
- 查看错误日志

### 运行时错误
1. 查看日志：`C:\ProgramData\LanMouseShare\Logs\live\`
2. 回滚到备份版本
3. 重新检查修改的代码

### 功能不生效
1. 确认两台机器都已更新
2. 重启 Agent 和 Service
3. 检查配置文件是否正确
4. 查看日志中的错误信息

---

## 📞 下一步

### 已完成的构建（2026-10-27）
1. ✅ 运行 `scripts\Apply-Fixes.ps1`（需 UTF-8 BOM，已为 scripts\ 下所有 .ps1 补上）
2. ✅ 剪贴板 / 静默启动 / 边角阻挡代码核对完毕
3. ✅ `scripts\Quick-Build.ps1` 修复后编译通过（Release，0 错误）
4. ✅ `scripts\Build-Installer.ps1 -Version 0.2.0-beta-fix1` 生成单文件安装 exe
5. ✅ 包内含 `source\` 完整源码目录 + source.zip

### 还需要在真机上做
1. ⏳ 两台机器都安装 `LanMouseShareSetup.exe`
2. ⏳ 验证剪贴板文本 / 图片 / 文件（懒加载）行为
3. ⏳ 验证重启后静默启动到托盘
4. ⏳ 验证屏幕四角阻挡
5. ⏳ 收集测试反馈，调整参数

### 后续工作
1. 补完 `Controllers\*.cs` 新架构（InputInjector / AppLog API），再把 csproj 里的注释打开
2. 准备正式发布版本

---

**版本**: 0.2.0-beta-fix1  
**创建日期**: 2026-10-27  
**状态**: 待测试
