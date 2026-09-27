# AI-toolbar 全面测试报告

测试日期：2026-09-27
测试分支：`linux-desktop`
测试 Agent：`.codex/agents/ai-toolbar-tester.toml`
测试原则：只读测试，未修改业务代码、配置、测试数据或安装环境；临时产物均位于 `/tmp`。

## 1. 总结

Linux 纯逻辑、设置/历史、API SSE、管理 API 鉴权、PySide6 GUI、PyInstaller 构建以及临时目录安装/卸载测试通过。当前没有依据充分的 P0/P1 产品缺陷。

有三类未完成验证：

- Windows 构建因当前 Linux 环境缺少 `.NET Framework 4.8` reference assemblies 阻塞。
- PySide6 X11/Xvfb 已在临时显示器中完成启动和控件 smoke；真实 Xorg/Wayland 仍未执行。
- 真实 Xorg/Wayland、Windows 实机、目标应用划词、多显示器/HiDPI 视觉交互、外部真实 API 和长时间稳定性未执行。

## 2. 测试环境

| 项目 | 实际值 |
|---|---|
| OS/内核 | Linux 6.8.0-138-generic x86_64 |
| 系统 Python | 3.10.12；缺少项目安装和 `PySide6`，直接运行推荐命令会导入失败 |
| 项目 Python | `.venv/bin/python` 3.10.12；`PySide6 6.9.3`；PyInstaller 6.22.3 |
| Qt | PySide6 offscreen 逻辑测试可用；X11 GUI 连接未建立 |
| .NET | `/usr/bin/dotnet` 8.0.131；无 `msbuild` 命令，dotnet 构建缺少 .NET Framework 4.8 reference assemblies |
| X11 工具 | `xvfb-run`、`xclip`、`xauth` 存在；临时 Xvfb 可被 Qt 连接，真实桌面 DISPLAY 为空 |
| 当前 DISPLAY | 空；`WAYLAND_DISPLAY` 空；`XDG_SESSION_TYPE=tty` |

## 3. 测试矩阵

状态含义：通过 = 有命令或实验结果证据；环境阻塞 = 测试未执行且不能归因于产品；未执行 = 需要真实平台或外部条件。

| 类别 | 测试内容 | 命令/输入 | 预期与实际 | 状态 |
|---|---|---|---|---|
| 静态 | Python 编译 | `python3 -m compileall -q src/Linux tests` | 退出码 0 | 通过 |
| 静态 | Shell 语法 | `bash -n packaging/linux/*.sh` | 4 个脚本均退出码 0 | 通过 |
| 静态 | Shell 安全检查 | `shellcheck packaging/linux/*.sh` | 4 个脚本均无诊断 | 通过 |
| 静态 | HTML/JS | `xmllint --html --noout ...`、`node --check src/Desktop/ManagementPage.js` | JS 退出码 0；HTML 解析器报告 HTML5 语义标签诊断但退出码 0，不能作为 HTML 失败证据 | 通过/需浏览器复核 |
| 回归 | Linux unittest | `QT_QPA_PLATFORM=offscreen .venv/bin/python -m unittest discover -s tests -p 'test_linux_*.py'` | 9/9，`OK` | 通过 |
| 回归 | UI unittest | 同上，包含 `tests/test_linux_ui.py` | 12/12，`OK` | 通过 |
| 设置/安全 | profile、迁移、切换、密钥不落盘、仅剩 profile 不可删除 | 现有 unittest + 临时 keyring mock | 测试通过；`settings.json` 不含测试密钥，public settings 不暴露 `ProtectedApiKey` | 通过 |
| API | prompt 校验 | mock 输入 `ask` + 空问题 | 抛出 `ValueError` | 通过 |
| API | SSE 流式回答 | 本地 mock 返回 reasoning chunk、两个 answer chunk、`[DONE]` | reasoning 不进入答案；收到 `你好`、`，世界`；完成值为 `你好，世界` | 通过 |
| API | HTTP 错误 | 本地 mock 返回 HTTP 500 `bad upstream` | `failed` 信号为 `接口返回 HTTP 500：bad upstream` | 通过 |
| 管理 API | 未授权/错误 Origin/授权读取 | 临时目录 + `127.0.0.1` 随机端口 | 401/403/200，符合预期 | 通过 |
| 管理 API | 非回环 HTTP 地址 | `http://example.com/v1` | `SettingsError` 拒绝 | 通过 |
| 管理 API | 设置颜色校验 | 错误颜色输入 | 需带正确 Origin 后验证；本轮第一次请求因测试脚本 Origin 多了 `/` 得到 403，未将其计为产品失败 | 部分执行 |
| 管理 API | 历史接口 | 临时历史库新增记录后 GET `/api/history?limit=1` | HTTP 200 | 通过 |
| 构建 | PyInstaller 单文件 | 项目 CI 同等参数，输出定向到 `/tmp` | 构建成功；HTML/CSS/JS/AppIcon 均嵌入 ELF | 通过 |
| 启动保护 | 无 DISPLAY | 冻结程序，移除 `DISPLAY`/`WAYLAND_DISPLAY` | 退出码 2，输出 X11 要求提示 | 通过 |
| GUI | Xvfb 应用启动 | `timeout 12s xvfb-run -a ... AiSelectionToolbar.Linux --settings` | 进程持续运行至 timeout 退出码 124，无启动异常 | 通过 |
| GUI | PySide6 控件 smoke | 临时 Xvfb；工具栏按钮、定位、回答 Markdown、固定按钮 | 7 个按钮；点击“了解”发出 `explain`；工具栏位于屏幕内；回答完成、Markdown 和固定切换通过；截图保存到 `/tmp` | 通过 |
| GUI | 扩展回归 | 临时 Xvfb；右下角定位、短代码块、长回答、固定/重置 | 右下角未越界；短文档 58px、viewport 85px；长回答可滚动且受屏幕高度限制 | 通过 |
| 打包 | `.run` 生成 | `bash packaging/linux/make-installer.sh ...` | 生成 `/tmp/ai-toolbar-installer/AISelectionToolbar-linux-x64.run` | 通过 |
| 安装 | 临时 HOME 安装 | 执行 `.run`，检查可执行文件和 desktop 文件 | 安装成功，文件存在 | 通过 |
| 卸载 | 临时 HOME 卸载 | 执行 `uninstall.sh`，检查目标文件 | 程序和 desktop 文件删除；未触碰真实用户数据 | 通过 |
| Windows | .NET 构建 | `dotnet build src/Desktop/AiSelectionToolbar.Desktop.csproj --configuration Release` | MSB3644：缺少 `Microsoft.NETFramework.ReferenceAssemblies v4.8` | 环境阻塞 |
| Windows | 安装包/目标程序 | Windows package smoke、真实 Windows | 当前环境无 Windows/ISCC/WPF 桌面 | 未执行 |

