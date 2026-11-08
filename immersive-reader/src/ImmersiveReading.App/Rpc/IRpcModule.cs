namespace ImmersiveReading.App.Rpc;

public delegate Task<object?> RpcHandler(RpcRequest request, CancellationToken ct);

public interface IRpcModule
{
    IEnumerable<(string Method, RpcHandler Handler)> Handlers { get; }
}

