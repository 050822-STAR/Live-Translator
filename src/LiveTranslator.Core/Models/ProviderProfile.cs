namespace LiveTranslator.Core.Models;

/// <summary>Wire protocol spoken by a translation endpoint.</summary>
public enum ProviderProtocol
{
    /// <summary>OpenAI Chat Completions and the many vendors that clone it.</summary>
    OpenAI,
    /// <summary>Same body as <see cref="OpenAI"/>, authenticated with an <c>api-key</c> header.</summary>
    AzureOpenAI,
    Anthropic,
    Gemini,
    /// <summary>Ollama native <c>/api/chat</c> (NDJSON streaming).</summary>
    Ollama,
    DeepL,
    GoogleFree,
}

public sealed class ProviderProfile : ObservableModel
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "";
    private string _presetId = "";
    private ProviderProtocol _protocol = ProviderProtocol.OpenAI;
    private string _baseUrl = "";
    private string _apiKey = "";
    private string _model = "";
    private double _temperature = 0.3;
    private int _maxTokens = 512;
    private int _timeoutSeconds = 15;
    private bool _stream = true;
    private string _extraBodyJson = "";
    private string _extraHeaders = "";

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string PresetId { get => _presetId; set => Set(ref _presetId, value); }
    public ProviderProtocol Protocol { get => _protocol; set => Set(ref _protocol, value); }
    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value); }
    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, value); }
    public string Model { get => _model; set => Set(ref _model, value); }

    /// <summary>Sampling temperature; a negative value means "do not send the parameter".</summary>
    public double Temperature { get => _temperature; set => Set(ref _temperature, value); }

    /// <summary>Output token cap; 0 means "do not send the parameter".</summary>
    public int MaxTokens { get => _maxTokens; set => Set(ref _maxTokens, value); }

    public int TimeoutSeconds { get => _timeoutSeconds; set => Set(ref _timeoutSeconds, value); }
    public bool Stream { get => _stream; set => Set(ref _stream, value); }

    /// <summary>JSON object deep-merged into the request body; <c>null</c> values delete keys.</summary>
    public string ExtraBodyJson { get => _extraBodyJson; set => Set(ref _extraBodyJson, value); }

    /// <summary>One <c>Name: value</c> header per line.</summary>
    public string ExtraHeaders { get => _extraHeaders; set => Set(ref _extraHeaders, value); }

    public bool IsLlm => Protocol is not (ProviderProtocol.DeepL or ProviderProtocol.GoogleFree);

    public override string ToString() => Name;

    public ProviderProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        PresetId = PresetId,
        Protocol = Protocol,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Model = Model,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        TimeoutSeconds = TimeoutSeconds,
        Stream = Stream,
        ExtraBodyJson = ExtraBodyJson,
        ExtraHeaders = ExtraHeaders,
    };
}
