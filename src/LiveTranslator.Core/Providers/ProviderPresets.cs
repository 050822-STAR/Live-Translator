using LiveTranslator.Core.Models;

namespace LiveTranslator.Core.Providers;

/// <remarks>Thinking is switched off in code for every service that allows it (see <see cref="ThinkingOff"/>).</remarks>
public sealed record ProviderPreset(
    string Id,
    string DisplayName,
    ProviderProtocol Protocol,
    string BaseUrl,
    string DefaultModel,
    string Notes = "",
    bool RequiresKey = true)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Starting points only: every field stays editable, and "获取模型列表" queries the live catalogue,
/// so a renamed or retired model id never blocks the user.
/// </summary>
public static class ProviderPresets
{
    public static IReadOnlyList<ProviderPreset> All { get; } =
    [
        new("google-free", "Google 翻译（免费，无需 Key）", ProviderProtocol.GoogleFree, "https://translate.googleapis.com", "",
            Notes: "非 LLM，无需配置即可使用；国内网络可能需要代理。", RequiresKey: false),
        new("deepl", "DeepL", ProviderProtocol.DeepL, "https://api-free.deepl.com", "",
            Notes: "免费版用 api-free.deepl.com，专业版改为 https://api.deepl.com。"),

        new("openai", "OpenAI", ProviderProtocol.OpenAI, "https://api.openai.com/v1", "gpt-4o-mini",
            Notes: "推理模型（o 系列 / gpt-5）已自动关闭思考，无法关闭的降到最低思考强度。"),
        new("azure-openai", "Azure OpenAI", ProviderProtocol.AzureOpenAI, "https://YOUR-RESOURCE.openai.azure.com/openai/v1", "",
            Notes: "模型填部署名。也可粘贴完整的 .../deployments/{部署名}/chat/completions?api-version=... 地址。"),
        new("anthropic", "Anthropic Claude", ProviderProtocol.Anthropic, "https://api.anthropic.com/v1", "claude-haiku-4-5",
            Notes: "Haiku 系列首字延迟最低，适合实时翻译。"),
        new("gemini", "Google Gemini", ProviderProtocol.Gemini, "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-flash-lite",
            Notes: "已默认关闭思考；Pro 等无法关闭思考的模型自动使用最低思考预算。"),

        new("deepseek", "DeepSeek 深度求索", ProviderProtocol.OpenAI, "https://api.deepseek.com/v1", "deepseek-chat",
            Notes: "已默认关闭思考（V4 模型默认开启思考）。"),
        new("qwen", "阿里云百炼 · 通义千问", ProviderProtocol.OpenAI, "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-turbo",
            Notes: "已默认关闭思考。国际站地址: https://dashscope-intl.aliyuncs.com/compatible-mode/v1"),
        new("doubao", "火山方舟 · 豆包", ProviderProtocol.OpenAI, "https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-1-6-flash-250615",
            Notes: "模型可填模型 ID 或推理接入点 ID（ep-...）。已默认关闭思考。"),
        new("zhipu", "智谱 GLM", ProviderProtocol.OpenAI, "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash",
            Notes: "glm-4-flash 免费；GLM-4.5 及以上已默认关闭思考。"),
        new("moonshot", "月之暗面 Kimi", ProviderProtocol.OpenAI, "https://api.moonshot.cn/v1", "moonshot-v1-8k"),
        new("qianfan", "百度千帆 · 文心", ProviderProtocol.OpenAI, "https://qianfan.baidubce.com/v2", "ernie-speed-128k",
            Notes: "API Key 使用千帆 V2 的 bce-v3/... 格式。"),
        new("hunyuan", "腾讯混元", ProviderProtocol.OpenAI, "https://api.hunyuan.cloud.tencent.com/v1", "hunyuan-turbos-latest"),
        new("minimax", "MiniMax", ProviderProtocol.OpenAI, "https://api.minimaxi.com/v1", "MiniMax-Text-01"),
        new("stepfun", "阶跃星辰 StepFun", ProviderProtocol.OpenAI, "https://api.stepfun.com/v1", "step-2-mini"),
        new("siliconflow", "硅基流动 SiliconFlow", ProviderProtocol.OpenAI, "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct",
            Notes: "聚合平台，可选大量开源模型。"),

        new("xai", "xAI Grok", ProviderProtocol.OpenAI, "https://api.x.ai/v1", "grok-4-fast-non-reasoning"),
        new("mistral", "Mistral AI", ProviderProtocol.OpenAI, "https://api.mistral.ai/v1", "mistral-small-latest"),
        new("groq", "Groq（超低延迟）", ProviderProtocol.OpenAI, "https://api.groq.com/openai/v1", "llama-3.1-8b-instant"),
        new("cerebras", "Cerebras（超低延迟）", ProviderProtocol.OpenAI, "https://api.cerebras.ai/v1", "llama3.1-8b"),
        new("together", "Together AI", ProviderProtocol.OpenAI, "https://api.together.xyz/v1", "meta-llama/Llama-3.3-70B-Instruct-Turbo"),
        new("fireworks", "Fireworks AI", ProviderProtocol.OpenAI, "https://api.fireworks.ai/inference/v1", "accounts/fireworks/models/llama-v3p1-8b-instruct"),
        new("openrouter", "OpenRouter", ProviderProtocol.OpenAI, "https://openrouter.ai/api/v1", "openai/gpt-4o-mini",
            Notes: "一个 Key 访问数百个模型。"),
        new("cohere", "Cohere", ProviderProtocol.OpenAI, "https://api.cohere.ai/compatibility/v1", "command-r7b-12-2024"),
        new("nvidia", "NVIDIA NIM", ProviderProtocol.OpenAI, "https://integrate.api.nvidia.com/v1", "meta/llama-3.1-8b-instruct"),

        new("ollama", "Ollama（本地）", ProviderProtocol.Ollama, "http://localhost:11434", "qwen2.5:7b",
            Notes: "模型常驻内存（keep_alive=30m），启动时自动预加载。", RequiresKey: false),
        new("lmstudio", "LM Studio（本地）", ProviderProtocol.OpenAI, "http://localhost:1234/v1", "",
            Notes: "先在 LM Studio 中加载模型并启动本地服务器。", RequiresKey: false),
        new("custom-openai", "自定义 OpenAI 兼容接口", ProviderProtocol.OpenAI, "http://localhost:8000/v1", "",
            Notes: "适用于 vLLM、SGLang、llama.cpp server、One API / New API 等网关。", RequiresKey: false),
    ];

    public static ProviderPreset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    public static ProviderProfile CreateProfile(string presetId)
    {
        var preset = Find(presetId) ?? throw new ArgumentException($"未知模板: {presetId}", nameof(presetId));
        return new ProviderProfile
        {
            Name = preset.DisplayName,
            PresetId = preset.Id,
            Protocol = preset.Protocol,
            BaseUrl = preset.BaseUrl,
            Model = preset.DefaultModel,
            // Local models and MT services get generous timeouts; cloud LLMs fail fast so hedging can kick in.
            TimeoutSeconds = preset.Protocol == ProviderProtocol.Ollama || !preset.RequiresKey ? 30 : 15,
        };
    }
}
