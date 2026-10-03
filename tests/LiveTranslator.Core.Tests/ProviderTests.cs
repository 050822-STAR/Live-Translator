using System.Net;
using System.Text.Json;

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
        profile.ExtraBodyJson = """{"enable_thinking": false, "temperature": null}""";
        profile.ExtraHeaders = "X-Custom: 1";
        var provider = new OpenAICompatibleProvider(profile, new HttpClient(handler));

        var chunks = await TestData.Collect(provider.TranslateStreamAsync(
            TestData.Request("Hello world", new ContextPair("Hi.", "嗨。"))));

        Assert.Equal(["你好", "世界"], chunks);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://api.deepseek.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("1", request.Headers.GetValues("X-Custom").Single());

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("m", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("enable_thinking").GetBoolean());
        Assert.False(root.TryGetProperty("temperature", out _)); // removed by the null in extra body
        Assert.Equal(100, root.GetProperty("max_tokens").GetInt32());
        var roles = root.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()!).ToArray();
        Assert.Equal(["system", "user", "assistant", "user"], roles);
        Assert.Equal("Hello world", root.GetProperty("messages")[3].GetProperty("content").GetString());
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
        profile.ExtraBodyJson = """{"generationConfig":{"thinkingConfig":{"thinkingBudget":0}}}""";
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
}

public class OllamaProviderTests
{
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

    [Fact]
    public void Validate_reports_malformed_extra_body()
    {
        var profile = ProviderPresets.CreateProfile("deepseek");
        profile.ExtraBodyJson = "{not json";
        Assert.Contains("JSON", ProviderFactory.Validate(profile));
        profile.ExtraBodyJson = "[1,2]";
        Assert.Contains("JSON 对象", ProviderFactory.Validate(profile));
    }
}
