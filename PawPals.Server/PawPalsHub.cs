using Microsoft.AspNetCore.SignalR;

namespace PawPals.Server;

public interface IPawPalsClient
{
    Task RoomChanged(RoomView room);
    Task ChatReceived(RoomChat message);
    Task WorldUpdated(WorldView world);
}

public sealed class PawPalsHub(RoomStore rooms) : Hub<IPawPalsClient>
{
    public Task<RoomSession> EnterPersistent(PersistentJoin request) => Run(() => rooms.Enter(Context.ConnectionId, request));
    public Task<RoomSession> CreateRoom(string name, int catId) => Run(() => rooms.Create(Context.ConnectionId, name, catId));
    public Task<RoomSession> JoinRoom(string code, string name, int catId) => Run(() => rooms.Join(Context.ConnectionId, code, name, catId));
    public Task<RoomSession> ResumeRoom(string code, Guid playerId, string token) => Run(() => rooms.Resume(Context.ConnectionId, code, playerId, token));
    public Task<RoomView> GetRoom() => Run(() => rooms.Get(Context.ConnectionId));
    public Task<RoomChat> SendChat(string text) => Run(() => rooms.SendChat(Context.ConnectionId, text));
    public Task Interact(string action) => Run(async () => { await rooms.Interact(Context.ConnectionId, action); return true; });
    public Task LeaveRoom() => Run(async () => { await rooms.Leave(Context.ConnectionId); return true; });

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await rooms.Disconnect(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (RoomFault ex) { throw new HubException($"{ex.Code}: {ex.Message}"); }
    }
}

public sealed class SignalRRoomEvents(IHubContext<PawPalsHub, IPawPalsClient> hub) : IRoomEvents
{
    public Task Attach(string connectionId, string code) => hub.Groups.AddToGroupAsync(connectionId, code);
    public Task Detach(string connectionId, string code) => hub.Groups.RemoveFromGroupAsync(connectionId, code);
    public Task Changed(RoomView room) => hub.Clients.Group(room.Code).RoomChanged(room);
    public Task Chat(string code, RoomChat message) => hub.Clients.Group(code).ChatReceived(message);
    public Task World(WorldView world) => hub.Clients.Group(world.Code).WorldUpdated(world);
}

public sealed class RoomMaintenanceWorker(RoomStore rooms) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) await rooms.Expire(); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

public sealed class WorldWorker(RoomStore rooms) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double previous = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                double now = clock.Elapsed.TotalSeconds;
                double delta = Math.Clamp(now - previous, 0, 0.1);
                previous = now;
                await rooms.Tick(delta);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
