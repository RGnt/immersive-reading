using ImmersiveReading.App.Composition;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Config;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ImmersiveReading.App.Tests;

public sealed class AppServicesContainerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _paths;

    public AppServicesContainerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ir_appservices_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _paths = new AppPaths(
            ReaderRoot: _tempDir,
            WorkspaceRoot: _tempDir,
            AppDataDir: _tempDir,
            DbPath: Path.Combine(_tempDir, "library.db"),
            DictDbPath: Path.Combine(_tempDir, "dictionaries.db"),
            CoversDir: Path.Combine(_tempDir, "covers"),
            ExtractorDir: _tempDir,
            ExtractorOutputDir: Path.Combine(_tempDir, "output"),
            ExtractorBooksDir: Path.Combine(_tempDir, "books"),
            WwwrootDir: _tempDir);
    }

    private sealed class CustomTestPluginModule : IPluginModule
    {
        public string ModuleId => "test-plugin";
        public bool ConfiguredServicesCalled { get; private set; }

        public void ConfigureServices(IServiceCollection services)
        {
            ConfiguredServicesCalled = true;
            services.AddSingleton(new TestCustomService("injected-via-ioc"));
        }

        public void Dispose() {}
    }

    private sealed record TestCustomService(string Value);

    [Fact]
    public void Create_RegistersCoreAbstractionsInServiceProvider()
    {
        using var services = AppServices.Create(_paths, []);

        var sp = services.ServiceProvider;
        Assert.NotNull(sp.GetService<IDbConnectionFactory>());
        Assert.NotNull(sp.GetService<IBookCatalog>());
        Assert.NotNull(sp.GetService<ILanguagePluginResolver>());
        Assert.NotNull(sp.GetService<IChapterTokenMapProvider>());
        Assert.NotNull(sp.GetService<IVocabularyService>());
        Assert.NotNull(sp.GetService<IReaderContextService>());
        Assert.NotNull(sp.GetService<ISnippetEnricher>());
        Assert.NotNull(sp.GetService<IVocabularyBudgetService>());
        Assert.NotNull(sp.GetService<IConfigurationManager>());
    }

    [Fact]
    public void Create_CallsConfigureServicesOnPluginModules()
    {
        var testModule = new CustomTestPluginModule();
        using var services = AppServices.Create(_paths, [testModule]);

        Assert.True(testModule.ConfiguredServicesCalled);
        var custom = services.ServiceProvider.GetService<TestCustomService>();
        Assert.NotNull(custom);
        Assert.Equal("injected-via-ioc", custom.Value);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
