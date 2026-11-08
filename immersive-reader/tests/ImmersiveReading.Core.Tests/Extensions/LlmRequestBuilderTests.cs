using ImmersiveReading.Core.Extensions;
using Xunit;

namespace ImmersiveReading.Core.Tests.Extensions;

public sealed class LlmRequestBuilderTests
{
    [Fact]
    public void Build_ThrowsWhenNoMessages()
    {
        var builder = LlmRequestBuilder.Create();
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_ConstructsValidRequestWithDefaultOptions()
    {
        var req = LlmRequestBuilder.Create()
            .WithSystemPrompt("sys")
            .WithUserPrompt("usr")
            .Build();

        Assert.Equal(2, req.Messages.Count);
        Assert.Equal("system", req.Messages[0].Role);
        Assert.Equal("sys", req.Messages[0].Content);
        Assert.Equal("user", req.Messages[1].Role);
        Assert.Equal("usr", req.Messages[1].Content);
        Assert.Equal(0.3, req.Temperature);
        Assert.Equal(1024, req.MaxTokens);
        Assert.Null(req.TaskKind);
    }

    [Fact]
    public void Build_ClampsTemperatureAndSetsTaskKind()
    {
        var req = LlmRequestBuilder.Create()
            .WithUserPrompt("hello")
            .WithTemperature(3.5)
            .WithMaxTokens(512)
            .WithTaskKind("quiz.test")
            .Build();

        Assert.Single(req.Messages);
        Assert.Equal(2.0, req.Temperature);
        Assert.Equal(512, req.MaxTokens);
        Assert.Equal("quiz.test", req.TaskKind);
    }
}

