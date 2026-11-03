# 主控 / 被控端 状态关系书（Control State Model）

版本：0.1.47（0.1.45 引入控制租约状态机；0.1.47 增加 Service 中继通道，本文档是规范来源，代码必须逐条对应）
适用范围：LanMouseShare 两台已配对设备之间的跨屏键鼠控制。
文档性质：**规范**。实现与本文档冲突时，以本文档为准；修改行为必须先改本文档。

---

## 1. 术语

| 术语 | 含义 |
| --- | --- |
| 主控端（Owner / 控制方） | 当前把自己的物理键鼠转发给对方的那台机器。同一时刻全局最多一台。 |
| 被控端（Target / 接收方） | 当前接收并注入对方键鼠事件的那台机器。 |
| 空闲（Idle） | 两台机器都不跨屏，各自使用自己的物理键鼠。 |
| 世代号 epoch | 单调递增的控制权版本号。每次产生新的控制权都 +1，旧版本的控制消息一律作废。 |
| 租约 lease | 主控端周期性（50 ms）发给被控端的“我仍在控制你”的声明，带 epoch 与 TTL。 |
| 确认 ack | 被控端收到租约后回给主控端的“我已收到并接受”回执。 |
| 方向 direction | 布局方向（Left / Right / Up / Down），表示对端屏幕在本机的哪一侧。 |

单台机器有三个角色状态：`Idle`、`Owner`、`Target`。**任何时刻一台机器只能处于其中一个状态。**

---

## 2. 不变量（Invariants）

这些不变量是稳定性的根基，任何实现都不得破坏。I1～I3 是硬约束，I4～I8 是派生约束。

- **I1 单一主控**：同一时刻全局最多一台机器处于 `Owner`。两台机器若同时想成为 `Owner`，必须按第 5 节仲裁规则在有限步内收敛到一台。
- **I2 有界抑制**：`Owner` 抑制本机物理输入（`SuppressLocalInput`）是允许的，但抑制**必须**能被证明仍然有效；一旦 700 ms 内没有收到对端 ack，必须立刻解除抑制并回到 `Idle`。**永远不允许出现“没有主控、但本机输入被吞掉”的状态。**
- **I3 有界注入**：`Target` 只有在 600 ms 内收到过当前 epoch 的有效租约时才注入远端输入；租约过期立即停止注入（本地输入本来就不受影响，所以不会卡死）。
- **I4 被控端永不自封主控**：`Target` 不能因为光标退回到自己屏幕边缘就变成 `Owner`。退回只允许**请求**主控端释放（`ControlRelease`），由主控端决定何时释放、光标落在哪里。
- **I5 单写者**：Agent（UI 进程）是控制状态的唯一写者；Service 只负责传输与上报，不自行改写 `Owner/Target`。Agent 每次状态变化后通过 `SetControlTarget` 把结果镜像给 Service，供界面与日志显示。
- **I6 世代单调**：所有控制消息都携带 epoch。接收方只接受 `epoch >= 当前 epoch` 的消息；`epoch` 更小的消息直接丢弃并计数（`staleMessages`）。新控制权的 epoch 一律取 `max(本机已知最大 epoch, 对端已知最大 epoch) + 1`。
- **I7 确定性裁决**：仲裁比较键为 `(epoch, ownerDeviceId)`，按 epoch 大者优先、epoch 相同则 `ownerDeviceId` 字符串序（Ordinal）大者优先。两台机器使用同一规则，因此即使同时发起控制也能收敛。
- **I8 无状态残留**：任何一次 `Release` 都必须清空：远端虚拟坐标、待发送队列、鼠标按键按住状态、边缘切换武装状态（`arm=false`，需离开边缘带 ≥12 px 后重新武装）、`remoteReturnReleasePending`。不允许旧状态在下一轮控制中被继承。

---

## 3. 状态机

### 3.1 状态定义

| 状态 | 语义 | 本机物理输入 | 远端输入注入 |
| --- | --- | --- | --- |
| `Idle` | 不跨屏 | 全部放行 | 不注入 |
| `Owner` | 我在控制对端 | 全部抑制（键盘+鼠标+滚轮） | 不注入（对端负责注入） |
| `Target` | 对端在控制我 | 全部放行（本地输入始终可用） | 仅当租约有效时注入 |

### 3.2 状态迁移表

迁移必须由下表列出的事件触发；未列出的组合一律忽略。

