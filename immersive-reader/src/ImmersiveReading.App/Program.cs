using ImmersiveReading.App.Cli;
using ImmersiveReading.App.Composition;
using ImmersiveReading.App.Hosting;

namespace ImmersiveReading.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.Discover();
        using var services = AppServices.Create(paths, PluginCatalog.CreateModules());

        if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            return SmokeTest.RunAsync(services).GetAwaiter().GetResult();
        }

        if (args.Contains("--verify-reader", StringComparer.OrdinalIgnoreCase))
        {
            return ReaderVerification.RunAsync(services, args).GetAwaiter().GetResult();
        }

        bool verifyUi = args.Contains("--verify-ui", StringComparer.OrdinalIgnoreCase);
        return new MainWindowHost(services, paths).Run(verifyUi);
    }

    public static Stream HandleAppSchemeRequest(
        string url,
        string wwwrootDir,
        string coversDir,
        out string contentType)
        => AppSchemeHandler.HandleRequest(url, wwwrootDir, coversDir, out contentType);

    public static Stream HandleAppSchemeRequest(
        string url,
        string wwwrootDir,
        string coversDir,
        string? extractorOutputDir,
        out string contentType)
        => AppSchemeHandler.HandleRequest(url, wwwrootDir, coversDir, extractorOutputDir, out contentType);
}
