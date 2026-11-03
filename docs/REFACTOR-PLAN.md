# LanMouseShare 重构计划

## 📊 当前问题诊断

基于 `FEATURE-AUDIT.md` 和代码审查，当前主要问题：

### 1. MainWindow.xaml.cs 过于庞大（3609 行）
**问题：**
- 控制逻辑、UI 逻辑、网络通信、文件传输混在一起
- 难以测试和维护
- 状态管理分散

**影响：**
- Bug 难以定位
- 新功能难以添加
- 团队协作困难

### 2. 边缘返回检测过于敏感
**当前实现：** `RemoteEdgeReturnDetector`
**问题：**
- 单次大位移立即触发返回
- 鼠标约 2 秒就弹回主控端
- 用户体验差

**0.1.58 改进：**
- 阈值 90px、≥2 个样本、300ms 窗口
- 但逻辑仍在 MainWindow 中

### 3. 文件传输机制复杂
**当前流程：**
```
剪贴板监听 → 文件公告 → 懒加载 → Ctrl+V 拦截 → 请求拉取 → 传输 → 写剪贴板
```

**问题：**
- 步骤过多，容易出错
- `LocalHasFiles` 标志不同步
- 跨屏粘贴文件经常失败

### 4. 控制权状态分散
**当前状态存储：**
- `ControlLinkMachine._role`（核心状态机）
- `ControlSessionController`（会话控制）
- `InputRoutingController`（输入路由）
- `MainWindow` 中的多个标志位

**问题：**
- 状态同步困难
- 容易出现不一致

## 🎯 重构目标

### 阶段一：控制逻辑分离（1-2 天）
**目标：** 将 MainWindow 中的控制逻辑提取到独立的控制器类

**新增类：**
```
src/Desktop.Agent/Controllers/
├── ControlFlowController.cs       # 控制流程协调器（主入口）
├── EdgeDetectionController.cs     # 边缘检测与切屏逻辑
├── RemoteControlController.cs     # 远程控制会话管理
└── InputForwardingController.cs   # 输入转发与注入
```

**收益：**
- 职责清晰，易于测试
- 降低 MainWindow 复杂度到 1500 行以下
- 便于单元测试

### 阶段二：边缘返回逻辑优化（0.5 天）
**优化点：**
1. 动态阈值：根据鼠标速度调整
2. 方向检测：只计算朝向边缘的移动
3. 加速度检测：区分快速甩动和缓慢推动

**新实现：** `AdaptiveEdgeReturnDetector.cs`

### 阶段三：文件传输简化（1 天）
**新流程：**
```
剪贴板监听 → 立即传输小文件(<10MB) → 写剪贴板
            → 大文件使用传输队列 → 后台传输
```

**改进：**
- 小文件即时传输，无需 Ctrl+V 触发
- 大文件显示进度条
- 统一的传输管理器

**新类：** `FileTransferManager.cs`

### 阶段四：状态管理统一（0.5 天）
**创建统一状态管理器：**
```csharp
public class ControlStateManager
{
    private ControlLinkMachine _linkMachine;
    private ControlSession _session;
    
    public ControlState CurrentState { get; }
    public event EventHandler<StateChangedEventArgs> StateChanged;
    
    public bool CanBeginControl();
    public void BeginControl();
    public void ReleaseControl(ReleaseReason reason);
}
```

### 阶段五：日志与诊断增强（0.5 天）
**目标：** 更详细的诊断信息

**新增：**
- 每次状态迁移的完整上下文
- 性能指标（延迟、丢包率）
- 自动问题检测（持续弹回、高延迟等）

## 📐 重构架构

### 新的 Agent 架构
```
MainWindow (UI 协调器，<1500 行)
├── ControlFlowController (控制流程)
│   ├── EdgeDetectionController (边缘检测)
│   ├── RemoteControlController (远程控制)
│   └── InputForwardingController (输入转发)
├── FileTransferManager (文件传输)
├── ClipboardManager (剪贴板管理)
└── DiagnosticsCollector (诊断收集)
```

### 控制流程简化
```
用户移动鼠标
└─> EdgeDetectionController.OnMouseMove()
    └─> 检测到边缘
        └─> ControlFlowController.RequestControl()
            └─> ControlStateManager.BeginControl()
                ├─> RemoteControlController.Activate()
                │   └─> 发送租约、抑制本地输入
                └─> InputForwardingController.Start()
                    └─> 转发输入到远端
```

## 🧪 测试策略

### 单元测试
- `EdgeDetectionController` 的边界条件
- `AdaptiveEdgeReturnDetector` 的阈值逻辑
- `ControlStateManager` 的状态迁移

### 集成测试
- 使用 LanCtrl 进行远程调试
- 模拟网络延迟和丢包
- 压力测试（快速切换、大文件传输）

### 回归测试
- 验证所有功能仍然可用
- 对比 0.1.58 的日志输出
- 确保性能不下降

## 📅 实施计划

### Week 1
- [ ] Day 1-2: 阶段一 - 控制逻辑分离
  - 创建控制器类
  - 迁移逻辑
  - 更新 MainWindow
- [ ] Day 3: 阶段二 - 边缘返回优化
  - 实现 `AdaptiveEdgeReturnDetector`
  - 集成测试
- [ ] Day 4: 阶段三 - 文件传输简化
  - 实现 `FileTransferManager`
  - 重构剪贴板逻辑
- [ ] Day 5: 阶段四五 - 状态管理和日志
  - 实现 `ControlStateManager`
  - 增强诊断日志

### Week 2
- [ ] 集成测试与调优
- [ ] 文档更新
- [ ] 发布 0.2.0 版本

## 🔄 迁移路径

### 向后兼容
- 保持现有配置格式
- 保持现有网络协议
- 保持现有日志事件名称

### 渐进式重构
1. 先创建新的控制器类，与现有代码并行
2. 逐步迁移功能到新类
3. 使用功能开关在新旧实现之间切换
4. 完全迁移后删除旧代码

## 🚀 预期收益

### 代码质量
- MainWindow 从 3609 行降到 <1500 行
- 循环复杂度降低 50%
- 测试覆盖率提升到 60%+

### 用户体验
- 鼠标弹回问题解决
- 文件传输成功率 >95%
- 响应延迟降低 20%

### 可维护性
- 新功能开发速度提升 2x
- Bug 修复时间降低 50%
- 代码审查时间降低 40%

---

**开始日期：** 2026-10-21
**负责人：** Claude + yangyang
**优先级：** 高