## 4. 白盒覆盖范围

已通过或动态触达的主要路径：

- `src/Linux/ai_selection_toolbar/settings.py`：默认值合并、profile 创建/切换、远程 HTTP 拒绝、密钥通过 mock keyring 保存、JSON 脱敏。
- `src/Linux/ai_selection_toolbar/api.py`：`ask` 空问题校验、SSE `delta.content` 解析、reasoning 字段忽略、`[DONE]`、HTTPError。
- `src/Linux/ai_selection_toolbar/management.py`：loopback 监听、Bearer token、Origin 校验、设置/历史 GET 路径。
- `packaging/linux/make-installer.sh`、`install.sh`、`uninstall.sh`：语法、ShellCheck、`.run` 生成、临时 HOME 安装与卸载。
- PyInstaller 数据文件收集：管理页三件资源和 `AppIcon.svg` 均在冻结产物中发现。
- GUI 证据：修复前回答窗口为 760x223，文档高度 79、viewport 高度 68、滚动范围 11；修复后最小高度为 240，短代码文档高度 58、viewport 高度 85，短内容不再发生隐藏溢出。工具栏截图为 472x49。

未形成覆盖率报告，也没有声称代码覆盖率达标。以下路径仍需平台测试或专门回归：取消请求竞态、真实 keyring/Secret Service、X11 PRIMARY 读取、托盘和窗口视觉状态、自动启动、长时间运行、Windows WPF/SQLite/DPAPI/安装器。

## 5. 缺陷与风险

### P2：Windows 交付链路未在本机验证

- 状态：环境阻塞，不判定为产品缺陷。
- 现象：`dotnet build` 报 `MSB3644`，缺少 `.NETFramework,Version=v4.8` reference assemblies。
- 影响：本轮不能确认 WPF 编译、SQLite 原生库、Windows 安装器和 Windows 目标程序划词。
- 下一步：使用 Windows 2022 CI 或安装 .NET Framework 4.8 Developer Pack 后执行项目规定的 `msbuild` 和 `tests/windows-package-smoke.ps1`。

### P3：系统 Python 直接运行推荐命令会误导为导入失败

- 现象：系统 `python3` 缺少 `PySide6` 和 `ai_selection_toolbar`；`.venv/bin/python` 测试通过。
- 判断：属于开发环境未激活/未安装项目，不是已确认产品缺陷。
- 建议：开发文档和测试 Agent 命令统一显式使用 `.venv/bin/python`，或先执行安装步骤。

### P3：回答窗口短内容存在隐藏滚动区域——已修复

- 修复：`AnswerWindow._fit_to_answer()` 将自动收缩下限从 `220` 调整为 `240`，并新增 `tests/test_linux_ui.py` 回归覆盖。
- 证据：修复后短代码块文档高度 58、viewport 高度 85；UI unittest 12/12 通过，扩展 Xvfb 回归通过。
- 仍需确认：真实桌面字体、缩放比例和极端 Markdown 内容需要在 Xorg/多显示器环境复验。

## 6. 未覆盖与下一轮建议

1. 在真实 Xorg 上重跑托盘、工具栏定位、回答窗口 resize/滚动/固定/关闭、X11 PRIMARY 和 `xclip` 缺失分支。
2. 使用本地 mock server 重复验证快速取消、取消后再次请求、空回答、非流式 JSON、超时和长回答；记录首 chunk、完成时间及重复次数。
3. 在 Windows 11 和 Windows 7 SP1（若仍需支持）执行 WPF、DPAPI、SQLite、管理页 token/Origin、安装/升级/卸载和目标应用矩阵。
4. 做浏览器真实交互验收：设置加载保存、profile 切换、颜色错误提示、历史索引、删除/清空、Markdown 和响应式布局。
5. 检查当前工作区已有 20 个用户修改文件；本轮没有对这些修改作代码审查，也没有提交、推送或清理它们。

## 7. 交付变更

本轮修改 `src/Linux/ai_selection_toolbar/ui.py` 修复短回答布局，并新增 `tests/test_linux_ui.py` 回归测试；更新本报告文件。没有修改其他业务模块、配置或安装环境，没有执行 `git commit`、`git push` 或发布操作。
