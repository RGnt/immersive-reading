using System.Text.Json;
using ImmersiveReading.App.Appearance;
using ImmersiveReading.App.Rpc;
using ImmersiveReading.App.Rpc.Modules;
using ImmersiveReading.Core.Knowledge;
using ImmersiveReading.Core.Prompts;
using ImmersiveReading.Knowledge;
using Xunit;

namespace ImmersiveReading.App.Tests;

/// <summary>The JSON-RPC contract of Settings → Prompts, Custom CSS and Memory: method names, payloads and result shapes the client relies on.</summary>
public class CustomizationRpcTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cz-rpc-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, RpcHandler> _handlers = [];
    private readonly KnowledgeHub _hub = new();
    private readonly PromptCatalog _prompts;
    private readonly UserCssStore _css;

    private const string Template = "---\nname: demo.prompt\nversion: 1\npurpose: demo\nvariables:\n  - who | default=world\n  - text | required | untrusted\n---\nHello {{who}} {{text}}\n";

    public CustomizationRpcTests()
    {
        Directory.CreateDirectory(_dir);
        _prompts = new PromptCatalog(Path.Combine(_dir, "prompts"));
        _prompts.RegisterBuiltIns("demoPlugin", [Template]);
        _css = new UserCssStore(Path.Combine(_dir, "appearance"));
        _hub.RegisterSource(new UserMemorySource(Path.Combine(_dir, "memory.db")));
        foreach (var module in new IRpcModule[] { new PromptsRpcModule(_prompts), new AppearanceRpcModule(_css), new MemoryRpcModule(_hub) })
            foreach (var (method, handler) in module.Handlers) _handlers[method] = handler;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<JsonElement> Call(string method, object? payload = null)
    {
        var request = new RpcRequest(method, JsonSerializer.SerializeToElement(payload ?? new { }, RpcJson.Options));
        return JsonSerializer.SerializeToElement(await _handlers[method](request, CancellationToken.None), RpcJson.Options);
    }

    // ------------------------------------------------------------------ prompts
    [Fact]
    public async Task Prompts_ListShowsTheContract_CheckAndSaveApplyTheRules_ResetGoesBack()
    {
        var list = (await Call("prompts.list")).GetProperty("prompts").EnumerateArray().ToList();
        var demo = Assert.Single(list);
        Assert.Equal(("demo.prompt", "demoPlugin", false), (demo.GetProperty("name").GetString(), demo.GetProperty("owner").GetString(), demo.GetProperty("usingOverride").GetBoolean()));
        var untrusted = demo.GetProperty("variables").EnumerateArray().Single(v => v.GetProperty("name").GetString() == "text");
        Assert.True(untrusted.GetProperty("untrusted").GetBoolean());
        Assert.True(untrusted.GetProperty("required").GetBoolean());

        string bad = Template + "{{nope}}";
        var check = await Call("prompts.check", new { name = "demo.prompt", text = bad });
        Assert.False(check.GetProperty("ok").GetBoolean());
        Assert.Contains("'nope' is used but not declared", check.GetProperty("error").GetString());
        Assert.True(check.GetProperty("line").GetInt32() > 0);

        var refused = await Call("prompts.save", new { name = "demo.prompt", text = bad });
        Assert.False(refused.GetProperty("ok").GetBoolean());

        string good = Template.Replace("Hello", "Greetings");
        var saved = await Call("prompts.save", new { name = "demo.prompt", text = good });
        Assert.True(saved.GetProperty("ok").GetBoolean());
        Assert.True(saved.GetProperty("prompt").GetProperty("usingOverride").GetBoolean());
        Assert.Contains("Greetings", _prompts.Render("demo.prompt", new Dictionary<string, object?> { ["text"] = "x" }));

        var reset = await Call("prompts.reset", new { name = "demo.prompt" });
        Assert.True(reset.GetProperty("removed").GetBoolean());
        Assert.Contains("Hello", _prompts.Render("demo.prompt", new Dictionary<string, object?> { ["text"] = "x" }));
    }

    // ------------------------------------------------------------------ custom CSS
    [Fact]
    public async Task CustomCss_SavesToOneFile_CanBeSwitchedOffWithoutLosingIt_AndRefusesAHugeFile()
    {
        var empty = await Call("appearance.getUserCss");
        Assert.Equal(("", true), (empty.GetProperty("css").GetString(), empty.GetProperty("enabled").GetBoolean()));

        var saved = await Call("appearance.setUserCss", new { css = "html.ir-app { --accent-color: hotpink; }", enabled = true });
        Assert.True(saved.GetProperty("enabled").GetBoolean());
        Assert.Equal("html.ir-app { --accent-color: hotpink; }", File.ReadAllText(Path.Combine(_dir, "appearance", "user.css")));

        var off = await Call("appearance.setUserCssEnabled", new { enabled = false });
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Contains("hotpink", off.GetProperty("css").GetString());             // switched off, not deleted
        Assert.False(new UserCssStore(Path.Combine(_dir, "appearance")).Get().Enabled);   // and it stays off after a restart

        await Assert.ThrowsAsync<ArgumentException>(() => Call("appearance.setUserCss", new { css = new string('a', UserCssStore.MaxBytes + 1) }));
        Assert.Contains("hotpink", _css.Get().Css);                                  // the refused save changed nothing
    }

    // ------------------------------------------------------------------ memory
    [Fact]
    public async Task Memory_ListAddForgetAndForgetAll()
    {
        var added = await Call("memory.add", new { text = "My sister is called Yuna", subject = "family" });
        Assert.True(added.GetProperty("ok").GetBoolean());
        await Call("memory.add", new { text = "I work as a nurse" });

        var refused = await Call("memory.add", new { text = new string('x', UserMemorySource.MaxTextChars + 1) });
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.NotNull(refused.GetProperty("error").GetString());

        var list = await Call("memory.list");
        Assert.True(list.GetProperty("available").GetBoolean());
        var facts = list.GetProperty("facts").EnumerateArray().ToList();
        Assert.Equal(2, facts.Count);
        var sister = facts.Single(f => f.GetProperty("subject").GetString() == "family");
        Assert.Equal("settings", sister.GetProperty("origin").GetString());

        Assert.True((await Call("memory.forget", new { id = sister.GetProperty("id").GetString() })).GetProperty("ok").GetBoolean());
        Assert.Single((await Call("memory.list")).GetProperty("facts").EnumerateArray());

        var all = await Call("memory.forgetAll");
        Assert.Equal(1, all.GetProperty("removed").GetInt32());
        Assert.Empty((await Call("memory.list")).GetProperty("facts").EnumerateArray());
    }

    [Fact]
    public async Task Memory_ListSaysSoWhenNothingCanStoreFacts()
    {
        var module = new MemoryRpcModule(new KnowledgeHub());
        var handler = module.Handlers.Single(h => h.Method == "memory.list").Handler;

        var result = JsonSerializer.SerializeToElement(await handler(new RpcRequest("memory.list", JsonSerializer.SerializeToElement(new { })), default), RpcJson.Options);

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Empty(result.GetProperty("facts").EnumerateArray());
    }
}
