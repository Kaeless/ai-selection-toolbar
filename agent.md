# AI-toolbar 项目上下文

## 项目位置

- 本地开发目录：`/home/cao/code/AI-toolbar`
- GitHub：`https://github.com/Kaeless/ai-selection-toolbar`
- 仓库当前为公开仓库。
- 当前主要开发分支：`linux-desktop`
- 默认分支：`main`
- 当前已完成的最新提交：`136d918 fix(linux): improve selection toolbar layout`

## 项目目标

这是一个 AI 划词助手。用户选中文字后，在文字附近显示工具栏，可调用兼容 OpenAI Chat Completions 的 API 执行解释、翻译和自定义操作。

Windows 继续使用现有 C# 实现；Linux 使用 Python/PySide6 和 X11。配置页面采用本机回环 B/S 管理页，可配置多个 API，并选择当前使用的 API。

## 已完成改造

- Linux 客户端从旧 GTK/C# 路线迁移到 PySide6。
- Linux 配置页面由本机 `127.0.0.1` 服务提供，浏览器访问管理。
- Windows/Linux 均支持多个 API 配置和当前 API 切换。
- 管理页面采用 Apple 风格的浅色卡片、圆角和蓝色交互样式。
- Linux 工具栏改为深色不透明背景，提升文字可读性。
- Linux 工具栏位置使用 Qt 全局鼠标坐标，处理 X11 与 HiDPI 坐标差异，优先显示在选中文字下方，空间不足时翻到上方。
- Linux 回答窗口会随流式输出内容自动增高，超过上限后滚动显示。
- Linux 回答窗口支持“固定/取消固定”，固定后切换选中文字不会自动关闭回答。
- Linux 回答内容会按滚动条实际占用的 viewport 宽度动态重新排版，避免文字被滚动条遮挡。
- Linux 历史记录兼容旧版 C# SQLite 表结构，管理页索引位于左侧且不再显示分页按钮。
- Linux 发布包包含 `AiSelectionToolbar.Linux`、`install.sh`、`uninstall.sh` 和 `AppIcon.svg`。
- Linux CI 还会生成无需解压、可直接运行的 `AISelectionToolbar-linux-x64.run` 安装包；Windows CI 生成 Inno Setup 安装 EXE。
- 管理页面从后端动态显示当前版本和作者；当前版本为 `0.5.1`，作者为 `Kaeless`。

## 重要目录

- `src/Core/`：Windows/Linux 共用的配置和 API 模型。
- `src/Desktop/`：Windows 桌面端、管理页面和静态资源。
- `src/Linux/ai_selection_toolbar/`：PySide6 Linux 实现。
- `packaging/linux/`：Linux 安装和卸载脚本。
- `tests/`：Linux 配置、位置、历史等测试。
- `docs/LINUX.md`：Linux 构建、运行和限制说明。

## Linux 运行要求

- Python 3.10+（发布包自带运行时）。
- X11/Xorg 会话；原生 Wayland 不作为支持目标。
- `xclip`、`libx11-6`、`libxcb-cursor0`。
- 选中文字后依赖 X11 PRIMARY selection。
- 首次启动或使用 `--settings` 时打开浏览器管理页；正常启动主要驻留系统托盘。

源码运行：

```bash
cd /home/cao/code/AI-toolbar
. .venv/bin/activate
ai-selection-toolbar
```

发布包安装：

```bash
tar -xzf AISelectionToolbar-linux-x64-v0.5.1.tar.gz
cd AISelectionToolbar-linux-x64
./install.sh
```

直接打开配置页：

```bash
~/.local/opt/ai-selection-toolbar/AiSelectionToolbar.Linux --settings
```

## 测试与构建

```bash
cd /home/cao/code/AI-toolbar
.venv/bin/python -m unittest discover -s tests -p 'test_linux_*.py'
.venv/bin/python -m py_compile src/Linux/ai_selection_toolbar/app.py src/Linux/ai_selection_toolbar/ui.py
```

PyInstaller 构建入口参考 `.github/workflows/linux-build.yml`。构建时必须包含管理页面文件和 `src/Desktop/Assets/AppIcon.svg`。

## 最新发布

- 版本：`v0.5.1`
- 发布页：`https://github.com/Kaeless/ai-selection-toolbar/releases/tag/v0.5.1`
- Linux 下载：`https://github.com/Kaeless/ai-selection-toolbar/releases/download/v0.5.1/AISelectionToolbar-linux-x64-v0.5.1.run`
- SHA256：`35b6de9e7fe8a80f6ae40b374004aaebea6e8e9ebc4d007e8452584845890a94`

## 后续修改约定

- 面向用户的说明使用简体中文。
- 修改前先检查 `git status --short`、当前分支和相关代码。
- 只修改与当前问题直接相关的文件，不做无关格式化或重命名。
- 修改代码后运行适用的测试、编译和构建检查。
- 每次代码修改完成并通过测试后，必须重新编译生成 Linux 可执行安装包（`.run`，同时保留压缩包备用）。
- 未得到用户明确要求时，不执行 `git commit`、`git push`、创建 Release 或修改远程仓库。
- 不提交 API 密钥、Token、个人配置、`.venv` 或临时构建目录。
- `sources/`（如存在）只作为参考，不修改。

## 当前已知边界

- Linux 选区检测依赖 X11 PRIMARY，Wayland 原生会话无法保证可用。
- 不同桌面环境的系统托盘显示、HiDPI 缩放和多显示器定位仍建议实机验证。
- 本地静态检查和 Xvfb 启动检查不等同于所有桌面环境中的视觉验收。
