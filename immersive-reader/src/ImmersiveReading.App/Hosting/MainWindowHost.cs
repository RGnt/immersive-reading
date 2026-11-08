using ImmersiveReading.App.Composition;
using ImmersiveReading.App.Rpc;
using Photino.NET;

namespace ImmersiveReading.App.Hosting;

public sealed class MainWindowHost(AppServices services, AppPaths paths)
{
    public int Run(bool verifyUi = false)
    {
        int verifyExitCode = 0;
        PhotinoWindow? window = null;

        var dispatcher = new LibraryRpcDispatcher(
            services,
            sendMessageToUi: msg => window?.SendWebMessage(msg),
            onSelfTestCompleted: (passed, summary) =>
            {
                Console.WriteLine(summary);
                verifyExitCode = passed ? 0 : 1;
                window?.Close();
            },
            folderPicker: defaultPath =>
            {
                var picked = window?.ShowOpenFolder(
                    title: "Select Folder to Scan for Books",
                    defaultPath: defaultPath ?? "");
                return picked != null && picked.Length > 0 ? picked[0] : null;
            });

        var appSchemeHandler = new AppSchemeHandler(paths.WwwrootDir, paths.CoversDir, paths.ExtractorOutputDir, services.PluginHostManager);

        window = new PhotinoWindow()
            .SetTitle("Immersive Reading — EPUB Library & Reader")
            .SetUseOsDefaultSize(false)
            .SetSize(1480, 940)
            .Center()
            .RegisterCustomSchemeHandler("ir-app", (object sender, string scheme, string url, out string contentType) =>
            {
                return appSchemeHandler.Handle(url, out contentType);
            })
            .RegisterCustomSchemeHandler("ir-book", (object sender, string scheme, string url, out string contentType) =>
            {
                return services.BookScheme.Handle(url, out contentType);
            })
            .RegisterWebMessageReceivedHandler(async (object? sender, string message) =>
            {
                string reply = await dispatcher.DispatchJsonAsync(message).ConfigureAwait(false);
                window?.SendWebMessage(reply);

                if (verifyUi && message.Contains("\"library.getBootstrapState\"", StringComparison.Ordinal))
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                        window?.SendWebMessage("""{"event":"ui.runReaderSelfTest","data":{}}""");
                    });
                }
            });

        string indexHtmlPath = Path.Combine(paths.WwwrootDir, "dist", "index.html");
        window.Load(indexHtmlPath);
        window.WaitForClose();
        return verifyExitCode;
    }
}