| 当前状态 | 事件 | 守卫条件 | 迁移后 | 动作 |
| --- | --- | --- | --- | --- |
| `Idle` | 本机光标到达布局边缘并继续向外移动 | 已配对已连接、非暂停、边缘切换已武装、租约通道可用 | `Owner` | epoch+1；立即发租约；`AcquireControl`；抑制本机输入；把首次坐标转发给对端；日志 `screen.switch.out` |
| `Idle` | 收到对端租约 | 非暂停、租约 epoch ≥ 本地已知 epoch | `Target` | 记录 `lastLeaseSeen`；开始按租约注入；日志 `control.link.role` |
| `Owner` | 每次 tick（50 ms） | 仍为 `Owner` | `Owner` | 发送租约（epoch、ownerId、TTL 1200 ms、seq+1） |
| `Owner` | 收到对端 ack | ack.epoch == 当前 epoch | `Owner` | 更新 `lastAckSeen` |
| `Owner` | `now - max(lastAckSeen, claimTime) > 700 ms` | — | `Idle` | `Release(AckTimeout)`；恢复本机输入；日志 WARN `control.link.timeout reason=AckTimeout` |
| `Owner` | 收到对端更强的租约 | `(theirEpoch, theirOwner)` 大于 `(myEpoch, myOwner)` | `Target` | `Release(Arbitrated)`；采纳对端 epoch/owner；日志 `control.link.arbitrated` |
| `Owner` | 收到 `ControlRelease`（对端请求退回） | — | `Idle` | `ReleaseControlToLocalEdge`：按对端坐标映射回本机边缘、`arm=false`；日志 `screen.switch.back` |
| `Owner` | 本机热键 `Ctrl+F10` / `Ctrl+F11` | — | `Idle` | `StopControlEverywhere`；向对端发 `ControlPause{StopAll:true}` |
| `Target` | 每次 tick | — | `Target` | 无（不回发租约） |
| `Target` | 收到租约 | epoch ≥ 当前 epoch | `Target` | 刷新 `lastLeaseSeen`；回 ack（每 50 ms 最多一次） |
| `Target` | `now - lastLeaseSeen > 600 ms` | — | `Idle` | `Release(LeaseExpired)`；日志 WARN `control.link.timeout reason=LeaseExpired` |
| `Target` | 收到 `ControlRelease` | — | `Idle` | 清理注入状态；日志 `control.link.released` |
| `Target` | 本机光标在本机屏幕朝对端方向的边缘继续外移 | — | `Idle` | `Release(TargetRequestedRelease)`，**立即停止注入**；发 `ControlRelease` 请求；日志 `screen.switch.return`。主控端收到请求后走 `ReleaseControlToLocalEdge` 把光标放回本机边缘；请求丢失时主控端 700 ms 后按 `AckTimeout` 自行回收 |
| 任意 | 暂停（热键或对端 `ControlPause`） | — | `Idle` | `Release(Paused)`；清空一切残留 |
| 任意 | 断线 / 未配对 / 配对不匹配 | — | `Idle` | `Release(Disconnected)`；日志 `control.link.disconnected` |
| 任意 | 注入失败达阈值 / 已知冲突窗口 | — | `Idle` | `Release(InjectionBlocked)`；向对端发 `ControlRelease` |

### 3.3 抑制与注入的判定（唯一判定入口）

```
ShouldSuppressLocalInput := (role == Owner) && !paused
ShouldInjectRemoteInput  := (role == Target) && !paused && (now - lastLeaseSeen) <= 600ms && lease.epoch == currentEpoch
```

除这两条以外，**任何代码都不得单独抑制本机输入或单独注入远端输入**。0.1.44 及之前的卡死，根因就是这条判定存在多个来源（`_remoteControlActive`、`_controlledByPeer`、`_session.IsTargetPeer`、Service 的 `ActiveTargetDeviceId` 各自为政）。

---

## 4. 消息与通道

