# 控制器集成指南

## 已创建的新控制器

### 1. ControlFlowController.cs
**职责：** 控制流程总协调器
**功能：**
- 统一管理控制权状态机
- 协调边缘检测、远程控制、输入转发
- 处理租约和确认的心跳逻辑

### 2. EdgeDetectionController.cs
**职责：** 边缘检测与切屏
**改进：**
- 动态阈值和方向检测
- 防抖动机制
- 可配置的角落阻挡

### 3. RemoteControlController.cs
**职责：** 远程控制会话管理
**功能：**
- 控制权获取和释放
- 租约和确认的发送
- 会话持续时间跟踪

### 4. InputForwardingController.cs
**职责：** 输入转发和注入
**功能：**
- 输入事件队列管理
- 远程输入注入
- 按键释放管理

### 5. AdaptiveEdgeReturnDetector.cs
**职责：** 智能边缘返回检测
**改进：**
- 滑动窗口采样（300ms）
- 单样本限制（60px）
- 更大阈值（90px）和宽限期（1.5s）

### 6. FileTransferManager.cs
**职责：** 文件传输管理
**改进：**
- 小文件立即传输
- 大文件后台队列
- 统一进度报告

## 集成步骤

### 第一步：在 MainWindow 中引入新控制器

```csharp
// 在 MainWindow.xaml.cs 顶部添加
using LanMouseShare.Agent.Controllers;

// 在 MainWindow 类中替换现有字段
public partial class MainWindow : Window
{
    // 新的控制器（替换旧的分散逻辑）
    private ControlFlowController _controlFlow;
    private FileTransferManager _fileTransferManager;
    
    // 保留必要的现有组件
    private readonly PipeClient _pipe = new PipeClient();
    private DirectInputChannel _directInputChannel;
    private RawMouseInputMonitor _rawMouseInput;
    private ClipboardSyncService _clipboardSync;
    // ...
}
```

### 第二步：在构造函数中初始化控制器

```csharp
public MainWindow()
{
    InitializeComponent();
    
    // 初始化新控制器
    _controlFlow = new ControlFlowController(GetLocalDeviceId());
    _fileTransferManager = new FileTransferManager();
    
    // 连接事件
    _controlFlow.StateChanged += OnControlStateChanged;
    _controlFlow.ControlBegin += OnControlBegin;
    _controlFlow.ControlReleased += OnControlReleased;
    _controlFlow.EdgeSwitched += OnEdgeSwitched;
    
    _fileTransferManager.TransferProgress += OnFileTransferProgress;
    _fileTransferManager.TransferCompleted += OnFileTransferCompleted;
    _fileTransferManager.TransferError += OnFileTransferError;
    
    // 初始化其他组件...
}
```

### 第三步：替换鼠标移动处理逻辑

**旧代码（MainWindow.xaml.cs 约 1500-1600 行）：**
```csharp
private void OnRawMouseMoved(object sender, RawMouseEventArgs e)
{
    // 约 100 行的边缘检测和控制逻辑
    // 混合了状态判断、坐标转换、边缘检测等
}
```

**新代码：**
```csharp
private void OnRawMouseMoved(object sender, RawMouseEventArgs e)
{
    var position = new Point(e.X, e.Y);
    var screenBounds = GetCurrentScreenBounds();
    
    // 委托给控制流控制器
    _controlFlow.OnLocalMouseMove(position, screenBounds);
}
```

### 第四步：替换远程输入处理

**旧代码（约 2000-2100 行）：**
```csharp
private void OnDirectInputBatchReceived(object sender, DirectInputBatchEventArgs e)
{
    // 约 150 行的注入逻辑
    // 包含状态检查、坐标转换、注入执行等
}
```

**新代码：**
```csharp
private void OnDirectInputBatchReceived(object sender, DirectInputBatchEventArgs e)
{
    // 转换为 InputEvent 数组
    var events = ConvertToInputEvents(e.Events);
    
    // 委托给控制流控制器
    _controlFlow.OnRemoteInputReceived(events);
}

private InputEvent[] ConvertToInputEvents(object[] rawEvents)
{
    // 简单的转换逻辑
    var result = new List<InputEvent>();
    foreach (var raw in rawEvents)
    {
        // 根据现有格式转换
        result.Add(ConvertSingleEvent(raw));
    }
    return result.ToArray();
}
```

### 第五步：替换控制权管理

**旧代码（分散在多处）：**
```csharp
// 在约 10 个不同的方法中调用
_linkMachine.TryBeginControl(...)
_linkMachine.Release(...)
_session.SetTarget(...)
_inputRouter.SetSuppressLocal(...)
```

**新代码：**
```csharp
// 统一通过控制流控制器
_controlFlow.RequestReleaseControl(ControlLinkReleaseReason.UserRequest);
_controlFlow.SetPaused(true);
_controlFlow.SetPeerDeviceId(deviceId);
```

### 第六步：简化文件传输

**旧代码（ClipboardSyncService 和 FileDropSender）：**
```csharp
// 约 300 行的复杂流程
// 剪贴板监听 → 文件公告 → 懒加载 → Ctrl+V 拦截 → 请求拉取
```

