# 新控制器使用和测试指南

## 🎯 概述

本指南说明如何使用新创建的控制器系统，以及如何通过 LanCtrl 进行远程调试和测试。

## 📁 已创建的文件

### 核心控制器
```
src/Desktop.Agent/Controllers/
├── ControlFlowController.cs          # 控制流程总协调器
├── EdgeDetectionController.cs        # 边缘检测控制器
├── RemoteControlController.cs        # 远程控制会话管理
├── InputForwardingController.cs      # 输入转发与注入
├── AdaptiveEdgeReturnDetector.cs     # 自适应边缘返回检测
└── FileTransferManager.cs            # 文件传输管理器
```

### 集成和配置
```
src/Desktop.Agent/ControllerIntegration.cs  # 新旧实现切换包装器
src/Shared.Core/Configuration/AppConfig.cs  # 已添加 UseNewControllers 开关
```

### 测试脚本
```
scripts/
├── Test-NewControllers.ps1    # 完整的自动化测试脚本
├── Test-EdgeReturn.ps1        # 边缘返回压力测试
└── Debug-Remote.ps1           # LanCtrl 远程调试工具
```

### 文档
```
docs/
├── REFACTOR-PLAN.md              # 重构计划和目标
├── CONTROLLER-INTEGRATION.md     # 集成步骤详解
└── USAGE-GUIDE.md               # 本文档
```

## 🚀 快速开始

### 1. 启用新控制器

**方法 A：手动修改配置文件**

编辑 `C:\ProgramData\LanMouseShare\config.json`：
```json
{
  "UseNewControllers": true,
  ...
}
```

**方法 B：使用 LanCtrl 远程启用**

```powershell
# 在主控端启用
.\scripts\Debug-Remote.ps1 -Command enable-new -Host 192.168.1.100

# 在被控端启用
.\scripts\Debug-Remote.ps1 -Command enable-new -Host 192.168.1.101
```

### 2. 重启 Agent

新配置需要重启 Agent 才能生效：

```powershell
# 使用 LanCtrl 重启
.\scripts\Debug-Remote.ps1 -Command restart -Host <目标IP>
```

或者手动重启：
- 关闭 Agent 窗口
- 重新运行 `LanMouseShare.Agent.exe`

### 3. 验证新控制器是否生效

```powershell
# 运行完整测试
.\scripts\Test-NewControllers.ps1 -ControllerHost 192.168.1.100 -AgentHost 192.168.1.101

# 查看日志确认
.\scripts\Debug-Remote.ps1 -Command logs -Host 192.168.1.100
```

在日志中查找：
```
[INFO] controller.integration.mode mode=new_controllers
```

如果看到 `mode=new_controllers`，说明新控制器已启用。

## 🧪 测试场景

### 场景 1：基础功能测试

```powershell
# 运行完整的自动化测试
cd C:\Users\yangyang\Desktop\局域网键盘鼠标共享
.\scripts\Test-NewControllers.ps1 `
    -ControllerHost 192.168.1.100 `
    -AgentHost 192.168.1.101 `
    -Port 47800 `
    -Code 1234
```

**测试内容：**
- ✅ 连接测试
- ✅ Agent 运行检查
- ✅ 配置验证
- ✅ 日志输出检查
- ✅ 鼠标移动测试

### 场景 2：边缘返回测试

```powershell
# 测试改进的边缘返回检测器
.\scripts\Test-EdgeReturn.ps1 -Host 192.168.1.101
```

**测试场景：**
1. 快速甩动 - 应该**不触发**返回
2. 缓慢推动 - 应该**触发**返回
3. 停在边缘 - 应该**不触发**返回
4. 边缘抖动 - 应该**不触发**返回
5. 来回移动 - 应该**不触发**返回
6. 持续推动 - 应该**触发**返回
7. 角落区域 - 应该被**阻挡**

### 场景 3：实时监控

```powershell
# 启动实时日志监控
.\scripts\Debug-Remote.ps1 -Command monitor -Host 192.168.1.100
```

在另一个终端中操作鼠标，观察日志输出。

## 🔍 调试技巧

### 查看系统信息

```powershell
.\scripts\Debug-Remote.ps1 -Command info -Host <IP>
```

### 读取日志

```powershell
# 读取最近 100 行
.\scripts\Debug-Remote.ps1 -Command logs -Host <IP>

# 或使用 LanCtrl 直接读取
cd C:\Users\yangyang\Desktop\跨win系统调试\artifacts
.\LanCtrl.Cli.exe --host <IP> --tail "C:\ProgramData\LanMouseShare\Logs\live\Agent-*.log" --lines 100
```

### 查找特定日志事件

**控制权相关：**
```
controller.integration.mode        # 控制器模式（新/旧）
controller.integration.state_changed  # 状态变化
controller.integration.begin       # 开始控制
controller.integration.released    # 释放控制
```