| 消息 | 载体 | 频率 | 内容 | 作用 |
| --- | --- | --- | --- | --- |
| `LEASE` | UDP 47534（agent↔agent，直连） | 50 ms | `epoch, ownerDeviceId, ttlMs=1200, seq` | 声明控制权并证明主控端活着 |
| `ACK` | UDP 47534 | 收到租约时，最多每 50 ms 一次 | `epoch` | 证明被控端收到并接受 |
| `LEASE` / `ACK`（中继） | Agent 管道 → 本机 Service → 对端 Service（TCP 47532 / UDP 47533）→ 对端 Agent 的 `PollEvents` | 租约 100 ms；ack 收到即发 | 同上，JSON：`ControlLeasePayload` / `ControlLeaseAckPayload` | UDP 直连被防火墙阻断时的兜底，保证租约/ack 仍能跑完一个来回 |
| `ControlAcquire` | Service 管道 → TCP 47532 | 进入 `Owner` 时 1 次 | 现有 payload | 通知 Service/对端“我开始控制” |
| `ControlTargetChanged` | 同上 | 状态变化时 | `ActiveTargetDeviceId, Reason` | 供界面显示与日志 |
| `ControlRelease` | 同上 | 退回/释放时 | `RemoteX/Y, 屏幕尺寸, Direction, Reason` | 主控端告知被控端“我要把你光标放回哪” |
| `ControlPause` / `ControlResume` | 同上 | 热键 | `StopAll, Reason` | 全局暂停 |

说明：
- 租约有两条路径，语义相同、幂等应用：**UDP 47534 直连**（快路径，毫秒级）与 **Service 中继**（`SendControlLease` / `SendControlLeaseAck` → 对端 `PollEvents`）。中继复用已配对的 TCP/UDP 服务链路与既有防火墙规则，不需要再开端口；代价是单程约 50～100 ms。
- 700 ms 的 ack 预算按“中继 + 50 ms 事件轮询”的最坏一个来回留了余量；重复/迟到的租约与 ack 由 epoch 幂等丢弃。
- 键盘鼠标仍优先走 UDP 直连，避免受 Service 的 TCP 短连接（每条报文新建连接，500 ms 超时）拖累；Service 通道承载状态镜像、释放语义与中继租约。
- UDP 承载格式升级为 v3：`magic(4) + version(1) + type(1)`；`type=0` 输入批次（兼容 v2），`type=1` 租约，`type=2` ack。旧版本收到未知 type 直接丢弃，不会崩。
- 租约丢失不能导致本机输入被吞：这是 I2/I3 的兜底，不依赖任何重传。

---

## 5. 冲突仲裁

两台机器各自独立计算，必须收敛：

```
is_stronger(theirEpoch, theirOwner, myEpoch, myOwner):
    if theirEpoch != myEpoch: return theirEpoch > myEpoch
    return string.CompareOrdinal(theirOwner, myOwner) > 0
```

- 同时各自成为 `Owner` 时，两边都发出 epoch 相同的租约；收到对方租约后，较强的一方保持 `Owner`，较弱的一方 `Release(Arbitrated)` 并转为 `Target`。
- 若一方 epoch 更大（例如对端刚重启过、epoch 更大），则 epoch 大者胜——**因此 epoch 绝不能随便清零**；重启后从 `max(lastSeenEpoch, 0)+1` 继续。
- 仲裁后落败方必须在下一 tick 内停止抑制本机输入（I2 保证上限 700 ms）。

---

## 6. 边界与坐标

- 进入：主控端光标越过布局方向边缘 → 记录进入点像素 → `EdgeTransitionMapper.MapEntryToRemote` 映射到对端虚拟屏幕 → 首次 `MouseMove` 用该坐标。
- 退出（主控端主动）：对端虚拟光标越过同一侧边缘 → 主控端 `MapReturnToLocal` 映射回本机边缘，落点离边缘 **8 px**（`EdgeInset`），避免立刻再次触发。
- 退出（被控端请求）：被控端检测到光标在自己屏幕朝主控方向的边缘继续外移 → 发 `ControlRelease`（含当前坐标/屏幕尺寸/方向）→ 主控端按上式落点。
- 重新武装：任一次进入/退出后 `arm=false`，必须同时满足“光标离该边缘 ≥ 12 px”且“距上次切换 ≥ 500 ms”才重新武装；否则会出现 0.1.43 那种每秒来回翻转的乒乓。

### 6.1 指针注入（0.1.47 起）

