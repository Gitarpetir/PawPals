using System.Net;
using System.Text.Json;
using Npgsql;

namespace PawPals.Server;

// One authoritative game process. A startup fences the preceding deployment's writes.
// Connections are opened only for room metadata changes, never on the 20 Hz movement tick.
public sealed class PostgresRoomPersistence : IRoomPersistence, IDisposable
{
    private readonly NpgsqlDataSource source;
    private readonly Action superseded;
    private readonly Guid owner = Guid.NewGuid();
    private readonly object gate = new();
    private bool loaded;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public PostgresRoomPersistence(string connection, Action? onSuperseded = null)
    {
        source = NpgsqlDataSource.Create(ConnectionString(connection));
        superseded = onSuperseded ?? (() => { });
    }

    public static string ConnectionString(string value)
    {
        try
        {
            NpgsqlConnectionStringBuilder b;
            if (value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(value);
                string[] auth = uri.UserInfo.Split(':', 2);
                if (auth.Length != 2 || string.IsNullOrWhiteSpace(uri.Host) || uri.Fragment.Length != 0)
                    throw new ArgumentException();
                b = new NpgsqlConnectionStringBuilder
                {
                    Host = uri.Host, Port = uri.IsDefaultPort ? 5432 : uri.Port,
                    Username = Uri.UnescapeDataString(auth[0]), Password = Uri.UnescapeDataString(auth[1]),
                    Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
                    SslMode = SslMode.VerifyFull
                };
                // Neon URI query parameters (sslmode/channel_binding) need not be passed to Npgsql.
                // Remote TLS is always verified, including when the pasted URI says sslmode=require.
            }
            else b = new NpgsqlConnectionStringBuilder(value);
            if (string.IsNullOrWhiteSpace(b.Host) || string.IsNullOrWhiteSpace(b.Database)
                || string.IsNullOrWhiteSpace(b.Username)) throw new ArgumentException();
            bool local = b.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(b.Host, out var ip) && IPAddress.IsLoopback(ip));
            if (!local) b.SslMode = SslMode.VerifyFull;
            b.Timeout = 15; b.CommandTimeout = 15; b.MaxPoolSize = 3; b.MinPoolSize = 0;
            b.IncludeErrorDetail = false; b.LogParameters = false; b.PersistSecurityInfo = false;
            return b.ConnectionString;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or KeyNotFoundException)
        { throw new InvalidOperationException("DATABASE_URL tidak valid. Gunakan URI PostgreSQL dari Neon atau connection string Npgsql."); }
    }

    public RoomArchive Load()
    {
        lock (gate)
        {
            try
            {
                using var connection = source.OpenConnection();
                EnsureSchema(connection);
                using var tx = connection.BeginTransaction();
                using var read = new NpgsqlCommand("SELECT codes FROM pawpals_room_catalog WHERE id=1 FOR UPDATE", connection, tx);
                var archive = Decode((string)read.ExecuteScalar()!);
                using var claim = new NpgsqlCommand("UPDATE pawpals_room_catalog SET owner=@owner WHERE id=1", connection, tx);
                claim.Parameters.AddWithValue("owner", owner); claim.ExecuteNonQuery();
                tx.Commit(); loaded = true;
                return archive;
            }
            catch (Exception ex) when (DatabaseError(ex))
            { throw new IOException("Database room belum dapat dibaca. Data tidak diganti dengan room kosong."); }
        }
    }

    public void Save(RoomArchive value)
    {
        string json = Encode(value);
        lock (gate)
        {
            if (!loaded) throw new InvalidOperationException("Load harus dipanggil sebelum Save.");
            try
            {
                using var connection = source.OpenConnection();
                using var command = new NpgsqlCommand("UPDATE pawpals_room_catalog SET codes=@codes, updated_at=now() WHERE id=1 AND owner=@owner", connection);
                command.Parameters.AddWithValue("codes", json); command.Parameters.AddWithValue("owner", owner);
                if (command.ExecuteNonQuery() != 1)
                {
                    superseded();
                    throw new IOException("Proses server telah digantikan deployment baru. Gunakan satu instance server.");
                }
            }
            catch (Exception ex) when (DatabaseError(ex))
            { throw new IOException("Database room belum dapat menyimpan perubahan. Coba kembali saat database tersedia."); }
        }
    }

    // Run before first deployment, or while service is stopped. Import merges, never deletes codes.
    public int ImportCodes(string file)
    {
        var archive = JsonSerializer.Deserialize<RoomArchive>(File.ReadAllText(file), Options);
        if (archive?.Schema != 1 || archive.Rooms == null) throw new InvalidDataException("Format JSON room tidak valid.");
        string[] incoming = Codes(archive);
        lock (gate)
        {
            try
            {
                using var connection = source.OpenConnection(); EnsureSchema(connection);
                using var tx = connection.BeginTransaction();
                using var read = new NpgsqlCommand("SELECT codes FROM pawpals_room_catalog WHERE id=1 FOR UPDATE", connection, tx);
                var existing = Decode((string)read.ExecuteScalar()!);
                var merged = existing.Rooms.Select(r => r.Code).Concat(incoming).Distinct().Order().ToArray();
                if (merged.Length > 256) throw new InvalidDataException("Maksimal 256 kode room.");
                using var write = new NpgsqlCommand("UPDATE pawpals_room_catalog SET codes=@codes, owner=@owner, updated_at=now() WHERE id=1", connection, tx);
                write.Parameters.AddWithValue("codes", JsonSerializer.Serialize(merged));
                // Fence old snapshots so they cannot erase the newly imported codes.
                write.Parameters.AddWithValue("owner", Guid.NewGuid()); write.ExecuteNonQuery(); tx.Commit();
                return merged.Length;
            }
            catch (Exception ex) when (DatabaseError(ex))
            { throw new IOException("Impor database gagal. File sumber tidak diubah."); }
        }
    }

    private static string[] Codes(RoomArchive archive)
    {
        if (archive.Schema != 1 || archive.Rooms == null) throw new InvalidDataException("Format archive tidak valid.");
        string[] codes = archive.Rooms.Select(r => RoomRules.Code(r.Code)).Distinct().Order().ToArray();
        if (codes.Length > 256) throw new InvalidDataException("Maksimal 256 kode room.");
        return codes;
    }
    private static string Encode(RoomArchive value) => JsonSerializer.Serialize(Codes(value));
    private static RoomArchive Decode(string json)
    {
        string[] codes = JsonSerializer.Deserialize<string[]>(json) ?? throw new InvalidDataException("Katalog room rusak.");
        var archive = new RoomArchive(1, codes.Select(c => new SavedRoom(c, [])).ToArray());
        _ = Codes(archive); return archive;
    }
    private static void EnsureSchema(NpgsqlConnection connection)
    {
        using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS pawpals_room_catalog (
                id integer PRIMARY KEY CHECK (id=1),
                codes text NOT NULL, owner uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            INSERT INTO pawpals_room_catalog (id,codes,owner)
            VALUES (1,'[]','00000000-0000-0000-0000-000000000000') ON CONFLICT (id) DO NOTHING;
            """, connection);
        command.ExecuteNonQuery();
    }
    private static bool DatabaseError(Exception ex) => ex is NpgsqlException or TimeoutException;
    public void Dispose() => source.Dispose();
}
