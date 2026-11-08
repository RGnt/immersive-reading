using ImmersiveReading.Core.Config;

namespace ImmersiveReading.Core.Extensions;

public sealed record LlmMessage(
    string Role,
    string Content
);

public sealed record LlmRequest(
    IReadOnlyList<LlmMessage> Messages,
    double Temperature = 0.3,
    int MaxTokens = 1024,
    string? TaskKind = null
);

public sealed record LlmStructuredResponse<T>(
    T Value,
    string RawJson,
    string ModelId
);

public interface ILlmProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    bool IsLocalOffline { get; }

    Task<LlmStructuredResponse<T>> CompleteStructuredAsync<T>(
        LlmRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Factory interface for creating configured <see cref="ILlmProvider"/> instances.
/// </summary>
public interface ILlmProviderFactory
{
    ILlmProvider CreateProvider(LlmSettings settings);
}
