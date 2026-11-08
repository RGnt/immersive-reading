using ImmersiveReading.App.Composition;
using ImmersiveReading.App.Rpc;

namespace ImmersiveReading.App.Cli;

public static class SmokeTest
{
    public static async Task<int> RunAsync(AppServices services)
    {
        var dispatcher = new LibraryRpcDispatcher(services);
        string response = await dispatcher
            .DispatchJsonAsync("""{"id":"smoke-1","method":"library.getBootstrapState","params":{}}""")
            .ConfigureAwait(false);
        Console.WriteLine(response);
        return 0;
    }
}

