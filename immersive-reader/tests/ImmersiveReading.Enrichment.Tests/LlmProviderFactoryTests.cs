using ImmersiveReading.Core.Config;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Plugins.AiCompanion.Providers;
using Xunit;

namespace ImmersiveReading.Enrichment.Tests;

public sealed class LlmProviderFactoryTests
{
    private sealed class MockLlmProvider : ILlmProvider
    {
        public string ProviderId => "mock";
        public string DisplayName => "Mock Provider";
        public bool IsLocalOffline => true;

        public Task<LlmStructuredResponse<T>> CompleteStructuredAsync<T>(LlmRequest request, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class MockLlmProviderFactory : ILlmProviderFactory
    {
        public ILlmProvider CreateProvider(LlmSettings settings) => new MockLlmProvider();
    }

    [Fact]
    public void DefaultFactory_CreatesOpenAiCompatibleProvider()
    {
        var factory = DefaultLlmProviderFactory.Instance;
        var settings = new LlmSettings(
            EndpointUrl: "http://localhost:11434/v1/chat/completions",
            ModelId: "llama3.2");

        var provider = factory.CreateProvider(settings);
        Assert.NotNull(provider);
        Assert.Equal("openai-compatible", provider.ProviderId);
    }

    [Fact]
    public void CustomFactory_CanInjectAlternativeProvider()
    {
        var factory = new MockLlmProviderFactory();
        var provider = factory.CreateProvider(new LlmSettings());

        Assert.NotNull(provider);
        Assert.Equal("mock", provider.ProviderId);
        Assert.True(provider.IsLocalOffline);
    }
}
