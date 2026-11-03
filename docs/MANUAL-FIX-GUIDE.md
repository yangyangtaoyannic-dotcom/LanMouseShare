# 手动修复指南

## 🔧 问题 1：剪贴板文件传输和文本/图片共享

### 文件位置
`src\Desktop.Agent\ClipboardSyncService.cs`

### 修复方法

找到 `OnClipboardChanged` 方法（约第 354 行），将检测顺序从：
```csharp
// 旧顺序：文件 -> 文本 -> 图片
if (Clipboard.ContainsFileDropList()) { ... }
else if (Clipboard.ContainsText()) { ... }
else if (Clipboard.ContainsImage()) { ... }
```

改为：
```csharp
// 新顺序：文本 -> 图片 -> 文件
if (Clipboard.ContainsText())
{
    // 确保不是文件路径列表
    var text = Clipboard.GetText();
    var hasFiles = Clipboard.ContainsFileDropList();
    if (!hasFiles || text.Length < 260)  // MAX_PATH
    {
        payload = new ClipboardPayload
        {
            OriginDeviceId = state.LocalDeviceId,
            Version = ++_version,
            Format = ClipboardFormats.Text,
            Text = text
        };
    }
}
else if (Clipboard.ContainsImage())
{
    var bytes = EncodeImage(Clipboard.GetImage());
    if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImageBytes)
    {
        AppLog.Warn("Clipboard image ignored. Bytes=" + (bytes != null ? bytes.Length : 0));
        return;
    }

    payload = new ClipboardPayload
    {
        OriginDeviceId = state.LocalDeviceId,
        Version = ++_version,
        Format = ClipboardFormats.ImagePng,
        ImagePngBytes = bytes
    };
}
else if (Clipboard.ContainsFileDropList())
{
    // 文件复制：只发送文件列表公告，不立即传输
    var files = new List<string>();
    var collection = Clipboard.GetFileDropList();
    if (collection != null)
    {
        for (int i = 0; i < collection.Count && files.Count < 32; i++)
        {
            var path = collection[i];
            if (!string.IsNullOrEmpty(path))
            {
                files.Add(path);
            }
        }
    }

    if (files.Count > 0)
    {
        lock (_localFilePaths)
        {
            _localFilePaths.Clear();
            _localFilePaths.AddRange(files);
        }

        var sizes = new long[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            try
            {
                var info = new FileInfo(files[i]);
                sizes[i] = info.Exists ? info.Length : 0;
            }
            catch (Exception)
            {
                sizes[i] = 0;
            }
        }

        payload = new ClipboardPayload
        {
            OriginDeviceId = state.LocalDeviceId,
            Version = ++_version,
            Format = ClipboardFormats.Files,
            FilePaths = files.ToArray(),
            FileSizes = sizes,
            IsLazy = true  // 懒加载：粘贴时才传输
        };

        AppLog.Event("INFO", "clipboard.files.detected", "count=" + files.Count + " lazy=true");

        var announceHandler = FileClipboardAnnounced;
        if (announceHandler != null)
        {
            announceHandler(this, new FileClipboardDetectedEventArgs(files.ToArray()));
        }
    }
}
```

**关键改动：**
1. 文本和图片检测优先于文件
2. 文件传输保持懒加载模式（`IsLazy = true`）
3. 添加日志标记 `lazy=true`

---

## 🔧 问题 2：MainWindow 窗口最小化

### 文件位置
`src\Desktop.Agent\MainWindow.xaml.cs`

### 修复方法

找到 `OnLoaded` 方法（约第 144 行），在设置完版本信息后添加：

```csharp
private void OnLoaded(object sender, RoutedEventArgs e)
{
    VersionText.Text = "LanMouseShare " + AppVersion;
    UpdatePauseButton();
    UpdateControlTargetText();
    AppLog.Info("Agent window loaded. Version=" + AppVersion);

    // 添加这段代码 - 检查是否需要静默启动
    var app = Application.Current as App;
    if (app != null && app.StartMinimized)
    {
        Hide();
        WindowState = WindowState.Minimized;
        AppLog.Event("INFO", "window.minimized", "reason=startup_parameter");
    }

    EnsureLocalServiceAvailable();
    
    // ... 其余代码保持不变
}
```

