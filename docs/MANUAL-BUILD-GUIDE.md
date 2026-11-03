# 手动编译和安装 0.2.0-beta 版本指南

## 📋 前提条件

- Visual Studio 2017 或更高版本（或 MSBuild Tools）
- .NET Framework 4.8 SDK
- 管理员权限

## 🔨 步骤 1：编译项目

### 方法 A：使用 Visual Studio

1. 打开 `LanMouseShare.sln`
2. 选择 **Release** 配置
3. 右键解决方案 → **清理解决方案**
4. 右键解决方案 → **重新生成解决方案**
5. 确保没有错误

### 方法 B：使用命令行

在项目根目录以管理员身份打开 PowerShell：

```powershell
# 查找 MSBuild
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"

# 如果找不到，尝试其他路径
# Visual Studio 2022:
# $msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"

# 或使用 .NET Framework MSBuild:
# $msbuild = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"

# 清理
& $msbuild LanMouseShare.sln /t:Clean /p:Configuration=Release /nologo

# 编译
& $msbuild LanMouseShare.sln /t:Build /p:Configuration=Release /nologo
```

## 📦 步骤 2：手动打包

### 创建发布目录

```powershell
# 创建目录
$version = "0.2.0-beta"
$dist = "C:\Users\yangyang\Desktop\局域网键盘鼠标共享\dist\LanMouseShare-$version"

New-Item -ItemType Directory -Path $dist -Force
New-Item -ItemType Directory -Path "$dist\Agent" -Force
New-Item -ItemType Directory -Path "$dist\Service" -Force
New-Item -ItemType Directory -Path "$dist\Scripts" -Force
New-Item -ItemType Directory -Path "$dist\Docs" -Force
```

### 复制文件

```powershell
# 设置源路径
$root = "C:\Users\yangyang\Desktop\局域网键盘鼠标共享"

# 复制 Agent
Copy-Item "$root\src\Desktop.Agent\bin\Release\*" "$dist\Agent\" -Recurse -Force

# 复制 Service
Copy-Item "$root\src\Host.Service\bin\Release\*" "$dist\Service\" -Recurse -Force

# 复制 Scripts
Copy-Item "$root\scripts\*" "$dist\Scripts\" -Recurse -Force

# 复制 Docs
Copy-Item "$root\docs\*" "$dist\Docs\" -Recurse -Force

# 复制根文档
Copy-Item "$root\PROJECT_GUIDE.md" "$dist\" -Force
```

## 🚀 步骤 3：安装到目标机器

### 在主控端和被控端都执行以下操作：

1. **停止旧服务**
```powershell
# 停止 Agent（如果正在运行）
taskkill /F /IM LanMouseShare.Agent.exe

# 停止 Service
net stop LanMouseShare
```

2. **备份旧版本**（可选但推荐）
```powershell
$backup = "C:\Program Files\LanMouseShare-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Move-Item "C:\Program Files\LanMouseShare" $backup -Force
```

3. **安装新版本**
```powershell
# 创建目标目录
New-Item -ItemType Directory -Path "C:\Program Files\LanMouseShare\Agent" -Force
New-Item -ItemType Directory -Path "C:\Program Files\LanMouseShare\Service" -Force

# 复制文件（将 $dist 替换为你的实际路径）
Copy-Item "$dist\Agent\*" "C:\Program Files\LanMouseShare\Agent\" -Recurse -Force
Copy-Item "$dist\Service\*" "C:\Program Files\LanMouseShare\Service\" -Recurse -Force
```

4. **启动服务**
```powershell
# 启动 Service（如果已安装）
net start LanMouseShare

# 或者重新安装 Service
sc.exe create LanMouseShare binPath= "C:\Program Files\LanMouseShare\Service\LanMouseShare.Service.exe" start= auto
net start LanMouseShare

# 启动 Agent
Start-Process "C:\Program Files\LanMouseShare\Agent\LanMouseShare.Agent.exe"
```

## ⚙️ 步骤 4：启用新控制器

编辑配置文件 `C:\ProgramData\LanMouseShare\config.json`：

### 方法 A：手动编辑
```json
{
  "DeviceId": "...",
  "DeviceName": "...",
  "UseNewControllers": true,
  ...其他配置...
}
```

