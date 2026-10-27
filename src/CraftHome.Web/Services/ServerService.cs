namespace CraftHome.Web.Services;

public sealed class ServerService(ConnectionStore connections, McsManagerClient engine, ILogger<ServerService> logger)
{
    private readonly SemaphoreSlim controlGate = new(1, 1);
    private int demoStatus;

    public async Task<InstanceSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var connection = connections.Read();
        return connection.DemoMode ? new(Volatile.Read(ref demoStatus), null, false)
            : await engine.GetSnapshotAsync(connection, ct);
    }

    public Task<string> GetLogsAsync(CancellationToken ct = default)
    {
        var connection = connections.Read();
        return connection.DemoMode
            ? Task.FromResult("[演示模式] 当前未连接真实服务器。\n[演示模式] 启停只改变演示状态，不启动 Minecraft。")
            : engine.GetLogsAsync(connection, ct);
    }

    public async Task ControlAsync(string action, CancellationToken ct = default)
    {
        if (action is not ("start" or "stop" or "restart"))
            throw new ArgumentException("不支持的操作。", nameof(action));
        if (!await controlGate.WaitAsync(0, ct))
            throw new EngineException("另一个控制请求正在提交，请稍后刷新。");
        try
        {
            var connection = connections.Read();
            if (connection.DemoMode)
                Volatile.Write(ref demoStatus, action == "stop" ? 0 : 3);
            else
                await engine.ControlAsync(connection, action, ct);
            logger.LogInformation("Server control submitted: {Action}; demo={Demo}", action, connection.DemoMode);
        }
        finally { controlGate.Release(); }
    }
}