**关键改动：**
1. 检查 App.StartMinimized 属性
2. 如果为 true，隐藏窗口并最小化到托盘
3. 记录日志

---

## 🔧 问题 3：边角阻挡功能验证

### 文件位置
`src\Input.Native\EdgeSwitchDetector.cs`

### 验证步骤

检查 `IsInBlockedCorner` 方法（约第 84 行）是否存在：

```csharp
public static bool IsInBlockedCorner(Point point, Size screenSize, string peerDirection, int cornerBlockPixels)
{
    var size = cornerBlockPixels < 0 ? 0 : cornerBlockPixels;
    if (size == 0 || screenSize.Width <= 0 || screenSize.Height <= 0)
    {
        return false;
    }

    if (peerDirection == "Left" || peerDirection == "Right")
    {
        var atHorizontalEdge = peerDirection == "Left"
            ? point.X <= 0
            : point.X >= screenSize.Width - 1;
        return atHorizontalEdge && (point.Y < size || point.Y >= screenSize.Height - size);
    }

    if (peerDirection == "Up" || peerDirection == "Down")
    {
        var atVerticalEdge = peerDirection == "Up"
            ? point.Y <= 0
            : point.Y >= screenSize.Height - 1;
        return atVerticalEdge && (point.X < size || point.X >= screenSize.Width - size);
    }

    return false;
}
```

**如果方法存在且正确，边角阻挡功能应该正常工作。**

如果不工作，检查 MainWindow.xaml.cs 中：
- `_blockScreenCorners` 是否正确读取配置
- `BlockCornersBox.IsChecked` 是否正确绑定到 `_blockScreenCorners`

---

## ✅ 修复验证清单

### 1. 编译前检查
- [ ] ClipboardSyncService.cs - 检测顺序：文本 -> 图片 -> 文件
- [ ] App.xaml.cs - 包含 `StartMinimized` 属性
- [ ] StartupTaskManager.cs - 包含 `/minimized` 参数
- [ ] MainWindow.xaml.cs - OnLoaded 检查 StartMinimized

### 2. 编译
```powershell
cd "C:\Users\yangyang\Desktop\局域网键盘鼠标共享"
.\scripts\Build-Release.ps1
```

### 3. 测试

**测试文本共享：**
1. 电脑 A 复制一段文本
2. 电脑 B 粘贴 - 应该直接出现

**测试图片共享：**
1. 电脑 A 截图（Ctrl+C）
2. 电脑 B 粘贴到画图 - 应该直接出现

**测试文件传输：**
1. 电脑 A 复制一个文件
2. 电脑 B 按 Ctrl+V
3. 文件应该开始传输并粘贴

**测试静默启动：**
1. 设置开机自启动
2. 注销并重新登录
3. Agent 应该在托盘，不显示窗口

**测试边角阻挡：**
1. 启用"跨屏边角阻挡"
2. 从角落（80px 内）尝试切屏 - 应该被阻止
3. 从中间切屏 - 应该正常工作

---

## 📝 常见问题

### Q: 编译错误 "App.StartMinimized 不存在"
A: 确保 App.xaml.cs 已正确修改，包含 `public bool StartMinimized { get; private set; }`

### Q: 文本还是不能共享
A: 检查 ClipboardSyncService.cs 的检测顺序，确保文本检测在文件之前

### Q: 窗口还是显示
A: 检查 MainWindow.xaml.cs 的 OnLoaded 方法，确保添加了最小化逻辑

### Q: 边角阻挡还是不工作
A: 
1. 检查配置文件 `C:\ProgramData\LanMouseShare\config.json`
2. 确保 `BlockScreenCorners: true`
3. 重启 Agent 使配置生效

---

## 🚀 快速应用

如果你想快速应用所有修复：

```powershell
# 1. 运行自动修复脚本
cd "C:\Users\yangyang\Desktop\局域网键盘鼠标共享"
.\scripts\Apply-Fixes.ps1

# 2. 在 Visual Studio 中手动修复剪贴板代码

# 3. 编译
.\scripts\Build-Release.ps1

# 4. 打包
.\scripts\Package-Release.ps1

# 5. 安装测试
```

---

**版本**: 0.2.0-beta-fix1  
**更新日期**: 2026-10-27
