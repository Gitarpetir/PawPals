using System.Security.Cryptography;
using System.Text;
using PawPals.Desktop;

namespace PawPals.Server;

public sealed class RoomStore
{
    private sealed class Player(Guid id, string name, int cat, string hash)
    {
        public Guid Id = id;
        public string Name = name;
        public int CatId = cat;
        public string TokenHash = hash;
        public string? Connection;
        public DateTimeOffset? LastChat, BubbleUntil;
        public CatBehavior Behavior = new();
        public double X = Random.Shared.NextDouble();
        public string? Message;
        public List<(Guid Id, string Text, DateTimeOffset Until)> Bubbles = [];
    }
    private sealed class Room(string code)
    {
        public string Code = code;
        public Guid Epoch = Guid.NewGuid();
        public long Version, Sequence;
        public List<Player> Players = [];
    }
    private sealed class Attempts(DateTimeOffset start) { public DateTimeOffset Start = start; public int Count; }
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Room> rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Room Room, Player Player)> connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Attempts> attempts = new(StringComparer.Ordinal);
    private readonly IRoomEvents events;
    private readonly TimeProvider clock;
    private readonly ILogger<RoomStore> logger;
    private readonly IRoomPersistence disk;
    private DateTimeOffset nextCheckpoint;
    private bool dirty;

    public RoomStore(IRoomEvents events, TimeProvider clock, ILogger<RoomStore> logger, IRoomPersistence? disk = null)
    {
        this.events = events; this.clock = clock; this.logger = logger;
        this.disk = disk ?? new VolatileRoomPersistence();
        var archive = this.disk.Load();
        foreach (var saved in archive.Rooms)
        {
            var room = new Room(RoomRules.Code(saved.Code));
            // Kode dipertahankan, kehadiran hanya milik koneksi yang sedang hidup.
            // Backup migrasi disimpan JsonRoomPersistence sebelum penulisan berikutnya.
            rooms.Add(room.Code, room);
        }
        nextCheckpoint = clock.GetUtcNow().AddSeconds(5);
    }

    // ID/token mengamankan sesi aktif; setelah disconnect pengguna melakukan Join baru.
    public Task<RoomSession> Create(string connection, string? name, int catId) => Enter(connection,
        new PersistentJoin(Guid.NewGuid(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), "create", null, name ?? "", catId));
    public Task<RoomSession> Join(string connection, string? code, string? name, int catId) => Enter(connection,
        new PersistentJoin(Guid.NewGuid(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), "join", code, name ?? "", catId));
    public Task<RoomSession> Resume(string connection, string? code, Guid id, string? token) => Enter(connection,
        new PersistentJoin(id, token ?? "", "resume", code, "", 1));

    public async Task<RoomSession> Enter(string connection, PersistentJoin request)
    {
        await gate.WaitAsync();
        try
        {
            await Maintenance(); LimitAttempts(connection);
            if (request == null || request.PlayerId == Guid.Empty || !ValidSecret(request.ResumeToken)
                || request.Mode is not ("create" or "join" or "resume"))
                throw new RoomFault("INVALID_SESSION", "Identitas sesi tidak valid.");
            foreach (Room existing in rooms.Values)
            {
                Player? member = existing.Players.Find(p => p.Id == request.PlayerId);
                if (member == null) continue;
                if (!ValidToken(member, request.ResumeToken)
                    || (!string.IsNullOrWhiteSpace(request.Code) && RoomRules.Code(request.Code) != existing.Code))
                    throw new RoomFault("INVALID_SESSION", "Identitas sesi tidak cocok.");
                if (member.Connection == connection) return Session(existing, member, request.ResumeToken);
                RequireFree(connection);
                throw new RoomFault("SESSION_ACTIVE", "Sesi aktif pada koneksi sebelumnya. Tutup client lama, lalu Join lagi.");
            }
            if (request.Mode == "resume") throw new RoomFault("INVALID_SESSION", "Keanggotaan tersimpan tidak ditemukan. Masuk room kembali.");
            RequireFree(connection);
            string name = RoomRules.Name(request.Name); RoomRules.Cat(request.CatId);
            Room room;
            bool creating = request.Mode == "create";
            if (creating)
            {
                if (rooms.Count >= 256) throw new RoomFault("SERVER_FULL", "Server pengujian sedang penuh.");
                string code; do { code = NewCode(); } while (rooms.ContainsKey(code));
                room = new Room(code); rooms.Add(code, room);
            }
            else room = FindRoom(request.Code);
            if (room.Players.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new RoomFault("NAME_TAKEN", "Nama sedang dipakai anggota online. Pilih nama lain.");
            if (room.Players.Count >= RoomRules.Capacity)
                throw new RoomFault("ROOM_FULL", "Room penuh. Maksimal 10 pemain online.");
            var player = NewPlayer(request.PlayerId, name, request.CatId, Hash(request.ResumeToken));
            room.Players.Add(player);
            try
            {
                await events.Attach(connection, room.Code);
                Persist();
            }
            catch
            {
                room.Players.Remove(player); if (creating) rooms.Remove(room.Code);
                try { await events.Detach(connection, room.Code); } catch { }
                throw;
            }
            // Kode room sudah tersimpan sebelum join dikonfirmasi.
            player.Connection = connection;
            connections.Add(connection, (room, player));
            await Publish(room);
            return Session(room, player, request.ResumeToken);
        }
        finally { gate.Release(); }
    }

    public async Task<RoomView> Get(string connection)
    {
        await gate.WaitAsync();
        try { return View(RequireMember(connection).Room); }
        finally { gate.Release(); }
    }
    public async Task Leave(string connection)
    {
        await gate.WaitAsync();
        try
        {
            if (!connections.TryGetValue(connection, out var entry)) return;
            await events.Detach(connection, entry.Room.Code);
            int index = entry.Room.Players.IndexOf(entry.Player);
            entry.Room.Players.RemoveAt(index);
            try { Persist(); }
            catch
            {
                entry.Room.Players.Insert(index, entry.Player);
                await events.Attach(connection, entry.Room.Code);
                throw;
            }
            connections.Remove(connection);
            await Publish(entry.Room);
        }
        finally { gate.Release(); }
    }
    public async Task Disconnect(string connection)
    {
        await gate.WaitAsync();
        try
        {
            attempts.Remove(connection);
            if (!connections.Remove(connection, out var entry)) return;
            entry.Room.Players.Remove(entry.Player);
            dirty = true;
            TryCheckpoint(force: true);
            await Publish(entry.Room);
        }
        finally { gate.Release(); }
    }
    public async Task<RoomChat> SendChat(string connection, string? text)
    {
        await gate.WaitAsync();
        try
        {
            var (room, player) = RequireMember(connection);
            string message = RoomRules.Message(text); var now = clock.GetUtcNow();
            if (player.LastChat.HasValue && now - player.LastChat.Value < TimeSpan.FromMilliseconds(750))
                throw new RoomFault("CHAT_TOO_FAST", "Tunggu sebentar sebelum mengirim lagi.");
            player.LastChat = now; player.Message = message; player.BubbleUntil = now.AddSeconds(6);
            player.Behavior.BeginChatPause();
            var chat = new RoomChat(Guid.NewGuid(), player.Id, player.Name, message, now, room.Code);
            player.Bubbles.RemoveAll(b => b.Until <= now);
            player.Bubbles.Add((chat.Id, message, now.AddSeconds(6)));
            await events.Chat(room.Code, chat); await PublishWorld(room, true); return chat;
        }
        finally { gate.Release(); }
    }
    public async Task Interact(string connection, string action)
    {
        await gate.WaitAsync();
        try
        {
            var (room, player) = RequireMember(connection);
            if (action == "click") player.Behavior.Click();
            else if (action == "sleep") player.Behavior.RequestSleep();
            else if (player.Behavior.RequestAction(action)) { }
            else throw new RoomFault("INVALID_ACTION", "Interaksi tidak dikenal.");
            await PublishWorld(room);
        }
        finally { gate.Release(); }
    }
    // Pemeliharaan batas percobaan dan retry checkpoint kode room.
    public async Task Expire()
    {
        await gate.WaitAsync();
        try { await Maintenance(); }
        finally { gate.Release(); }
    }
    private Task Maintenance()
    {
        var now = clock.GetUtcNow();
        foreach (var item in attempts.ToArray())
            if (now - item.Value.Start >= TimeSpan.FromSeconds(10)) attempts.Remove(item.Key);
        TryCheckpoint(false); return Task.CompletedTask;
    }
    public async Task Tick(double delta)
    {
        await gate.WaitAsync();
        try
        {
            var now = clock.GetUtcNow();
            foreach (var room in rooms.Values)
            {
                if (!room.Players.Any(p => p.Connection != null)) continue;
                foreach (var player in room.Players)
                {
                    if (player.Connection == null) continue;
                    player.Bubbles.RemoveAll(b => b.Until <= now);
                    if (player.BubbleUntil.HasValue && player.BubbleUntil <= now)
                    { player.BubbleUntil = null; player.Message = null; player.Behavior.EndChatPause(); }
                    player.Behavior.Tick(delta);
                    if (player.BubbleUntil.HasValue) continue;
                    int direction = player.Behavior.Direction;
                    double speed = player.Behavior.Speed / 1600.0;
                    player.X = Math.Clamp(player.X + direction * speed * delta, 0, 1);
                    if (speed > 0) { if (player.X <= 0) player.Behavior.Face(1); else if (player.X >= 1) player.Behavior.Face(-1); }
                }
                await PublishWorld(room);
            }
            TryCheckpoint(false);
        }
        finally { gate.Release(); }
    }
    private void Persist()
    {
        try
        {
            // Versi baru hanya menyimpan kode; tidak menyimpan token/anggota offline.
            disk.Save(new RoomArchive(1, rooms.Values.Select(r => new SavedRoom(r.Code, Array.Empty<SavedPlayer>())).ToArray()));
            dirty = false; nextCheckpoint = clock.GetUtcNow().AddSeconds(5);
        }
        catch (Exception ex) when (JsonRoomPersistence.StorageError(ex))
        { throw new RoomFault("STORAGE_ERROR", "Server belum dapat menyimpan data room. Perubahan belum dikonfirmasi."); }
    }
    private void TryCheckpoint(bool force)
    {
        if (!dirty || (!force && clock.GetUtcNow() < nextCheckpoint)) return;
        nextCheckpoint = clock.GetUtcNow().AddSeconds(5);
        try { Persist(); }
        catch (RoomFault) { logger.LogError("Room checkpoint failed. Membership remains stored; last position may be older."); }
    }
    private async Task Publish(Room room)
    {
        room.Version++;
        try { await events.Changed(View(room)); } catch (Exception ex) { logger.LogWarning(ex, "Room notification failed."); }
        await PublishWorld(room, true);
    }
    private async Task PublishWorld(Room room, bool includeBubbles = false)
    {
        room.Sequence++;
        try { await events.World(World(room, includeBubbles)); } catch (Exception ex) { logger.LogWarning(ex, "World notification failed."); }
    }
    private WorldView World(Room room, bool includeBubbles = true) => new(room.Code, room.Sequence, room.Players.Select(p =>
        new PetFrame(p.Id, p.X, p.Behavior.Direction,
            p.Connection == null ? "Sleeping1" : p.Behavior.Animation.ActionName,
            p.Connection == null ? 0 : p.Behavior.FrameIndex,
            p.Connection == null ? 1 + .025 * Math.Sin(clock.GetUtcNow().TimeOfDay.TotalSeconds * Math.PI * 2 / 3) : p.Behavior.BreathingScale,
            p.Connection == null ? null : p.Message,
            p.Connection != null && p.BubbleUntil.HasValue ? Math.Max(0, (p.BubbleUntil.Value - clock.GetUtcNow()).TotalSeconds) : 0,
            p.Connection != null,
            includeBubbles ? p.Bubbles.Where(b => b.Until > clock.GetUtcNow()).Select(b => new BubbleView(b.Id, b.Text,
                Math.Max(0, (b.Until - clock.GetUtcNow()).TotalSeconds))).ToArray() : null)).ToArray(), room.Epoch);
    private static RoomView View(Room room) => new(room.Code, RoomRules.Capacity, room.Version,
        room.Players.Select(p => new PlayerView(p.Id, p.Name, p.CatId, p.Connection != null, null)).ToArray(), room.Epoch);
    private RoomSession Session(Room room, Player player, string token) => new(player.Id, token, View(room), World(room), 10);
    private void LimitAttempts(string connection)
    {
        if (!attempts.TryGetValue(connection, out var value)) attempts[connection] = value = new Attempts(clock.GetUtcNow());
        if (++value.Count > 15) throw new RoomFault("TOO_MANY_ATTEMPTS", "Terlalu banyak percobaan. Tunggu 10 detik.");
    }
    private void RequireFree(string connection)
    { if (connections.ContainsKey(connection)) throw new RoomFault("ALREADY_IN_ROOM", "Keluar room saat ini terlebih dahulu."); }
    private (Room Room, Player Player) RequireMember(string connection) => connections.TryGetValue(connection, out var value)
        ? value : throw new RoomFault("NOT_IN_ROOM", "Masuk room terlebih dahulu.");
    private Room FindRoom(string? code) => rooms.TryGetValue(RoomRules.Code(code), out var room)
        ? room : throw new RoomFault("ROOM_NOT_FOUND", "Room tidak ditemukan atau telah ditutup.");
    private static Player NewPlayer(Guid id, string name, int cat, string hash)
    { var value = new Player(id, name, cat, hash); value.Behavior.SetCanItch(cat != 3); return value; }
    private static string NewCode() => string.Concat(Enumerable.Range(0, 6).Select(_ => RoomRules.CodeAlphabet[RandomNumberGenerator.GetInt32(RoomRules.CodeAlphabet.Length)]));
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static bool ValidSecret(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ValidToken(Player player, string token) => CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(player.TokenHash), Encoding.ASCII.GetBytes(Hash(token)));
}
