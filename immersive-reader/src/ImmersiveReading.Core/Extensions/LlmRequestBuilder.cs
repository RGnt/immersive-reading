namespace ImmersiveReading.Core.Extensions;

/// <summary>
/// Fluent builder for constructing strongly-typed <see cref="LlmRequest"/> instances.
/// </summary>
public sealed class LlmRequestBuilder
{
    private readonly List<LlmMessage> _messages = [];
    private double _temperature = 0.3;
    private int _maxTokens = 1024;
    private string? _taskKind;

    public static LlmRequestBuilder Create() => new();

    public LlmRequestBuilder WithMessage(string role, string content)
    {
        _messages.Add(new LlmMessage(role, content));
        return this;
    }

    public LlmRequestBuilder WithSystemPrompt(string systemPrompt)
        => WithMessage("system", systemPrompt);

    public LlmRequestBuilder WithUserPrompt(string userPrompt)
        => WithMessage("user", userPrompt);

    public LlmRequestBuilder WithAssistantMessage(string assistantMessage)
        => WithMessage("assistant", assistantMessage);

    public LlmRequestBuilder WithTemperature(double temperature)
    {
        _temperature = Math.Clamp(temperature, 0.0, 2.0);
        return this;
    }

    public LlmRequestBuilder WithMaxTokens(int maxTokens)
    {
        _maxTokens = Math.Max(1, maxTokens);
        return this;
    }

    public LlmRequestBuilder WithTaskKind(string? taskKind)
    {
        _taskKind = taskKind;
        return this;
    }

    public LlmRequest Build()
    {
        if (_messages.Count == 0)
        {
            throw new InvalidOperationException("At least one message is required to construct an LlmRequest.");
        }

        return new LlmRequest(
            Messages: _messages.AsReadOnly(),
            Temperature: _temperature,
            MaxTokens: _maxTokens,
            TaskKind: _taskKind);
    }
}

