# Linux 端开发与使用

Linux 桌面端使用 Python 3.10+ 与 PySide6，划词检测基于 X11 PRIMARY 选区。设置页面由仅监听 `127.0.0.1` 的本机服务提供，可在浏览器中管理多个 API，并选择工具栏当前使用的连接。目标环境为 x86_64 Xorg 桌面。

## 依赖与运行

Ubuntu 22.04/24.04 示例：

```bash
sudo apt install libx11-6 libxcb-cursor0 xclip python3 python3-venv
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -e .
ai-selection-toolbar
```

发布的 Linux 安装包包含 Python 与 Qt 运行时，但仍依赖 X11 和 `xclip`。登录时选择 Xorg 会话。优先使用可执行安装包 `AISelectionToolbar-linux-x64.run`：下载后直接运行即可，无需手动解压；它会注册应用菜单项。压缩包仍作为备用方式，解压后运行 `./install.sh`。也可以直接运行 `./AiSelectionToolbar.Linux`。首次启动会打开浏览器管理页；后续可从托盘进入，或用 `AiSelectionToolbar.Linux --settings` 打开。运行 `./uninstall.sh` 可卸载程序，设置和历史仍保留。

设置存于 `${XDG_CONFIG_HOME:-~/.config}/ai-selection-toolbar/settings.json`。API 密钥通过系统 keyring（Secret Service）保存，不写入 JSON；Windows 继续使用 DPAPI，因此两个平台可共用配置字段，但密钥需要分别录入。历史存于 `${XDG_DATA_HOME:-~/.local/share}/ai-selection-toolbar/history.db`。联网只发生在用户点击 AI 操作后；云端地址必须为 HTTPS，本机回环可用 HTTP。

## 已知边界

- 原生 Wayland 不允许普通应用读取其他程序的全局选区、指针与快捷键；本实现要求 X11，Wayland/XWayland 混合会话不作为支持目标。
- X11 PRIMARY 由目标程序自行提供。无法选中文字的控件、扫描 PDF 和不公开 PRIMARY 的程序无法取词。工具栏以拖选松开位置作为选区末端锚点，优先显示在其下方；空间不足时翻到上方。
- 某些桌面没有系统托盘区域，此时仍可用 `--settings` 打开管理页，并通过进程管理器退出。
- CI 验证 Python 单元测试、打包、无 DISPLAY 的启动保护及 Xvfb 下的 PySide6 启动。不同应用中的划词和多显示器定位仍需在 Xorg 桌面实测。

Linux 端构建命令：

```bash
python -m unittest tests/test_linux_settings.py tests/test_linux_position.py
pyinstaller --onefile --name AiSelectionToolbar.Linux --paths src/Linux src/Linux/launcher.py
```

`src/Linux` 中原有的 GTK/C# 文件只作为迁移参考保留，不再由 Linux CI 构建，也不进入发布包。
