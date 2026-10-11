# Live Translator

[![Build & Test](https://github.com/STAR-0925/Live-Translator/actions/workflows/build.yml/badge.svg)](https://github.com/STAR-0925/Live-Translator/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

基于 **Windows 实时辅助字幕（Live Captions）** 的实时语音翻译器。任何正在播放的声音（视频、会议、直播、游戏）都会被系统识别成字幕，再由你选择的大模型或翻译服务实时译出，显示在主窗口和可叠加在任何画面上的悬浮字幕中。

设计参考了 [SakiRinn/LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator)，在此基础上重写了翻译管线，重点做了两件事：**接入更多大模型 API**，以及**大幅降低从说话到看到译文的延迟**。

## 与参考项目的区别

| | LiveCaptions-Translator | Live Translator |
|---|---|---|
| 接口协议 | 每个服务单独硬编码（4 个 LLM：OpenAI / Ollama / OpenRouter / LM Studio） | 7 种协议适配器：OpenAI 兼容、Azure OpenAI、Anthropic Claude、Google Gemini、Ollama 原生、DeepL、Google 免费 |
| 内置模板 | 11 个服务 | 28 个模板（见下表），并可自定义任意 OpenAI 兼容网关 |
| 思考模式 | 不处理 | 所有能关闭思考的大模型**自动关闭思考**（按服务选用各家参数）；思考被强制的模型自动降到最低思考强度 |
| 模型选择 | 手动输入 | 一键从服务端获取模型列表 |
| 响应方式 | 等待完整响应后一次性显示 | **流式输出**，逐字显示（SSE / NDJSON） |
| 何时开始翻译 | 句子结束（或停顿/累积若干次变化）后才发请求 | **边说边译**：句子没说完就开始翻译；句末标点出现时直接**复用**进行中的请求，常常不需要再发一次请求 |
| 调度 | 3 个轮询循环（25 ms / 40 ms 休眠），每显示一个完整句子后停顿 720 ms 才刷新下一次 | 事件驱动：字幕一变化立即处理，每个 token 立即推送到界面 |
| 并发 | 新结果到达时取消更早的请求 | 边说边译的请求流水线式重叠进行（默认最多 3 个），较新的结果追上后才接管；已结束的句子有独立的并发名额，从不排在推测请求后面 |
| 连接 | 共享 `HttpClient` 上反复 `DefaultRequestHeaders.Clear()`（并发时有串号风险），8 秒超时 | 每个请求独立请求头；HTTP/2 + 连接池；启动时与空闲时**预热连接**（Ollama 预加载模型） |
| 容错 | 失败即报错 | 可设置**备用服务竞速**：主服务超过 N 毫秒没出字（或报错）就同时请求备用服务，谁先出字用谁 |
| 其他 | — | 翻译结果 LRU 缓存、推理模型思考内容过滤（`<think>`、Claude thinking、Gemini thought）、拒绝不支持参数时自动降级重试、API Key 用 Windows DPAPI 加密保存 |

## 内置服务模板

| 类别 | 服务 |
|---|---|
| 国际大模型 | OpenAI、Azure OpenAI、Anthropic Claude、Google Gemini、xAI Grok、Mistral、Cohere |
| 国内大模型 | DeepSeek、阿里云百炼（通义千问）、火山方舟（豆包）、智谱 GLM、月之暗面 Kimi、百度千帆（文心）、腾讯混元、MiniMax、阶跃星辰 |
| 聚合 / 加速平台 | OpenRouter、硅基流动 SiliconFlow、Groq、Cerebras、Together AI、Fireworks AI、NVIDIA NIM |
| 本地部署 | Ollama、LM Studio、自定义 OpenAI 兼容接口（vLLM / SGLang / llama.cpp / One API / New API 等） |
| 传统机器翻译 | Google 翻译（免费，无需 Key，默认）、DeepL |

模板只是起点：接口地址、模型、参数全部可改。模板里的默认模型名可能随厂商更新而过时，点击“获取模型列表”即可从服务端拉取最新列表。

## 系统要求

- Windows 11 22H2 及以上（自带“实时辅助字幕”）。没有该功能时仍可用手动输入模式。
- [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（或使用下文的自包含发布，无需安装运行时）。
- 首次使用前，请在 Windows 中打开一次实时辅助字幕（`Win + Ctrl + L`），按提示下载语音识别语言包，并在其设置里选择**说话人所用的语言**（Live Captions 只识别所选语言）。

## 下载

到 [Releases](https://github.com/STAR-0925/Live-Translator/releases) 下载：

- `LiveTranslator-win-x64-standalone.zip`：独立版，解压后双击 `LiveTranslator.exe` 即可，无需安装任何东西（约 70 MB）。
- `LiveTranslator-win-x64.zip`：精简版（不到 1 MB），需要先安装 .NET 8 桌面运行时。

每次提交的自动构建产物也可以在 [Actions](https://github.com/STAR-0925/Live-Translator/actions) 页面下载。

## 构建与运行

需要 .NET 8 SDK 或更高版本。

```bash
dotnet build LiveTranslator.sln -c Release
```

```bash
dotnet run --project src/LiveTranslator.App -c Release
```

发布为单个可执行文件（自包含，目标机器无需安装 .NET）：

```bash
dotnet publish src/LiveTranslator.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish
```

ARM64 设备把 `win-x64` 换成 `win-arm64`。

命令行参数：

- `--no-captions`：不启动实时辅助字幕，只用右下角输入框手动翻译（用于测试服务配置）。

## 使用

1. 启动后默认使用免费的 Google 翻译，播放任意有声内容即可看到译文。
2. 点击 ⚙ →“翻译服务”→ 从模板添加，例如“DeepSeek 深度求索”，填入 API Key，点击“测试翻译”确认可用，再点“设为主服务”，最后“保存并应用”。
3. 标题栏的服务名和目标语言可以随时点击快速切换。
4. 悬浮字幕按钮打开可拖动、可缩放的透明字幕条；开启“鼠标穿透”后可以叠在播放器上，右键悬浮字幕按钮可解除。

### 推荐的低延迟配置

- 选用首字延迟低的小模型：如 `deepseek-chat`、`qwen-turbo`、`glm-4-flash`、`gemini-2.5-flash-lite`、`claude-haiku-4-5`、`gpt-4o-mini`，或 Groq / Cerebras 上的 Llama。
- **思考已自动关闭**：思考对翻译只增加延迟，还会和译文抢输出额度。程序按服务自动发送对应参数，无需配置：
  - OpenRouter：`reasoning.effort` 依次 `none`、`minimal`、`low`
  - OpenAI / Azure 推理模型（o 系列、gpt-5）：`reasoning_effort` 依次 `none`、`minimal`、`low`
  - 通义千问、硅基流动：`enable_thinking: false`；豆包、智谱 GLM：`thinking.type = disabled`
  - Gemini：3 系列 `thinkingLevel = minimal`（不行用 `low`），2.5 系列 `thinkingBudget = 0`（2.5 Pro 用最低的 128）
  - Groq 的 Qwen3：`reasoning_effort = none`；Ollama：`think: false`（GPT-OSS 用 `think: "low"`）
  - DeepSeek：`thinking.type = disabled`（V4 模型默认开启思考）
  - 其他服务（Kimi、MiniMax、阶跃、混元、千帆等）以及本地 / 自建服务和网关（vLLM、SGLang、llama.cpp、LM Studio、One API 等）：先把各家的关闭参数连同 `reasoning_effort: low` 一次全部带上，被拒再逐个单独尝试
  - 确认没有关闭开关的服务（xAI、Mistral、Cohere 等）：使用最低的 `reasoning_effort: low`
  - Claude 默认不思考

  模型拒绝某个参数时会自动换下一种写法，全部被拒就不带参数，结果会被记住，只有第一句多几次往返。如果仍出现红字“模型没有返回译文…结束原因：length”，说明这个模型连最低思考强度都会耗尽额度，请换用不带思考的模型。
- 保持“边说边译”“流式输出”“连接预热”开启；“上下文句数”设为 0 可再省一点首字时间（但术语一致性会变差）。
- 网络不稳定时设置一个**备用服务**（如主用 DeepSeek，备用 Groq），竞速延迟设为 800~1500 ms。竞速只用于整句翻译，边说边译不会因此多花 Token。
- 状态栏会显示最近 30 段的延迟统计，调整参数后可以直接对比首字中位数和 90% 分位的变化。“网络中位”是请求发出到收到响应头的时间，与首字时间的差就是模型自己的思考时间；“平均分几次到达”接近 1 说明译文被攒成一整块才送到，不是真正的流式。

## 延迟是怎么降下来的

一句话从说出到看到译文，延迟由这几段组成：语音识别 → 发现字幕变化 → 决定何时翻译 → 网络建连 → 模型首字 → 显示。本项目逐段处理：

1. **发现变化**：独立线程以 15 ms 间隔读取字幕文本，变化即推送，没有多级轮询叠加。
2. **何时翻译**：字幕一出现新词就发请求（两次请求至少间隔 200 ms，正常语速下约每个词一次），译文几个词几个词地跟上说话人；多个请求**重叠进行、互不取消**；说话人真正停顿时（超过平时词间隔的 2 倍），针对已说完内容的请求会优先发出，不会排在过时的请求后面——即使模型首字比说话还慢，字幕也会几个词几个词地持续更新；说话停顿 150 ms 时立即发起，因为停顿后通常马上出现句末标点。句子完成时，如果推测请求的文字相同（只差标点），直接把它**提升**为最终译文，不再重新请求。
3. **分片翻译**：句子在逗号、顿号等处切开，说话人越过一个分句后，这个分句立即单独定稿翻译、只翻一次；边说边译只翻译还在说的那一小段，不再每多一个词就把整句重发一遍。同一句的各个分片仍在同一行里接续显示。没有标点的长句超过一定长度（默认 90，中日韩文字按 2 计）时在词间切开。
4. **网络**：连接池复用 + 预热 + HTTP/2，后续请求不再付 DNS/TCP/TLS 握手成本。预热会同时建立与并行请求数相当的连接，边说边译的多个请求同时发出时也不必现场握手。流式请求不要求压缩，并声明 `Accept: text/event-stream`，避免 CDN 或代理把逐字输出攒成一整块再发。
5. **模型**：流式输出，首个 token 到达即显示；内置模板默认关闭思考模式。
6. **显示**：后台线程把更新合并后投递到 UI 线程，同一帧内多个 token 只渲染一次，没有固定的显示停顿。新请求的结果追上屏幕上已有的译文后才替换，字幕只会变长、不会缩回去重新打字。

测试套件里有一个基准测试（`End_to_end_latency_report`），在本机回环地址上模拟一个“首字 300 ms、每个 token 40 ms”的模型，逐词输入一句话。在开发机上的一次结果：

- 一个完整的非流式请求耗时 824 ms，所以“等句子结束、再等完整响应”的方式在句末之后至少还要等这么久；
- 本项目在句末标点出现后 **62 ms** 显示最终译文的首字，**576 ms** 完成整句。

另一个基准测试（`Continuous_speech_latency_report`）模拟更接近云端大模型的情况：首字 600 ms，说话人每 200 ms 一个词、中间不停顿。开发机上 3.5 秒的连续说话期间，字幕更新了 23 次，最长 390 ms 没有变化；同样条件下，1.0.0 版本的引擎因为每个新请求都会取消上一个，在说话人停下之前一次都没有显示。

这些是模拟模型的数据，真实延迟主要取决于所选模型和网络，但上面这几段节省与具体服务无关。

## 数据与隐私

- 设置保存在 `%APPDATA%\LiveTranslator\settings.json`；设置环境变量 `LIVETRANSLATOR_HOME` 可改用其他目录（便携使用或多套配置）。
- API Key 使用 Windows DPAPI 加密，只有当前 Windows 账户能解密；复制到其他电脑或账户后需要重新填写。
- 勾选“将翻译记录保存到文件”后，记录按天写入 `logs\yyyy-MM-dd.tsv`。
- 识别出的字幕文本会发送给你选择的翻译服务。

## 开发

```bash
dotnet test LiveTranslator.sln
```

```
src/LiveTranslator.Core        与界面无关的核心库（可单元测试）
  Providers/                   协议适配器、服务模板、竞速包装
  Pipeline/                    分句、文本规整、翻译引擎、缓存、思考内容过滤
  Http/                        共享 HttpClient、SSE / NDJSON 流式解析、JSON 合并
  Settings/                    设置读写（原子写入、密钥加密接口）
src/LiveTranslator.App         WPF 桌面程序（实时辅助字幕读取、主窗口、悬浮字幕、设置）
tests/LiveTranslator.Core.Tests xUnit 测试，含基于真实 Socket 的流式与延迟测试
```

新增一种协议：在 `Providers/` 中继承 `ProviderBase` 实现 `StreamCoreAsync`，并在 `ProviderFactory` 注册；仅新增 OpenAI 兼容厂商只需在 `ProviderPresets` 里加一行。详见 [贡献指南](CONTRIBUTING.md)。

### 发布新版本

1. 修改 `Directory.Build.props` 中的 `<Version>`，并在 [CHANGELOG.md](CHANGELOG.md) 中整理本版本的改动。
2. 提交并推送后，打上版本标签并推送：`git tag v1.1.0`，然后 `git push origin v1.1.0`。
3. GitHub Actions 会自动测试、打包两个安装包并创建 Release 草稿，检查说明后点击发布即可。

## 反馈

- 使用问题与功能建议：[Issues](https://github.com/STAR-0925/Live-Translator/issues)
- 安全问题：见 [SECURITY.md](SECURITY.md)

## 许可证

本项目以 [Apache License 2.0](LICENSE) 开源。

## 致谢

- [LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator)（Apache-2.0）：实时辅助字幕的窗口查找与隐藏方式、字幕文本规整的正则规则改编自该项目，详见 [NOTICE](NOTICE)。
