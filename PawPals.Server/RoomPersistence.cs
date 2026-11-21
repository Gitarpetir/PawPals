using System.Text.Json;

namespace PawPals.Server;

public sealed record SavedPlayer(Guid Id, string Name, int CatId, string TokenHash, double X, int Direction);
public sealed record SavedRoom(string Code, SavedPlayer[] Players);
public sealed record RoomArchive(int Schema, SavedRoom[] Rooms);
public interface IRoomPersistence
{
    RoomArchive Load();
    void Save(RoomArchive value);
}
public sealed class VolatileRoomPersistence : IRoomPersistence
{
    public RoomArchive Load() => new(1, []);
    public void Save(RoomArchive value) { }
}

public sealed class JsonRoomPersistence : IRoomPersistence, IDisposable
{
    private readonly string path;
    private readonly FileStream ownership;
    private readonly ILogger<JsonRoomPersistence> logger;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public JsonRoomPersistence(string path, ILogger<JsonRoomPersistence> logger)
    {
        this.path = Path.GetFullPath(path); this.logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        ownership = new FileStream(this.path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public void Dispose() => ownership.Dispose();
    public RoomArchive Load()
    {
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return new(1, []);
        try
        {
            var value = Read(path);
            // Satu salinan permanen sebelum perubahan semantik offline 0.10.
            if (File.Exists(path) && !File.Exists(path + ".before-v0.10"))
                File.Copy(path, path + ".before-v0.10", false);
            return value;
        }
        catch (Exception first) when (StorageError(first))
        {
            try
            {
                var restored = Read(path + ".bak");
                logger.LogWarning("Primary room data could not be read. Restoring previous backup; last update may be missing.");
                return restored;
            }
            catch (Exception second) when (StorageError(second))
            { throw new InvalidDataException("Data room dan backup tidak dapat dibaca. Pulihkan data server; file tidak dihapus otomatis.", first); }
        }
    }
    private static RoomArchive Read(string file)
    {
        var value = JsonSerializer.Deserialize<RoomArchive>(File.ReadAllText(file), Options);
        if (value?.Schema != 1 || value.Rooms == null) throw new InvalidDataException("Format data room tidak valid.");
        return value;
    }
    public void Save(RoomArchive value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, value, Options); file.Flush(true); }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }
    public static bool StorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException;
}
