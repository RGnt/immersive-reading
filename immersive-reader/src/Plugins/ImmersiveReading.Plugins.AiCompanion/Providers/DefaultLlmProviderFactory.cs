using ImmersiveReading.Core.Config;
using ImmersiveReading.Core.Extensions;

namespace ImmersiveReading.Plugins.AiCompanion.Providers;

/// <summary>
/// Default factory for instantiating OpenAI-compatible LLM providers from settings.
/// </summary>
public sealed class DefaultLlmProviderFactory : ILlmProviderFactory
{
    public static readonly DefaultLlmProviderFactory Instance = new();

    public ILlmProvider CreateProvider(LlmSettings settings)
    {
        var options = AiCompanionModule.ToEndpointOptions(settings);
        return new OpenAiCompatibleLlmProvider(options);
    }
}