- 主控端在远端屏幕范围内一律发**绝对坐标**（`InputKinds.MouseMove`，X/Y 为远端屏幕像素）。被控端 `MOUSEEVENTF_ABSOLUTE` 注入，忽略本机指针加速曲线，保证 1:1 跟随。
- 主控端自己维护远端虚拟坐标（`_remoteVirtualX/Y`），并钳在 `[0, ScreenWidth-1] × [0, ScreenHeight-1]`。
- 目标屏幕边界：若本次移动后虚拟坐标不再变化（已经顶到边界），改发**相对增量**（`InputKinds.MouseMoveRelative`），把“继续往外推”的意图交给被控端边缘检测器；被控端用该增量累加推送量。
- 被控端边缘返回判定：`RemoteEdgeReturnDetector.Update` 需要 `250 ms` 内累计向外 ≥ `12 px` 才返回 `ShouldRelease=true`（reason=`EdgePush`）；判定后 `500 ms` 冷却；单次注入、停在边缘、角落屏蔽都不释放。
- 被控端**不得**因为“注入后指针没动”而判定失败：指针在屏幕边界被 Windows 钳位是正常现象，只记 `mouse.clamp.suspected`。

### 6.2 帧压缩与传输优先级（0.1.50 起）

- 直连通道（UDP 47534）在 v3 帧里新增两种紧凑载荷：`FrameMouseRelativeCompact=3`（每事件 `sbyte dx,dy`，2 字节）和 `FrameMouseAbsoluteCompact=4`（每事件 `ushort x,y`，4 字节），一帧最多 `MaxCompactEvents=200` 条。
- 只有“整帧都是同类鼠标事件且坐标在压缩范围内”才用紧凑帧；含按键、滚轮、超范围坐标或条数超限时自动回退通用帧（`FrameInputBatch=0`，兼容旧版本）。
- 编解码统一在 `src\Shared.Core\Control\ControlChannelCodec.cs`（`EncodeInput` / `TryDecodeInput`），Agent 只管收发。
- 传输优先级由 `src\Shared.Core\Networking\TrafficPriority.cs` 打标：输入套接字 `DSCP 46`（EF），日志套接字 `DSCP 8`（后台）；标记失败只记 Debug，不影响功能。
- 日志推送在输入活跃（250 ms 内有输入）时降为 20 行 / 60 ms，空闲时 200 行 / 120 ms，避免抢占上行；日志推送还可由用户在 Agent 界面直接关闭。
- 带宽可观测：`DirectInputChannel.FramesSent` / `BytesSent` / `LastFrameBytes` 与 `RemoteInputSender` 的累计字节，经 Debug 日志 `mouse.remote.send`（含 `bytes=`）输出。


---

## 7. 故障与恢复矩阵

| 故障 | 检测方式 | 结果 | 用户感受 |
| --- | --- | --- | --- |
| 主控端进程崩溃 | 被控端 600 ms 无租约 | `Target → Idle` | 被控端 0.6 s 内恢复本地键鼠 |
| 被控端进程崩溃 | 主控端 700 ms 无 ack | `Owner → Idle` | 主控端 0.7 s 内恢复本地键鼠 |
| 网络丢包（HTTP 无关，UDP） | 同上（丢失 ack/租约） | 同上 | 0.6～0.7 s 后安全回到本地 |
| UDP 47534 被防火墙阻断 | 直连无 ack、中继 ack 正常 | 经中继继续工作；中继也不可用时 `Owner → Idle` | 跨屏可用（延迟略高），绝不卡输入 |
| 两端同时进入边缘 | epoch 相同 → 设备号裁决 | 一方 `Owner`、一方 `Target` | 最多一次 100 ms 抖动 |
| 远端注入失败（管理员窗口等） | 注入结果检查（阈值） | `Idle` + 提示 | 自动退回本地 |
| Service 重启 | 状态轮询 2 s 内发现断线 | `Idle` | 自动恢复 |
| 暂停（Ctrl+F11） | 热键 | 全部 `Idle` | 双端恢复本地 |

---

## 8. 代码对照

| 规范 | 代码位置 |
| --- | --- |
| 状态机与不变量 | `src\Shared.Core\Control\ControlLinkMachine.cs`（纯逻辑，可直接单元测试） |
| 租约/ack 编解码（UDP v3 帧） | `src\Shared.Core\Control\ControlChannelCodec.cs` |
| 租约/ack 中继载荷 | `src\Shared.Core\Protocol\ControlPayload.cs`（`ControlLeasePayload`、`ControlLeaseAckPayload`） |
| 中继命令与排队 | `src\Host.Service\PeerCoordinator.cs`（`HandleCommand` 的 `SendControlLease` / `SendControlLeaseAck`，`HandleEnvelope` → `QueueEvent`） |
| 状态判定入口 | `src\Desktop.Agent\MainWindow.xaml.cs`：`OnInputCaptured`、`OnRawMouseMoved`、`OnDirectInputBatchReceived`、`InjectPeerInput`、`OnLinkTick`、`SyncLinkFlags`、`ApplyIncomingLease`、`HandleRelayedLease`、`HandleRelayedAck` |
| 边界映射 | `src\Shared.Core\Control\EdgeTransitionMapper`（`EdgeInset=8`、`RemoteInset=8`） |
| 边缘检测 | `src\Input.Native\EdgeSwitchDetector.cs` + MainWindow 的 `arm/lock` |
| 日志事件 | 见第 9 节 |

