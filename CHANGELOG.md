# 更新日志

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [未发布]

### 修复
- 服务返回网页（如被限流、被代理拦截）时，错误提示只显示网页标题，不再显示整段 HTML 源码。

### 新增
- 推送 `v*` 版本标签后自动编译、测试、打包两个安装包，并创建 Release 草稿。
- 问题反馈与功能建议模板、贡献指南、安全说明。

## [1.0.0] - 2026-10-03

首个版本。

### 新增
- 基于 Windows 实时辅助字幕的实时语音翻译，主窗口与可鼠标穿透的悬浮字幕。
- 7 种协议适配器：OpenAI 兼容、Azure OpenAI、Anthropic Claude、Google Gemini、Ollama、DeepL、Google 免费翻译；内置 28 个服务模板。
- 每个服务可配置额外请求体 JSON 与额外请求头；一键获取模型列表；连接测试。
- 低延迟管线：流式输出、边说边译与结果复用、事件驱动调度、连接预热、备用服务竞速、结果缓存。
- API Key 使用 Windows DPAPI 加密保存；设置文件原子写入、损坏时自动备份。

[未发布]: https://github.com/STAR-0925/Live-Translator/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/STAR-0925/Live-Translator/releases/tag/v1.0.0
