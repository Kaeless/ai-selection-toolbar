# 本机管理 API

Windows 与 Linux 共用 `src/Features/ManagementUI/` 中的静态浏览器界面。各平台后台分别启动本机 HTTP 服务，动态端口绑定到 `127.0.0.1`，页面令牌由桌面进程在启动时生成并放在 URL fragment 中。前端读取令牌后立即清除地址栏 fragment，只将其放入 API 的 `Authorization: Bearer <token>` 请求头。

## 共用接口

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `GET` | `/api/settings` | 读取非密钥设置；密钥字段永不返回 |
| `POST` | `/api/settings` | 保存自动显示、登录启动、语言、笔记目录、工具栏样式和自定义操作 |
| `POST` | `/api/connection` | 保存 API 地址、模型和可选密钥 |
| `POST` | `/api/exclusions/add` | 添加排除程序 `{ "application": "程序名" }` |
| `POST` | `/api/exclusions/remove` | 移除排除程序 |
| `GET` | `/api/history?search=<文本>&offset=<偏移>` | 搜索并分页读取历史，每页 50 条 |
| `POST` | `/api/history/delete` | 删除单条 `{ "id": 1 }` |
| `POST` | `/api/history/clear` | 清空历史 |

历史条目使用共同字段 `Id`、`TimeUtc`（Unix 毫秒；Windows .NET Framework 序列化为 `/Date(ms)/` 字符串）、`Action`、`Selection`、`Result`、`Application`、`SourceTitle` 和 `Prompt`。平台可以采用不同的本地数据库结构，但需映射到该响应格式。

## 安全与平台差异

- 所有管理请求仅接受回环客户端；`Host` 必须是当前服务的 `127.0.0.1:<port>`。
- API 请求必须提供随机会话令牌。写请求还必须来自当前管理页面的同源 `Origin`，并使用 JSON 内容类型。
- 远程 API 地址必须使用 HTTPS；只有本机回环地址允许 HTTP。
- API 密钥只通过写请求发送并由后台保存。Windows 使用 DPAPI；Linux 使用仅当前用户可读的文件。
- 登录启动由各平台实现：Windows 更新当前用户启动项，Linux 更新当前用户 XDG autostart 文件。
- 文件路径输入保持平台原生格式；不允许管理页面读取或上传所选程序文件。
