# Linux 端开发与使用

Linux 桌面端与 Windows 端共享模型、OpenAI 兼容的流式请求和 Markdown 笔记代码；UI 为 .NET 8 + GTK 3，划词检测为 X11 PRIMARY 选区。当前为 Linux 首版，目标为 x86_64 Xorg 桌面。

## 依赖与运行

Ubuntu 22.04/24.04 示例：

```bash
sudo apt install libgtk-3-0 libx11-6 xclip
dotnet run --project src/Linux/AiSelectionToolbar.Linux.csproj
```

开发机还需 .NET 8 SDK；发布的 `linux-x64` 自包含压缩包不需要另行安装 .NET，但仍依赖 GTK 3、X11 和 `xclip`。登录时选择 Xorg 会话。解压 CI 工件后，运行 `AiSelectionToolbar.Linux`。首次启动会打开设置；后续可通过托盘进入，或用 `AiSelectionToolbar.Linux --settings` 打开设置。配置 API 地址、模型和密钥后，在其他程序中划词；也可选中文字后按 Ctrl+Shift+Space。

设置存于 `${XDG_CONFIG_HOME:-~/.config}/ai-selection-toolbar/settings.json`；API 密钥另存为仅当前用户可读的 `api-key`，与 Windows DPAPI 文件不能互换。历史存于 `${XDG_DATA_HOME:-~/.local/share}/ai-selection-toolbar/history.db`。联网只发生在用户点击 AI 操作后；云端地址必须为 HTTPS，本机回环可用 HTTP。

## 已知边界

- 原生 Wayland 不允许普通应用读取其他程序的全局选区、指针与快捷键；本实现要求 X11，Wayland/XWayland 混合会话不作为支持目标。
- X11 PRIMARY 由目标程序自行提供。无法选中文字的控件、扫描 PDF 和不公开 PRIMARY 的程序无法取词。工具栏位置以鼠标指针为锚点，不是文本的精确字形边界。
- 某些桌面没有传统系统托盘区域，GTK StatusIcon 可由支持 StatusNotifier/AppIndicator 的桌面环境显示；其余环境可通过窗口或进程管理器退出。
- CI 验证编译、打包、无 DISPLAY 的启动保护及 Xvfb 下的 GTK 启动。需要在 Xorg 桌面上实测不同应用的划词、托盘、窗口定位与 API 结果。

Linux 端构建命令：

```bash
dotnet build src/Linux/AiSelectionToolbar.Linux.csproj -c Release
dotnet publish src/Linux/AiSelectionToolbar.Linux.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o dist/linux-x64
```