### 方法 B：使用 PowerShell
```powershell
$configPath = "C:\ProgramData\LanMouseShare\config.json"
$config = Get-Content $configPath | ConvertFrom-Json
$config | Add-Member -NotePropertyName 'UseNewControllers' -NotePropertyValue $true -Force
$config | ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding UTF8
```

**重启 Agent 使配置生效**

## 🧪 步骤 5：验证安装

### 检查新控制器是否启用

查看日志文件：
```powershell
# 查找最新日志
$logPath = "C:\ProgramData\LanMouseShare\Logs\live"
$latestLog = Get-ChildItem $logPath -Filter "Agent-*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

# 查看最后 50 行
Get-Content $latestLog.FullName -Tail 50
```

查找这一行：
```
[INFO] controller.integration.mode mode=new_controllers
```

如果看到 `mode=new_controllers`，说明新控制器已启用成功！

## 🧪 步骤 6：测试新功能

### 使用测试脚本

如果你有 LanCtrl 调试工具：

```powershell
cd "$dist\Scripts"

# 完整测试
.\Test-NewControllers.ps1 -ControllerHost <主控端IP> -AgentHost <被控端IP>

# 边缘返回测试
.\Test-EdgeReturn.ps1 -Host <被控端IP>

# 实时监控
.\Debug-Remote.ps1 -Command monitor -Host <IP>
```

### 手动测试

1. **基础控制测试**
   - 移动鼠标到配置的边缘方向
   - 验证是否能切换到另一台电脑
   - 测试鼠标和键盘是否正常工作

2. **边缘返回测试**
   - 在被控端慢慢推动鼠标到边缘
   - 验证是否在合理的时间内返回主控端
   - 快速甩动鼠标，验证不会误触发返回

3. **文件传输测试**
   - 拖拽小文件到 Agent 窗口
   - 检查是否成功传输到对端

## 🔄 如果遇到问题

### 回滚到旧实现

```powershell
# 禁用新控制器
$configPath = "C:\ProgramData\LanMouseShare\config.json"
$config = Get-Content $configPath | ConvertFrom-Json
$config.UseNewControllers = $false
$config | ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding UTF8

# 重启 Agent
taskkill /F /IM LanMouseShare.Agent.exe
Start-Process "C:\Program Files\LanMouseShare\Agent\LanMouseShare.Agent.exe"
```

### 完全回滚到旧版本

```powershell
# 找到备份
$backup = Get-ChildItem "C:\Program Files" -Filter "LanMouseShare-backup-*" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($backup) {
    # 停止服务
    net stop LanMouseShare
    taskkill /F /IM LanMouseShare.Agent.exe
    
    # 恢复旧版本
    Remove-Item "C:\Program Files\LanMouseShare" -Recurse -Force
    Move-Item $backup.FullName "C:\Program Files\LanMouseShare"
    
    # 启动服务
    net start LanMouseShare
    Start-Process "C:\Program Files\LanMouseShare\Agent\LanMouseShare.Agent.exe"
}
```

## 📊 新版本特性

### 改进点
- ✅ 边缘返回阈值从 12px 提升到 90px
- ✅ 新增 1.5 秒宽限期，避免过早触发返回
- ✅ 滑动窗口采样，忽略快速甩动
- ✅ 单样本限制 60px，防止单次大位移触发
- ✅ 控制逻辑重构，代码更清晰

### 新增日志事件
```
controller.integration.mode          - 控制器模式
controller.integration.state_changed - 状态变化
edge.return.triggered               - 触发边缘返回
edge.return.sample                  - 采样记录
input.forwarding.start              - 开始输入转发
file.transfer.begin                 - 文件传输开始
```

## 📝 注意事项

1. **备份重要**：安装前务必备份旧版本和配置文件
2. **两端同步**：主控端和被控端都要更新到相同版本
3. **测试充分**：在非关键时段进行测试
4. **保留日志**：如遇问题，保留日志文件用于分析

## 🆘 获取帮助

如果遇到问题，收集以下信息：

1. **配置文件**
   - `C:\ProgramData\LanMouseShare\config.json`

2. **日志文件**
   - `C:\ProgramData\LanMouseShare\Logs\live\Agent-*.log`
   - `C:\ProgramData\LanMouseShare\Logs\live\Service-*.log`

3. **系统信息**
   - Windows 版本
   - 屏幕分辨率和 DPI 设置
   - 网络配置

---

**版本**: 0.2.0-beta  
**构建日期**: 2026-10-21  
**基于版本**: 0.1.64
