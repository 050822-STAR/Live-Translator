using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using LiveTranslator.Core.Models;
using LiveTranslator.Core.Providers;

namespace LiveTranslator.Core.Tests;

public class OpenAICompatibleProviderTests
{
    [Fact]
    public async Task Builds_request_with_context_turns_auth_and_extra_body()
    {
        var handler = new FakeHandler(_ => FakeHandler.Sse(
            """{"choices":[{"delta":{"role":"assistant"}}]}""",
            """{"choices":[{"delta":{"content":"你好"}}]}""",
            """{"choices":[{"delta":{"reasoning_content":"hmm"}}]}""",
            """{"choices":[{"delta":{"content":"世界"}}]}""",
            "[DONE]"));
        var profile = TestData.Profile(ProviderProtocol.OpenAI, "https://api.deepseek.com/v1/");
        var provider = new OpenAICompatibleProvider(profile, new HttpClient(handler));

        var chunks = await TestData.Collect(provider.TranslateStreamAsync(
            TestData.Request("Hello world", new ContextPair("Hi.", "嗨。"))));

        Assert.Equal(["你好", "世界"], chunks);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://api.deepseek.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", request.Headers.GetValues("Authorization").Single());

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("m", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());
        Assert.False(root.TryGetProperty("chat_template_kwargs", out _)); // DeepSeek has no thinking switch to send
        Assert.Equal(100, root.GetProperty("max_tokens").GetInt32());
        var roles = root.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()!).ToArray();
        Assert.Equal(["system", "user", "assistant", "user"], roles);
        Assert.Equal("Hello world", root.GetProperty("messages")[3].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Empty_answer_reports_the_services_finish_reason_in_the_trace()
    {
        // A reasoning model that spent its whole token budget thinking: no content at all.
        var handler = new FakeHandler(_ => FakeHandler.Sse(
            """{"choices":[{"delta":{"role":"assistant","content":""}}]}""",
            """{"choices":[{"delta":{"reasoning":"..."}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"length"}]}""",
            "[DONE]"));
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://openrouter.ai/api/v1"), new HttpClient(handler));
        var trace = new RequestTrace();

        var chunks = await TestData.Collect(provider.TranslateStreamAsync(TestData.Request() with { Trace = trace }));

        Assert.Empty(chunks);
        var t = trace.Snapshot();
        Assert.Equal("length", t.FinishReason);
        Assert.Equal(0, t.Chunks);
        Assert.NotNull(t.HeadersMs);
        Assert.NotNull(t.EndMs);
    }

    [Fact]
    public async Task OpenRouter_switches_thinking_off_but_other_services_are_left_alone()
    {
        var handler = new FakeHandler(_ => FakeHandler.Sse("[DONE]"));
        var http = new HttpClient(handler);

        await TestData.Collect(new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://openrouter.ai/api/v1"), http).TranslateStreamAsync(TestData.Request()));
        await TestData.Collect(new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://api.deepseek.com/v1"), http).TranslateStreamAsync(TestData.Request()));

        using var openRouter = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("none", openRouter.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        using var other = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.False(other.RootElement.TryGetProperty("reasoning", out _));
    }

    [Theory]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-turbo", """{"enable_thinking":false}""")]
    [InlineData("https://api.siliconflow.cn/v1", "Qwen/Qwen3-8B", """{"enable_thinking":false}""")]
    [InlineData("https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-1-6-flash-250615", """{"thinking":{"type":"disabled"}}""")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "glm-4.5", """{"thinking":{"type":"disabled"}}""")]
    [InlineData("https://api.openai.com/v1", "gpt-5-mini", """{"reasoning_effort":"none"}""")]
    [InlineData("https://api.groq.com/openai/v1", "qwen/qwen3-32b", """{"reasoning_effort":"none"}""")]
    [InlineData("http://localhost:8000/v1", "Qwen3-8B", """{"chat_template_kwargs":{"enable_thinking":false}}""")]
    [InlineData("https://api.deepseek.com/v1", "deepseek-v4-flash", """{"thinking":{"type":"disabled"}}""")]
    [InlineData("https://api.moonshot.cn/v1", "kimi-k2.5", """{"thinking":{"type":"disabled"},"reasoning_effort":"low"}""")]
    public async Task Thinking_is_switched_off_in_each_services_own_way(string baseUrl, string model, string expected)
    {
        var handler = new FakeHandler(_ => FakeHandler.Sse("[DONE]"));
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, baseUrl, model), new HttpClient(handler));

        await TestData.Collect(provider.TranslateStreamAsync(TestData.Request()));

        var body = JsonNode.Parse(handler.Requests[0].Body)!.AsObject();
        foreach (var (key, value) in JsonNode.Parse(expected)!.AsObject())
            Assert.Equal(value!.ToJsonString(), body[key]!.ToJsonString());
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "gpt-4o-mini")]
    [InlineData("https://api.mistral.ai/v1", "magistral-small-latest")]
    [InlineData("https://api.x.ai/v1", "grok-3-mini")]
    [InlineData("https://api.groq.com/openai/v1", "openai/gpt-oss-20b")]
    public async Task Services_without_a_switch_of_their_own_ask_for_low_effort(string baseUrl, string model)
    {
        var handler = new FakeHandler(_ => FakeHandler.Sse("[DONE]"));
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, baseUrl, model), new HttpClient(handler));

        await TestData.Collect(provider.TranslateStreamAsync(TestData.Request()));

        var body = JsonNode.Parse(handler.Requests[0].Body)!.AsObject();
        Assert.Equal("low", body["reasoning_effort"]!.GetValue<string>());
        string[] standard = ["model", "messages", "stream", "temperature", "max_tokens", "max_completion_tokens", "reasoning_effort"];
        Assert.All(body.Select(p => p.Key), key => Assert.Contains(key, standard));
    }

    [Fact]
    public async Task Model_without_reasoning_that_rejects_effort_is_then_sent_plain()
    {
        var handler = new FakeHandler(r => r.Content!.ReadAsStringAsync().Result.Contains("reasoning_effort")
            ? FakeHandler.Text("""{"error":{"message":"Unrecognized request argument supplied: reasoning_effort"}}""", "application/json", HttpStatusCode.BadRequest)
            : FakeHandler.Sse("""{"choices":[{"delta":{"content":"好"}}]}""", "[DONE]"));
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://api.x.ai/v1", "grok-4"), new HttpClient(handler));

        Assert.Equal(["好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));
        Assert.Equal(["好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));

        Assert.Equal(3, handler.Requests.Count); // one refused try, remembered
        Assert.DoesNotContain("reasoning_effort", handler.Requests[^1].Body);
        Assert.Contains("temperature", handler.Requests[^1].Body); // other parameters are kept
    }

    [Fact]
    public async Task Unknown_gateway_gets_every_vendors_switch_at_once_then_narrower_ones()
    {
        // A strict server that only knows the "thinking" switch refuses every other field.
        var handler = new FakeHandler(r =>
        {
            var body = JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!.AsObject();
            return body.Any(p => p.Key is "enable_thinking" or "chat_template_kwargs" or "think" or "reasoning_effort")
                ? FakeHandler.Text("""{"error":{"message":"Extra inputs are not permitted"}}""", "application/json", HttpStatusCode.UnprocessableEntity)
                : FakeHandler.Sse("""{"choices":[{"delta":{"content":"好"}}]}""", "[DONE]");
        });
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://one-api.example.com/v1", "kimi-k2.5"), new HttpClient(handler));

        Assert.Equal(["好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));

        var bundle = JsonNode.Parse(handler.Requests[0].Body)!.AsObject();
        Assert.False(bundle["enable_thinking"]!.GetValue<bool>());
        Assert.Equal("disabled", bundle["thinking"]!["type"]!.GetValue<string>());
        Assert.False(bundle["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.False(bundle["think"]!.GetValue<bool>());
        Assert.Equal("low", bundle["reasoning_effort"]!.GetValue<string>());
        var switchesOnly = JsonNode.Parse(handler.Requests[1].Body)!.AsObject();
        Assert.Null(switchesOnly["reasoning_effort"]);
        var accepted = JsonNode.Parse(handler.Requests[2].Body)!.AsObject();
        Assert.Equal("disabled", accepted["thinking"]!["type"]!.GetValue<string>());
        Assert.Null(accepted["enable_thinking"]);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Weaker_thinking_setting_is_tried_when_the_model_rejects_switching_it_off()
    {
        // gpt-5 has no "none"; it goes down to "minimal". The temperature rejection is handled separately.
        var handler = new FakeHandler(r =>
        {
            var body = r.Content!.ReadAsStringAsync().Result;
            return body.Contains("\"none\"")
                ? FakeHandler.Text("""{"error":{"message":"Unsupported value: 'reasoning_effort' does not support 'none' with this model."}}""", "application/json", HttpStatusCode.BadRequest)
                : body.Contains("temperature")
                    ? FakeHandler.Text("""{"error":{"message":"Unsupported value: 'temperature' does not support 0.2 with this model."}}""", "application/json", HttpStatusCode.BadRequest)
                    : FakeHandler.Sse("""{"choices":[{"delta":{"content":"好"}}]}""", "[DONE]");
        });
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://api.openai.com/v1", "gpt-5"), new HttpClient(handler));

        Assert.Equal(["好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));
        Assert.Equal(["好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));

        var last = JsonNode.Parse(handler.Requests[^1].Body)!.AsObject();
        Assert.Equal("minimal", last["reasoning_effort"]!.GetValue<string>()); // thinking stays turned down
        Assert.Null(last["temperature"]);
        Assert.Equal(4, handler.Requests.Count); // none → temperature → minimal ok, then straight through
    }

    [Fact]
    public async Task Model_that_must_think_is_retried_without_switching_thinking_off()
    {
        var handler = new FakeHandler(r => r.Content!.ReadAsStringAsync().Result.Contains("\"reasoning\"")
            ? FakeHandler.Text("""{"error":{"message":"Reasoning is mandatory for this endpoint and cannot be disabled."}}""", "application/json", HttpStatusCode.BadRequest)
            : FakeHandler.Sse("""{"choices":[{"delta":{"content":"好"}}]}""", "[DONE]"));
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://openrouter.ai/api/v1"), new HttpClient(handler));

        Assert.Equal(["好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));
        Assert.Equal(["好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));

        // "none", "minimal" and "low" are all refused, then it goes without; the second sentence goes straight through.
        Assert.Equal(5, handler.Requests.Count);
        using var weaker = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("minimal", weaker.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        using var lowest = JsonDocument.Parse(handler.Requests[2].Body);
        Assert.Equal("low", lowest.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        using var retried = JsonDocument.Parse(handler.Requests[3].Body);
        Assert.False(retried.RootElement.TryGetProperty("reasoning", out _));
        Assert.True(retried.RootElement.TryGetProperty("temperature", out _)); // other parameters are kept
    }

    [Fact]
    public async Task Official_openai_uses_max_completion_tokens_and_azure_uses_api_key_header()
    {
        var handler = new FakeHandler(_ => FakeHandler.Sse("[DONE]"));
        var http = new HttpClient(handler);
        await TestData.Collect(new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://api.openai.com/v1"), http)
            .TranslateStreamAsync(TestData.Request()));
        await TestData.Collect(new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.AzureOpenAI,
                "https://x.openai.azure.com/openai/deployments/d/chat/completions?api-version=2024-10-21"), http)
            .TranslateStreamAsync(TestData.Request()));

        Assert.Contains("\"max_completion_tokens\":100", handler.Requests[0].Body);
        var azure = handler.Requests[1].Request;
        Assert.Equal("https://x.openai.azure.com/openai/deployments/d/chat/completions?api-version=2024-10-21", azure.RequestUri!.ToString());
        Assert.Equal("sk-test", azure.Headers.GetValues("api-key").Single());
        Assert.False(azure.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Retries_once_without_optional_parameters_when_server_rejects_them()
    {
        int calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? FakeHandler.Text("""{"error":{"message":"Unsupported value: 'temperature' does not support 0.2"}}""", "application/json", HttpStatusCode.BadRequest)
            : FakeHandler.Sse("""{"choices":[{"delta":{"content":"ok"}}]}""", "[DONE]"));
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://api.openai.com/v1"), new HttpClient(handler));

        Assert.Equal(["ok"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));
        Assert.Equal(["ok"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("temperature", handler.Requests[0].Body);
        Assert.DoesNotContain("temperature", handler.Requests[1].Body);
        Assert.DoesNotContain("temperature", handler.Requests[2].Body); // remembered, no wasted round trip
    }

    [Fact]
    public async Task Http_errors_become_readable_provider_exceptions_without_leaking_the_key()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("""{"error":{"message":"Incorrect API key provided"}}""", "application/json", HttpStatusCode.Unauthorized));
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://api.openai.com/v1"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("Incorrect API key provided", ex.Message);
        Assert.Contains("API Key", ex.Message);
        Assert.DoesNotContain("sk-test", ex.Message);
    }

    [Fact]
    public async Task Error_object_inside_stream_is_raised()
    {
        var handler = new FakeHandler(_ => FakeHandler.Sse("""{"error":{"message":"overloaded"}}"""));
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://x/v1"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));
        Assert.Contains("overloaded", ex.Message);
    }

    [Fact]
    public async Task Falls_back_to_plain_json_when_server_ignores_stream_flag()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("""{"choices":[{"message":{"content":"整段结果"}}]}""", "application/json"));
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://x/v1"), new HttpClient(handler));

        Assert.Equal(["整段结果"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));
    }

    [Fact]
    public async Task Yields_each_token_before_the_response_has_finished()
    {
        var body = new ChunkedStream();
        var handler = new FakeHandler(_ => FakeHandler.Streaming(body));
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://x/v1"), new HttpClient(handler));

        await using var e = provider.TranslateStreamAsync(TestData.Request()).GetAsyncEnumerator();
        body.Push("data: {\"choices\":[{\"delta\":{\"content\":\"A\"}}]}\n\n");
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("A", e.Current);

        // A gateway that forgets the blank line between events must still stream.
        body.Push("data: {\"choices\":[{\"delta\":{\"content\":\"B\"}}]}\n");
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("B", e.Current);

        body.Push("data: [DONE]\n\n");
        body.Complete();
        Assert.False(await e.MoveNextAsync());
    }

    [Fact]
    public async Task Timeout_is_reported_as_provider_exception_but_caller_cancellation_is_not()
    {
        var hanging = new ChunkedStream();
        var handler = new FakeHandler(_ => FakeHandler.Streaming(hanging));
        var profile = TestData.Profile(ProviderProtocol.OpenAI, "https://x/v1");
        profile.TimeoutSeconds = 1;
        var provider = new OpenAICompatibleProvider(profile, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));
        Assert.Contains("超时", ex.Message);

        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.TranslateStreamAsync(TestData.Request(), cts.Token)) { }
        });
    }

    [Fact]
    public async Task Lists_models()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("""{"data":[{"id":"b"},{"id":"a"}]}""", "application/json"));
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, "https://x/v1/chat/completions"), new HttpClient(handler));

        Assert.Equal(["a", "b"], await provider.ListModelsAsync());
        Assert.Equal("https://x/v1/models", handler.Requests[0].Request.RequestUri!.ToString());
    }
}

public class AnthropicProviderTests
{
    [Fact]
    public async Task Sends_messages_api_request_and_parses_text_deltas_only()
    {
        var handler = new FakeHandler(_ => FakeHandler.Sse(
            """{"type":"message_start","message":{}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"x"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"你"}}""",
            """{"type":"ping"}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"好"}}""",
            """{"type":"message_stop"}"""));
        var provider = new AnthropicProvider(TestData.Profile(ProviderProtocol.Anthropic, "https://api.anthropic.com/v1"), new HttpClient(handler));

        var chunks = await TestData.Collect(provider.TranslateStreamAsync(TestData.Request("Hi", new ContextPair("a", "b"))));

        Assert.Equal(["你", "好"], chunks);
        var (request, body) = handler.Requests[0];
        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.ToString());
        Assert.Equal("sk-test", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("SYS", doc.RootElement.GetProperty("system").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal(100, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Stream_error_event_is_raised()
    {
        var handler = new FakeHandler(_ => FakeHandler.Sse("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""));
        var provider = new AnthropicProvider(TestData.Profile(ProviderProtocol.Anthropic, "https://api.anthropic.com/v1"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));
        Assert.Contains("Overloaded", ex.Message);
    }
}

public class GeminiProviderTests
{
    [Fact]
    public async Task Uses_stream_endpoint_maps_roles_and_skips_thought_parts()
    {
        var handler = new FakeHandler(_ => FakeHandler.Sse(
            """{"candidates":[{"content":{"parts":[{"text":"thinking...","thought":true}]}}]}""",
            """{"candidates":[{"content":{"parts":[{"text":"你好"}],"role":"model"}}]}""",
            """{"candidates":[{"content":{"parts":[{"text":"，世界"}],"role":"model"},"finishReason":"STOP"}]}"""));
        var profile = TestData.Profile(ProviderProtocol.Gemini, "https://generativelanguage.googleapis.com/v1beta", "models/gemini-2.5-flash");
        var provider = new GeminiProvider(profile, new HttpClient(handler));

        var chunks = await TestData.Collect(provider.TranslateStreamAsync(TestData.Request("Hello", new ContextPair("a", "b"))));

        Assert.Equal(["你好", "，世界"], chunks);
        var (request, body) = handler.Requests[0];
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse", request.RequestUri!.ToString());
        Assert.Equal("sk-test", request.Headers.GetValues("x-goog-api-key").Single());
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(["user", "model", "user"], root.GetProperty("contents").EnumerateArray().Select(c => c.GetProperty("role").GetString()!).ToArray());
        var config = root.GetProperty("generationConfig");
        Assert.Equal(100, config.GetProperty("maxOutputTokens").GetInt32()); // kept by the deep merge
        Assert.Equal(0, config.GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32());
    }

    [Theory]
    [InlineData("gemini-3-flash-preview", "thinkingLevel", "minimal")]
    [InlineData("gemini-2.5-flash-lite", "thinkingBudget", "0")]
    [InlineData("gemini-flash-latest", "thinkingBudget", "0")]
    public async Task Thinking_is_switched_off_by_model_generation(string model, string field, string value)
    {
        var handler = new FakeHandler(_ => FakeHandler.Sse("""{"candidates":[{"content":{"parts":[{"text":"好"}]}}]}"""));
        var provider = new GeminiProvider(TestData.Profile(ProviderProtocol.Gemini, "https://generativelanguage.googleapis.com/v1beta", model), new HttpClient(handler));

        await TestData.Collect(provider.TranslateStreamAsync(TestData.Request()));

        var thinking = JsonNode.Parse(handler.Requests[0].Body)!["generationConfig"]!["thinkingConfig"]!;
        Assert.Equal(value, thinking[field]!.ToString());
    }

    [Fact]
    public async Task Pro_model_that_cannot_stop_thinking_falls_back_to_the_smallest_budget()
    {
        var handler = new FakeHandler(r => r.Content!.ReadAsStringAsync().Result.Contains("\"thinkingBudget\":0")
            ? FakeHandler.Text("""{"error":{"code":400,"message":"Budget 0 is invalid. This model only works in thinking mode."}}""", "application/json", HttpStatusCode.BadRequest)
            : FakeHandler.Sse("""{"candidates":[{"content":{"parts":[{"text":"好"}]}}]}"""));
        var provider = new GeminiProvider(TestData.Profile(ProviderProtocol.Gemini, "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-pro"), new HttpClient(handler));

        Assert.Equal(["好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));

        Assert.Equal(128, JsonNode.Parse(handler.Requests[^1].Body)!["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>());
    }
}

public class OllamaProviderTests
{
    [Theory]
    [InlineData("qwen3:8b", "false")]
    [InlineData("gpt-oss:20b", "low")] // ignores false and keeps thinking; it only takes a level
    public async Task Thinking_is_switched_off_or_turned_down(string model, string think)
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("{\"message\":{\"content\":\"好\"},\"done\":true}\n", "application/x-ndjson"));
        var provider = new OllamaProvider(TestData.Profile(ProviderProtocol.Ollama, "http://localhost:11434", model, ""), new HttpClient(handler));

        await TestData.Collect(provider.TranslateStreamAsync(TestData.Request()));

        Assert.Equal(think, JsonNode.Parse(handler.Requests[0].Body)!["think"]!.ToString());
    }

    [Fact]
    public async Task Parses_ndjson_stream()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text(
            "{\"message\":{\"role\":\"assistant\",\"content\":\"你\"},\"done\":false}\n" +
            "{\"message\":{\"role\":\"assistant\",\"content\":\"好\"},\"done\":false}\n" +
            "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n", "application/x-ndjson"));
        var provider = new OllamaProvider(TestData.Profile(ProviderProtocol.Ollama, "http://localhost:11434", key: ""), new HttpClient(handler));

        Assert.Equal(["你", "好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));
        var (request, body) = handler.Requests[0];
        Assert.Equal("http://localhost:11434/api/chat", request.RequestUri!.ToString());
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.Contains("\"keep_alive\":\"30m\"", body);
        Assert.Contains("\"num_predict\":100", body);
    }

    [Fact]
    public async Task Error_line_is_raised()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("{\"error\":\"model 'x' not found\"}\n", "application/x-ndjson"));
        var provider = new OllamaProvider(TestData.Profile(ProviderProtocol.Ollama, "http://localhost:11434"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));
        Assert.Contains("not found", ex.Message);
    }
}

public class MachineTranslationProviderTests
{
    [Fact]
    public async Task DeepL_sends_target_code_and_context()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("""{"translations":[{"detected_source_language":"EN","text":"你好"}]}""", "application/json"));
        var provider = new DeepLProvider(TestData.Profile(ProviderProtocol.DeepL, "https://api-free.deepl.com"), new HttpClient(handler));

        Assert.Equal(["你好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request("Hello", new ContextPair("Earlier.", "早些时候。")))));
        var (request, body) = handler.Requests[0];
        Assert.Equal("https://api-free.deepl.com/v2/translate", request.RequestUri!.ToString());
        Assert.Equal("DeepL-Auth-Key sk-test", request.Headers.GetValues("Authorization").Single());
        Assert.Contains("\"target_lang\":\"ZH-HANS\"", body);
        Assert.Contains("\"context\":\"Earlier.\"", body);
    }

    [Fact]
    public async Task Html_error_pages_are_summarized_instead_of_dumping_markup()
    {
        var page = "<html><head><meta charset=\"utf-8\"/><title>Sorry&hellip;</title><style>body{}</style></head><body><div>blocked</div></body></html>";
        var handler = new FakeHandler(_ => FakeHandler.Text(page, "text/html", HttpStatusCode.TooManyRequests));
        var provider = new GoogleFreeProvider(TestData.Profile(ProviderProtocol.GoogleFree, "https://translate.googleapis.com", key: ""), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => TestData.Collect(provider.TranslateStreamAsync(TestData.Request())));

        Assert.Equal("HTTP 429: 服务返回了网页而不是 API 响应：Sorry…（请求过于频繁或额度不足）", ex.Message);
    }

    [Fact]
    public async Task Google_free_abandons_a_blocked_connection_and_retries_on_a_fresh_one()
    {
        // Google flags single connections: the flagged one keeps answering 429, a new one works.
        var blocked = new FakeHandler(_ => FakeHandler.Text("<html><title>Sorry...</title></html>", "text/html", HttpStatusCode.TooManyRequests));
        var healthy = new FakeHandler(_ => FakeHandler.Text("""[[["你好","Hello",null,null,10]],null,"en"]""", "application/json"));
        int created = 0;
        var provider = new GoogleFreeProvider(
            TestData.Profile(ProviderProtocol.GoogleFree, "https://translate.googleapis.com", key: ""),
            new HttpClient(blocked),
            () => new HttpClient(++created == 1 ? blocked : healthy));

        Assert.Equal(["你好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request("Hello"))));
        Assert.Equal(["你好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request("Hello"))));

        Assert.Equal(3, created);              // active + spare up front, one new spare after the switch
        Assert.Single(blocked.Requests);       // the flagged connection is not used again
        Assert.Equal(2, healthy.Requests.Count(r => r.Request.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task Google_free_reports_429_when_fresh_connections_are_blocked_too()
    {
        var blocked = new FakeHandler(_ => FakeHandler.Text("<html><title>Sorry...</title></html>", "text/html", HttpStatusCode.TooManyRequests));
        var provider = new GoogleFreeProvider(
            TestData.Profile(ProviderProtocol.GoogleFree, "https://translate.googleapis.com", key: ""),
            new HttpClient(blocked),
            () => new HttpClient(blocked));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => TestData.Collect(provider.TranslateStreamAsync(TestData.Request("Hello"))));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(3, blocked.Requests.Count(r => r.Request.Method == HttpMethod.Get)); // two quick switches, then it reports
    }

    [Fact]
    public async Task Google_free_switches_to_the_prewarmed_spare_connection()
    {
        var blocked = new FakeHandler(_ => FakeHandler.Text("<html><title>Sorry...</title></html>", "text/html", HttpStatusCode.TooManyRequests));
        var spare = new FakeHandler(r => r.Method == HttpMethod.Head
            ? FakeHandler.Text("", "text/html", HttpStatusCode.NotFound)
            : FakeHandler.Text("""[[["你好","Hello",null,null,10]],null,"en"]""", "application/json"));
        var next = new FakeHandler(_ => FakeHandler.Text("", "text/html", HttpStatusCode.NotFound));
        var handlers = new Queue<FakeHandler>([blocked, spare, next]);
        var provider = new GoogleFreeProvider(
            TestData.Profile(ProviderProtocol.GoogleFree, "https://translate.googleapis.com", key: ""),
            new HttpClient(blocked),
            () => new HttpClient(handlers.Dequeue()));

        await provider.WarmUpAsync();                     // opens the active and the spare connection
        Assert.Equal(["你好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request("Hello"))));

        Assert.Single(spare.Requests, r => r.Request.Method == HttpMethod.Head); // the spare was warmed in advance
        Assert.Single(spare.Requests, r => r.Request.Method == HttpMethod.Get);  // and took over the retry
        await TestData.WaitUntil(() => next.Requests.Count == 1, because: "the next spare is warmed in the background");
    }

    [Fact]
    public async Task Google_free_recovers_when_the_spare_is_flagged_too()
    {
        var blocked = new FakeHandler(_ => FakeHandler.Text("<html><title>Sorry...</title></html>", "text/html", HttpStatusCode.TooManyRequests));
        var healthy = new FakeHandler(r => r.Method == HttpMethod.Head
            ? FakeHandler.Text("", "text/html", HttpStatusCode.NotFound)
            : FakeHandler.Text("""[[["你好","Hello",null,null,10]],null,"en"]""", "application/json"));
        var handlers = new Queue<FakeHandler>([blocked, blocked, healthy, healthy]); // active, spare, then fresh ones
        var provider = new GoogleFreeProvider(
            TestData.Profile(ProviderProtocol.GoogleFree, "https://translate.googleapis.com", key: ""),
            new HttpClient(blocked),
            () => new HttpClient(handlers.Dequeue()));

        Assert.Equal(["你好"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request("Hello"))));
        Assert.Equal(2, blocked.Requests.Count(r => r.Request.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task Google_free_warm_up_sends_a_single_probe()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("", "text/html", HttpStatusCode.NotFound));
        var provider = new GoogleFreeProvider(TestData.Profile(ProviderProtocol.GoogleFree, "https://translate.googleapis.com", key: ""), new HttpClient(handler));

        await provider.WarmUpAsync(4);

        var probe = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Head, probe.Request.Method);
    }

    [Fact]
    public async Task Google_free_concatenates_segments()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("""[[["你好。","Hello.",null,null,10],["世界","world",null,null,10]],null,"en"]""", "application/json"));
        var provider = new GoogleFreeProvider(TestData.Profile(ProviderProtocol.GoogleFree, "https://translate.googleapis.com", key: ""), new HttpClient(handler));

        Assert.Equal(["你好。世界"], await TestData.Collect(provider.TranslateStreamAsync(TestData.Request("Hello. world"))));
        var uri = handler.Requests[0].Request.RequestUri!.AbsoluteUri;
        Assert.Contains("tl=zh-CN", uri);
        Assert.Contains("q=Hello.%20world", uri);
    }
}

public class ProviderPresetTests
{
    [Fact]
    public void Every_preset_creates_a_valid_profile_and_provider()
    {
        Assert.True(ProviderPresets.All.Count >= 25);
        Assert.Equal(ProviderPresets.All.Count, ProviderPresets.All.Select(p => p.Id).Distinct().Count());
        foreach (var preset in ProviderPresets.All)
        {
            var profile = ProviderPresets.CreateProfile(preset.Id);
            if (string.IsNullOrEmpty(profile.Model) && profile.IsLlm)
                profile.Model = "placeholder"; // presets for user-loaded local models ship without a default
            Assert.True(Uri.IsWellFormedUriString(profile.BaseUrl, UriKind.Absolute), preset.Id);
            Assert.Null(ProviderFactory.Validate(profile) is { } error && preset.Protocol != ProviderProtocol.DeepL ? $"{preset.Id}: {error}" : null);
            Assert.NotNull(ProviderFactory.Create(profile, new HttpClient()));
        }
    }

}
