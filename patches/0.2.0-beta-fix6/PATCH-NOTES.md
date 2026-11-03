# LanMouseShare 0.2.0-beta-fix6 补丁说明

日期：2026-10-29

## 修复内容

### 1. 文件粘贴不传输（fix5 装机实测暴露，双机日志 + 本地复现程序定位）

**现象**：fix5 后右键"粘贴"不灰了，但点粘贴没有任何传输（日志：发布成功、零 fetch/零 pull）。

**根因（三个叠加）**：

1. **剪贴板格式 ID 符号错位**：`FORMATETC.cfFormat` 是带符号 short（如 -16172），而 `RegisterClipboardFormat` 返回 0xC000-0xFFFF 区间的正 int（如 49364）。GetData/QueryGetData 拿负数比较正数格式 ID 恒为 false → 所有格式都被拒（QueryGetData 恒返回 DV_E_FORMATETC、GetData 恒抛 "Unsupported clipboard format"）→ Explorer 拿不到任何内容 → 粘贴静默失败。
2. **GetDataHere 未实现**：Explorer 读 FileContents 的标准路径是 GetDataHere（调用方提供内存），旧实现恒抛异常。
3. **HGLOBAL 分配堆损坏**：AllocHGlobal 用 GMEM_MOVEABLE 分配但未 GlobalLock 就 Marshal.Copy，写坏全局堆 → Agent 进程崩溃隐患（本地复现程序 100% 复现进程死亡；此前被符号 bug 掩护从未执行到）。

**修复**：

1. NormalizeCf 归一化（负值 +65536）应用于 GetData/QueryGetData 全部格式比较。
2. 完整实现 GetDataHere：HGLOBAL（GlobalReAlloc+GlobalLock+拷贝+GlobalUnlock）与 ISTREAM（写入调用方流）两分支。
3. AllocHGlobal 改 GMEM_FIXED。
4. Contents 的 lindex<0 请求（OLE flush/批量渲染会用）直接拒绝 DV_E_FORMATETC，保证"粘贴时才传输"不被 flush 触发。

**本地验证**（反射直调测试，绕开剪贴板查看器干扰，全绿）：
- QueryGetData：Desc(HG,-1)=0、Contents(HG,-1)=DV_E_FORMATETC（flush 不预取）、Contents(HG,0)=0、Contents(ISTREAM,0)=0、Drop=0
- GetData：Desc 596B、Contents(HG,0) 触发拉取 65536B、ISTREAM 惰性流可读、Drop 4B
- GetDataHere：HG,0 65536B、HG,-1 拒绝
- 进程完整跑完，无崩溃

### 2. "打开接收目录"按钮与实际接收目录不一致

**现象**：拖动传输实际保存在 `C:\Users\Public\Desktop\Mouse传输文件`，界面"打开"按钮却打开 `C:\Users\Administrator\Desktop\Mouse传输文件`。

**根因**：Agent 界面用 `Environment.SpecialFolder.DesktopDirectory`（当前用户桌面），而服务端 `PeerCoordinator.ResolveIncomingFolder()` 用公共桌面 `%PUBLIC%\Desktop`。

**修复**：Agent 的 `GetTransferDirectory()` 改为与服务端相同的公共桌面三级回退逻辑（PUBLIC → CommonDesktopDirectory → TempPath）。

### 3. 已确认修复、本版未动

- 返回路径边角阻挡（fix4 修复）：双机实测生效（B 屏左下角推回被挡、中部正常返回），不再改动。

## 版本

- 主版本：0.2.0-beta-fix6（BuildInfo 单一版本源）。
- 安装器 Setup 保持 0.2.1（升级判定用纯数字版本，不变更）。

## 安装方法

1. 双击 `LanMouseShareSetup.exe` 按提示安装（需要管理员权限）。
2. 或便携覆盖：退出 Agent、`net stop LanMouseShare`，备份 `C:\Program Files\LanMouseShare`，用包内 `Agent\`、`Service\` 目录覆盖同名目录，再 `net start LanMouseShare` 并启动 Agent。
3. **两台电脑都必须升级到 fix6**（A=Win10 主控、B=Win7 被控都要装）。
4. 升级后重启 Agent；建议两台电脑都重启一次以验证自动启动。

## 验证要点

1. A 复制文件 → B 右键"粘贴"菜单立即可用（不灰）；点"粘贴"后才开始传输（不是复制时就传），完成后文件出现在 B 的目标位置。
2. 反向同样：B 复制文件 → A 粘贴时才传输（B=Win7 走 GetDataHere HGLOBAL 分支）。
3. 界面"打开接收目录"按钮打开 `C:\Users\Public\Desktop\Mouse传输文件`。
4. 边角阻挡：从 B 屏左下/右下角推回不切回，中部推回正常。