**新代码：**
```csharp
private async void OnFilesDropped(string[] filePaths)
{
    if (_state?.PeerDeviceId == null)
    {
        MessageBox.Show("未连接到对端设备");
        return;
    }
    
    try
    {
        var transferId = await _fileTransferManager.BeginTransferAsync(
            filePaths, 
            _state.PeerDeviceId);
        
        UpdateStatusText($"正在传输文件... (ID: {transferId.Substring(0, 8)})");
    }
    catch (Exception ex)
    {
        AppLog.Error("file.drop.failed", $"error={ex.Message}");
        MessageBox.Show($"文件传输失败: {ex.Message}");
    }
}

private void OnFileTransferProgress(object sender, FileTransferProgressEventArgs e)
{
    // 更新进度条
    Dispatcher.Invoke(() =>
    {
        var progress = (double)e.FileBytes / e.FileTotalBytes * 100;
        UpdateTransferProgress(e.FileName, progress);
    });
}
```

## 迁移清单

### 高优先级（立即迁移）
- [x] 创建所有控制器类
- [ ] 在 MainWindow 中初始化控制器
- [ ] 替换 OnRawMouseMoved 逻辑
- [ ] 替换 OnDirectInputBatchReceived 逻辑
- [ ] 连接所有事件处理器

### 中优先级（1-2 天内）
- [ ] 替换租约/确认处理
- [ ] 集成 AdaptiveEdgeReturnDetector
- [ ] 迁移文件传输到 FileTransferManager
- [ ] 更新 UI 绑定

### 低优先级（功能验证后）
- [ ] 删除旧的分散逻辑
- [ ] 清理未使用的字段
- [ ] 优化日志输出
- [ ] 添加单元测试

## 兼容性注意事项

### 保持网络协议不变
新控制器内部使用新的事件模型，但对外的网络协议（UDP 帧格式、TCP 消息格式）保持不变：
- `DirectInputChannel` 仍然使用 v3/v4/v5 帧格式
- `PipeClient` 的消息格式不变
- 租约和确认的序列化格式不变

### 保持配置格式不变
`config.json` 的所有字段保持兼容：
```json
{
  "PeerDirection": "Right",
  "BlockScreenCorners": true,
  "PauseHotkey": "Ctrl+F11",
  // ... 所有现有字段
}
```

### 保持日志事件名称
所有日志事件名称保持不变，确保现有的日志分析工具仍然有效：
- `control.link.role`
- `control.link.begin`
- `edge.reached`
- 等等

## 测试策略

### 单元测试
创建 `src/Desktop.Agent.Tests/Controllers/` 目录，为每个控制器编写测试：

```csharp
[TestClass]
public class EdgeDetectionControllerTests
{
    [TestMethod]
    public void TestEdgeDetection_Right()
    {
        var controller = new EdgeDetectionController();
        controller.SetDirection("Right");
        
        bool edgeReached = false;
        controller.EdgeReached += (s, e) => edgeReached = true;
        
        // 模拟鼠标移动到右边缘
        var bounds = new Rectangle(0, 0, 1920, 1080);
        controller.OnMouseMove(new Point(1919, 500), bounds);
        
        Assert.IsTrue(edgeReached);
    }
}
```

### 集成测试（使用 LanCtrl）
1. 在 LanCtrl 中连接到测试机
2. 通过 `lanctrl_run` 执行鼠标移动命令
3. 验证边缘检测和控制权切换
4. 检查日志输出

```bash
# 使用 LanCtrl.Cli 进行自动化测试
LanCtrl.Cli.exe --host 192.168.1.100 --run "启动Agent"
LanCtrl.Cli.exe --host 192.168.1.100 --move 1919,500
# 验证控制权切换
```

## 性能优化

### 减少 MainWindow 代码行数
- **目标：** 从 3609 行降到 <1500 行
- **方法：** 将约 2100 行的控制逻辑移到控制器类

### 降低循环复杂度
- **当前：** 多个 100+ 行的方法
- **目标：** 单个方法 <50 行
- **方法：** 每个控制器负责单一职责

### 提升响应速度
- **边缘检测：** 从多次判断简化到单次委托调用
- **输入注入：** 使用队列避免阻塞 UI 线程
- **文件传输：** 小文件立即传输，大文件后台处理

## 回滚计划

如果新控制器出现问题，可以快速回滚：

### 功能开关
在 `config.json` 中添加：
```json
{
  "UseNewControllers": true
}
```

在 MainWindow 构造函数中：
```csharp
if (_config.UseNewControllers)
{
    _controlFlow = new ControlFlowController(deviceId);
    // 使用新控制器
}
else
{
    // 使用旧逻辑
}
```

### Git 分支策略
- `main` - 稳定版本（0.1.64）
- `refactor/controllers` - 重构分支（新控制器）
- `hotfix/*` - 紧急修复

## 下一步

1. **立即执行：** 集成新控制器到 MainWindow
2. **今天完成：** 替换核心控制逻辑
3. **明天测试：** 使用 LanCtrl 进行端到端测试
4. **本周发布：** 0.2.0-beta 版本

---

**文档更新时间：** 2026-10-21
**负责人：** Claude + yangyang
**状态：** 控制器已创建，待集成
