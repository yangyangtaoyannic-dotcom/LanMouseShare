# PATCH-NOTES — 0.2.0-beta-fix5

日期：2026-10-29
上一版本：0.2.0-beta-fix4
安装器（Setup.exe）版本：0.2.1（保持不变，升级比较用纯数字版本）

---

## 本轮修复

### 1. 文件粘贴仍然是灰色（fix4 回归，本轮实测暴露并修复）

**现象**
- fix4 装上后，对端复制文件，本机右键"粘贴"还是灰的；只有极少数情况能触发传输。

**根因（日志实证）**
- 被控端日志反复出现：
  `warn clipboard.files.virtual.failed hr=0x800401F0`
- `0x800401F0` = `CO_E_NOTINITIALIZED`（"尚未调用 CoInitialize"）。
- fix4 把剪贴板数据对象托管到 **MTA 线程**（`CoInitializeEx(COINIT_MULTITHREADED)`），但 Windows 的 OLE 剪贴板要求调用线程经 **OleInitialize（STA）** 初始化：MTA 线程上 `OleGetClipboard` 正常、`OleSetClipboard` 恒失败。
- 本地最小复现程序验证：
  - STA 线程 + `OleInitialize` + `OleSetClipboard(NULL)` → **S_OK**
  - MTA 线程 + `CoInitializeEx` + `OleSetClipboard(NULL)` → **0x800401F0**
  - STA 线程带消息泵发布对象后，另一个线程 `OleGetClipboard`+`GetData` 正常拿到数据（跨公寓封送 OK）
- 发布失败后代码回退到"懒提示"，剪贴板留空 → 粘贴依旧灰。

**修复**
- `VirtualFileClipboard` 发布通道改为：进程级长驻 **STA 线程 + 消息泵**。
  - 线程：`SetApartmentState(STA)`，进入后先 `OleInitialize`；
  - 泵：`PeekMessage / TranslateMessage / DispatchMessage` 循环（COM 把 Explorer 等粘贴方的 `GetData` 回调封送回这条 STA，由泵分发）；
  - 对象创建与 `OleSetClipboard`（含占用重试）全部经队列投递到该线程执行；
  - 拉取大文件只阻塞这条 STA 线程，Agent 界面与输入钩子不受影响。
- `FetchRemoteForClipboard` 单文件拉取超时 35 秒 → **10 分钟**（实测大文件单文件要 69 秒，35 秒会导致粘贴中途报错）。
- 保留 fix4 的惰性渲染设计：`FileContents` 以 TYMED_ISTREAM 发布（本机随后新复制时 `OleFlushClipboard` 只拿流指针、不会整批强制渲染），Win7 等要 HGLOBAL 的调用方在粘贴时才现取字节。

### 2. 边角阻挡（fix4 已修，本轮双机实测确认）

- fix4 的返回路径边角阻挡本轮实测生效：主控端日志出现连续 `edge.return.corner.blocked`（在被控端屏幕左下角推回被挡、屏幕中段正常放行）。本轮未改代码。

---

## 安装方法

两台电脑都要升级（A 主控端 / B 被控端同样步骤）：

1. 停止旧版本
   ```
   taskkill /F /IM LanMouseShare.Agent.exe
   net stop LanMouseShare
   ```
2. 备份（推荐）：把 `C:\Program Files\LanMouseShare` 重命名为 `LanMouseShare-backup-fix4`
3. 覆盖新版本
   - 复制 `Agent\` 到 `C:\Program Files\LanMouseShare\Agent\`
   - 复制 `Service\` 到 `C:\Program Files\LanMouseShare\Service\`
4. 启动
   ```
   net start LanMouseShare
   运行 C:\Program Files\LanMouseShare\Agent\LanMouseShare.Agent.exe
   ```
   或者直接双击 `LanMouseShareSetup.exe` 覆盖安装（Setup 版本号保持 0.2.1 不变，仍会安装新 payload）。

---

## 验证要点

1. A 复制文件 → B 上 Explorer 右键"粘贴"应可用（不再灰）；点粘贴才开始传输（可看 B 上 Agent 状态栏/进度条）。
2. B 复制文件 → A 上同样操作，行为一致。
3. 粘贴大文件（>100MB）不报错、不中途失败。
4. 边角阻挡：A 控制 B 时，从 B 屏幕的四个角落推回主控屏应被挡住；屏幕边缘中间部位正常返回。
5. 日志确认：发布成功时日志出现 `clipboard.files.virtual count=N first=...`；若再出现 `clipboard.files.virtual.failed hr=0x800401F0` 请反馈。
