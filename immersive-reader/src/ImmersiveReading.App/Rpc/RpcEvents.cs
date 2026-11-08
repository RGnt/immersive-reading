using System.Text.Json;

namespace ImmersiveReading.App.Rpc;

public interface IRpcEventSink
{
    void Emit(string eventName, object data);
}

public sealed class DelegateRpcEventSink(Action<string>? sendMessageToUi) : IRpcEventSink
{
    public void Emit(string eventName, object data)
    {
        if (sendMessageToUi is null) return;
        string json = JsonSerializer.Serialize(new { @event = eventName, data }, RpcJson.Options);
        sendMessageToUi(json);
    }
}

