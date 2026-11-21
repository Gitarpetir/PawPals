namespace PawPals.Server;

public sealed class RoomFault(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IRoomEvents
{
    Task Attach(string connectionId, string code);
    Task Detach(string connectionId, string code);
    Task Changed(RoomView room);
    Task Chat(string code, RoomChat message);
    Task World(WorldView world);
}