---

## 9. 日志事件表（排障必须能只看日志还原状态迁移）

| 事件名 | 级别 | 关键字段 |
| --- | --- | --- |
| `control.link.begin` | INFO | epoch, direction, entry, localScreen, remoteScreen |
| `control.link.begin.denied` | WARN | reason, role, epoch |
| `control.link.lease` | DEBUG（2 s 节流） | role, epoch, owner, leaseAgeMs, ackAgeMs |
| `control.link.role` | INFO | from, to, epoch, owner |
| `control.link.sync` | DEBUG | reason, role, epoch |
| `control.link.timeout` | WARN | reason（AckTimeout / LeaseExpired / PeerReleased / …）, role, epoch |
| `control.link.arbitrated` | INFO | epoch, owner, wasOwner, path=udp\|relay |
| `control.link.stale` | DEBUG | count, role, epoch |
| `control.link.release` / `control.link.released` | INFO | reason, epoch, durationMs |
| `control.link.acquire.notice` | DEBUG | role, epoch, owner |
| `control.lease.send.fail` | DEBUG | path=udp, epoch, hasPeer |
| `control.lease.relay.fail` / `control.ack.relay.fail` | DEBUG | error |
| `control.lease.relay.parse.fail` / `control.ack.relay.parse.fail` | DEBUG | error |
| `control.target.mirror` | DEBUG | target, reason |
| `screen.switch.out` | INFO | direction, entry, localScreen, remoteScreen, remotePoint |
| `screen.switch.return` | INFO | direction, point, delta, reason |
| `screen.switch.back` | INFO | direction, remotePoint, mapped, localPoint, reason |
| `input.inject.ignored` | WARN（2 s 节流） | suppressed, role, epoch, reason |
| `input.inject.failed` | WARN | kind, reason, lastError |
| `mouse.remote.send` | DEBUG | abs=x,y 或 dx,dy, screen |
| `mouse.clamp.suspected` | DEBUG | kind, before, after, delta（指针在边界被钳位，不是注入失败） |
| `key.forward` | DEBUG | vk, scan, down, extended |
| `key.inject` | DEBUG | vk, scan, down, extended, result |

---

## 10. 验收清单（0.1.47 必须逐条通过）

1. 两端 `Idle` 启动，各自键鼠正常。
2. A 边缘切到 B：A 输入被吞（预期），B 光标跟随；A 按 `Ctrl+F10` 立即恢复两端。
3. A 控制 B 时把 B 的光标推到 B 屏幕右边缘（退回侧）：B 只发请求，A 光标回到 A 边缘 8 px 内侧，角色回到 `Idle`，A 键鼠立即可用。
4. 连续快速来回切 20 次，不出现“两边都动不了”；日志中不出现 `ack.timeout` 连续刷屏。
5. 控制中直接结束 B 的 Agent 进程：A 在 700 ms 内恢复本地键鼠，日志出现 `control.link.ack.timeout`。
6. 控制中拔掉 B 的网线/关 Wi-Fi：同上。
7. 两端几乎同时到达边缘：最多 200 ms 抖动后稳定为单主控，两端输入均可用。
8. B 被控时本地键鼠仍可正常使用（不应被抑制）。
9. 暂停/恢复（`Ctrl+F11`）双向生效，暂停后两端输入均可用。
10. 全程日志能还原每一次状态迁移（`control.link.*` 事件成对出现、epoch 单调）。
11. 在两台机器的防火墙里都**禁止** 47534 UDP 入站后重测：跨屏控制仍可用，日志出现 `control.lease.relay.fail` 或 `control.ack.relay.fail` 但无 `control.link.timeout` 连续刷屏。
12. 只阻断中继（停掉对端 Service）而保留 47534：租约直连仍生效，`control.link.arbitrated path=udp`，跨屏可用。
