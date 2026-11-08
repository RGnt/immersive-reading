using System.Text;
using ImmersiveReading.Core.Plugins;
using Xunit;

namespace ImmersiveReading.Core.Tests.Plugins;

public sealed class LanguagePluginRegistryTests
{
    private sealed class DummyPlugin(string code) : ILanguagePlugin
    {
        public string LanguageCode => code;
        public string DisplayName => $"Dummy {code}";
        public LanguageCapabilities Capabilities => LanguageCapabilities.None;
        public bool IsDisposed { get; private set; }

        public ITextTokenizer CreateTokenizer() => throw new NotImplementedException();
        public IDeinflector? GetDeinflector() => null;
        public IRubyAligner? GetRubyAligner() => null;
        public string NormalizeReading(ReadOnlySpan<char> reading) => reading.ToString();
        public bool NeedsPhoneticAnnotation(Rune character) => false;
        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public void Resolve_PicksMatchingPlugin_OrFallback()
    {
        using var registry = new LanguagePluginRegistry();
        var jaPlugin = new DummyPlugin("ja");
        var fallback = new DummyPlugin("*");

        registry.Register(jaPlugin);
        registry.Register(fallback, isFallback: true);

        Assert.Same(jaPlugin, registry.Resolve("ja-JP"));
        Assert.Same(jaPlugin, registry.Resolve("ja_JP"));
        Assert.Same(jaPlugin, registry.Resolve("JA"));
        Assert.Same(fallback, registry.Resolve("fr"));
        Assert.Same(fallback, registry.Resolve(null));
        Assert.Same(fallback, registry.Resolve(""));
    }

    [Fact]
    public void Resolve_ThrowsWhenNoPluginsRegistered()
    {
        using var registry = new LanguagePluginRegistry();
        Assert.Throws<InvalidOperationException>(() => registry.Resolve("ja"));
    }

    [Fact]
    public void Dispose_DisposesAllRegisteredPlugins()
    {
        var p1 = new DummyPlugin("en");
        var p2 = new DummyPlugin("es");

        using (var registry = new LanguagePluginRegistry())
        {
            registry.Register(p1);
            registry.Register(p2);
        }

        Assert.True(p1.IsDisposed);
        Assert.True(p2.IsDisposed);
    }
}

public sealed class WhitespaceTokenizerTests
{
    [Fact]
    public void TokenizeParagraph_ExtractsWordsWithCorrectOffsets()
    {
        using var plugin = new WhitespaceLanguagePlugin();
        string text = "Hello, world! It's a test-run.";
        var tokens = plugin.TokenizeParagraph(text, []);

        Assert.Equal(5, tokens.Count);

        Assert.Equal("Hello", tokens[0].Surface);
        Assert.Equal(0, tokens[0].StartCharOffset);
        Assert.Equal(5, tokens[0].EndCharOffset);
        Assert.Equal("hello", tokens[0].Lemma);

        Assert.Equal("world", tokens[1].Surface);
        Assert.Equal(7, tokens[1].StartCharOffset);
        Assert.Equal(12, tokens[1].EndCharOffset);

        Assert.Equal("It's", tokens[2].Surface);
        Assert.Equal("a", tokens[3].Surface);
        Assert.Equal("test-run", tokens[4].Surface);
    }
}