**边缘检测相关：**
```
edge.reached                      # 到达边缘
edge.return.triggered             # 触发返回
edge.return.sample                # 采样记录
edge.return.armed                 # 重新武装
```

**输入转发相关：**
```
input.forwarding.start            # 开始转发
input.forwarding.stop             # 停止转发
input.inject.success              # 注入成功
input.inject.failed               # 注入失败
```

**文件传输相关：**
```
file.transfer.begin               # 开始传输
file.transfer.completed           # 传输完成
file.transfer.failed              # 传输失败
```

### 切换回旧实现

如果新控制器出现问题，可以快速切换回旧实现：

```powershell
.\scripts\Debug-Remote.ps1 -Command disable-new -Host <IP>
```

## 📊 性能对比

### 预期改进

| 指标 | 旧实现 | 新实现 | 改进 |
|------|--------|--------|------|
| 鼠标弹回问题 | 约2秒弹回 | 90px阈值 + 1.5s宽限期 | ✅ 解决 |
| MainWindow 行数 | 3609 行 | <1500 行（目标） | ⏳ 进行中 |
| 控制逻辑清晰度 | 分散在多处 | 单一入口 | ✅ 已改进 |
| 文件传输 | 复杂流程 | 简化流程 | ✅ 已改进 |
| 测试覆盖率 | 低 | 可独立测试 | ✅ 已改进 |

## 🐛 已知问题和限制

### 当前版本限制
1. **未完全集成到 MainWindow** - 需要手动集成
2. **InputEvent 转换** - 需要根据实际格式实现
3. **文件传输** - FileTransferManager 的实际传输逻辑需要与 FileDropSender 集成

### 计划解决
- [ ] 完成 MainWindow 集成
- [ ] 实现 InputEvent 转换逻辑
- [ ] 集成文件传输到现有通道
- [ ] 添加单元测试

## 📝 日志分析示例

### 正常的控制流程

```
[INFO] controller.integration.mode mode=new_controllers
[INFO] edge.reached edge=Right x=1919 y=540 direction=Right
[INFO] controller.integration.begin epoch=1729501234567 direction=Right
[INFO] input.forwarding.start direction=Right remoteSize=1920x1080
[INFO] controller.integration.state_changed role=Owner paused=False
...
[INFO] edge.return.triggered totalPush=95 samples=3 direction=Right
[INFO] controller.integration.released reason=EdgePush epoch=1729501234567
[INFO] input.forwarding.stop injected=1234
```

### 异常情况

如果看到以下日志，说明可能有问题：

```
[ERROR] input.inject.failed kind=MouseMove error=...
[WARN] controller.integration.file_error id=... error=...
[ERROR] controller.flow.timeout reason=AckTimeout role=Owner
```

## 🔄 回滚步骤

如果需要完全回滚到旧实现：

1. **禁用新控制器**
```powershell
.\scripts\Debug-Remote.ps1 -Command disable-new -Host <IP>
```

2. **重启 Agent**
```powershell
.\scripts\Debug-Remote.ps1 -Command restart -Host <IP>
```

3. **验证**
```powershell
.\scripts\Debug-Remote.ps1 -Command logs -Host <IP>
```

查找日志中的：
```
[INFO] controller.integration.mode mode=legacy
```

## 📞 支持和反馈

### 报告问题

创建日志包：
1. 复现问题
2. 收集日志
```powershell
.\scripts\Debug-Remote.ps1 -Command logs -Host <IP> > issue-logs.txt
```
3. 包含配置文件
```
C:\ProgramData\LanMouseShare\config.json
```

### 性能问题

如果遇到性能问题：
1. 检查日志中的警告
2. 查看 CPU 使用率
3. 检查网络延迟（RTT）

## 🎓 进阶使用

### 自定义边缘检测参数

编辑 `AdaptiveEdgeReturnDetector.cs`：
```csharp
private const int ThresholdPixels = 90;      // 调整阈值
private const int MaxSamplePixels = 60;      // 单样本限制
private const int WindowMs = 300;            // 窗口时间
private const int GracePeriodMs = 1500;      // 宽限期
```

### 自定义文件传输策略

编辑 `FileTransferManager.cs`：
```csharp
private const long SmallFileSizeThreshold = 10 * 1024 * 1024; // 小文件阈值
private const int MaxConcurrentTransfers = 2;                  // 并发数
```

## 📚 参考文档

- [重构计划](REFACTOR-PLAN.md) - 完整的重构目标和进度
- [集成指南](CONTROLLER-INTEGRATION.md) - 详细的集成步骤
- [控制状态模型](CONTROL-STATE-MODEL.md) - 控制权状态机规范
- [功能审计](FEATURE-AUDIT.md) - 当前功能状态

---

**版本：** 0.2.0-beta  
**更新时间：** 2026-10-21  
**状态：** 控制器已创建，待集成测试
