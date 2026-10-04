# 贡献指南

欢迎提交问题反馈、功能建议和代码。

## 开发环境

- Windows 10/11
- .NET 8 SDK 或更高版本
- 任意编辑器（Visual Studio、Rider、VS Code 均可）

```bash
dotnet build LiveTranslator.sln
```

```bash
dotnet test LiveTranslator.sln
```

调试时可以用 `--no-captions` 参数启动，只通过右下角输入框手动翻译；设置环境变量 `LIVETRANSLATOR_HOME` 可以使用独立的设置目录，不影响自己日常使用的配置。

## 提交代码

1. Fork 仓库并从 `main` 新建分支。
2. 修改后确保 `dotnet build` 没有警告（项目开启了“警告视为错误”），`dotnet test` 全部通过。
3. 新功能或修复请尽量附带测试：核心逻辑都在 `src/LiveTranslator.Core`，可以直接单元测试。
4. 在 `CHANGELOG.md` 的“未发布”部分简要记录改动。
5. 提交 Pull Request，说明改了什么、为什么改。

## 新增翻译服务

- **OpenAI 兼容接口的厂商**：只需在 `src/LiveTranslator.Core/Providers/ProviderPresets.cs` 中添加一行模板。
- **新的接口协议**：在 `Providers/` 下继承 `ProviderBase`，实现 `StreamCoreAsync`（务必逐段 `yield` 流式结果），在 `ProviderFactory` 中注册，并参照 `ProviderTests.cs` 添加请求格式与响应解析的测试。

## 注意

- 不要在代码、测试或 Issue 中提交真实的 API Key；测试请使用明显的假值。
