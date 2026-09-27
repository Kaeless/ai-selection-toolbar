# AI 划词工具栏：开发原型

本目录是接续原项目需求方案的代码原型。以远程仓库 `Kaeless/ai-selection-toolbar` 的 `main` 为基线，通过独立分支和 Pull Request 集成代码；不要将本地初始化的 Git 历史强制推送到远端。

## 模块

- `src/Selection/`：全局鼠标拖选和 Ctrl+Shift+Space 快捷键触发；先检查前台与焦点进程的排除名单，再通过 Windows UI Automation `TextPattern` 获取选区。标准 Unicode Win32 `Edit` 控件可走 `EM_GETSEL`/`WM_GETTEXT` 有界消息回退；不读取密码控件，不通过模拟复制改变剪贴板。
- `src/Core/`：OpenAI 兼容的流式请求和取消、DPAPI 加密的 API 密钥、SQLite 历史、Markdown 笔记。
- `src/Desktop/`：.NET Framework 4.8 WPF 浮窗，以及只监听 `127.0.0.1` 的本机设置与历史管理页。管理页通过随机会话令牌访问本机 API。

已配置模型时程序收进系统托盘，划词后显示浮窗；首次未配置时显示窗口以便打开管理页。快捷键被其他程序占用时仍保留鼠标取词，并给出提示。远程 API 地址必须使用 HTTPS；HTTP 只允许回环地址上的本地 API。

用户点击解释、翻译或提问后，选中文字才会发往所配置的地址。未配置地址和模型时不发送。笔记需先完成解释并由用户编辑确认，同一来源每天追加到 `YYYY-MM-DD_来源_学习.md`。

## Windows 构建

在安装了 .NET Framework 4.8 Developer Pack 和 Visual Studio 2019/2022 的 Windows x64 开发机上：

```powershell
msbuild src\Desktop\AiSelectionToolbar.Desktop.csproj /t:Restore,Build /p:Configuration=Release /p:Platform=AnyCPU
```

工程目标进程为 x64。NuGet 包 `System.Data.SQLite.Core` 固定为 `1.0.119`；发布时确认输出目录含 `x64/SQLite.Interop.dll` 和托管提供程序，再将 Release 输出目录打包。API 密钥由当前 Windows 用户的 DPAPI 保护；复制设置文件到其他用户账户无法解密。

## 打包与安装

CI 在 `windows-2022` 构建后上传 `windows-x64-packages` 工件，包含 `AISelectionToolbar-0.5.1-win-x64-setup.exe` 安装程序和便携压缩包。Windows 本机也可运行 `ISCC.exe installer\AiSelectionToolbar.iss`，将安装程序生成到 `dist`。安装版可直接运行，无需手动解压；便携版仍需解压后运行 `AiSelectionToolbar.Desktop.exe`。

两种版本都要求目标计算机已安装 .NET Framework 4.8 或更高版本；安装程序会在安装前检查，便携版需用户自行确认。安装程序按当前用户安装到 `%LOCALAPPDATA%\Programs\AiSelectionToolbar`。登录启动可以在设置中开启，默认关闭；移动便携版后重新运行，会更新已启用的启动项路径。笔记默认在用户“文档”中的 `AiSelectionToolbar\Notes`，设置中可指定绝对目录。

## 必须在 Windows 验证

1. Win7 SP1 x64 与 Win11：启动、SQLite 原生库加载、配置与管理页；验证本机管理页无法从其他设备访问。
2. Chrome、Edge、Firefox、Word、PowerPoint、Adobe Acrobat Reader 及其他目标程序：拖选后的文字、位置、标题及排除程序。UI Automation provider 的能力由应用版本决定；标准 Edit 回退仅有控件边界而非字形位置，自绘编辑器、无 TextPattern provider 的控件与扫描版 PDF 无法保证取词。
3. 云端或本地兼容 API：流式结果、停止、错误与首次未配置时不发送；历史分页搜索/删除/清空、笔记按日追加和重复提醒。
4. 安装包和便携版的启动、卸载及文件完整性；登录启动开关和自定义笔记目录。检查更新及其提示仍待实现。

当前 Linux 工作区没有 .NET Framework/MSBuild。本分支的 GitHub Actions `windows-2022` 已通过编译、打包、安装/便携 SQLite 读写、桌面程序启动、本机管理页和卸载检查；详见[Windows 实机验收记录](./WINDOWS-ACCEPTANCE.md)。这些结果不等同于 Windows 7/11 实机运行或目标程序兼容性验收。
