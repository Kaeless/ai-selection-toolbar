# Windows 0.5.3 测试范围

基线：windows-desktop ddd3e39；界面来源：linux-desktop 5945529。
范围：同步三份管理页资源；修正 CI/包测试版本号；增加 Windows 桌面、管理 API、浏览器交互测试与真实渲染截图。
验收：Windows CI 编译、安装卸载、SQLite、SSE、DPAPI、管理 API 和桌面用例结果均可追溯，截图由实际 WPF 和实际 Windows 本机服务生成。
模型响应采用确定性的测试替身；不调用付费模型。Windows 7/11 实机、全局划词和快捷键不在 CI 自动测试结论内。
改动可通过撤销测试分支提交回退；测试仅使用 GitHub 临时 Windows 运行器的用户配置。
