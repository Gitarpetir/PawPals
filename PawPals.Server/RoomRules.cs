using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PawPals.Server;

public static class RoomRules
{
    public const int Capacity = 10;
    public const int NameLimit = 16;
    public const int MessageLimit = 150;
    public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Name(string? raw)
    {
        string value = Normalize(raw, 256, "INVALID_NAME").Trim();
        if (value.Length == 0 || value.Any(char.IsControl)
            || new StringInfo(value).LengthInTextElements > NameLimit)
            throw new RoomFault("INVALID_NAME", "Nama wajib diisi, maksimal 16 karakter, tanpa karakter kontrol.");
        return value;
    }

    public static string Message(string? raw)
    {
        string value = Regex.Replace(Normalize(raw, 4096, "INVALID_MESSAGE"), @"\s+", " ").Trim();
        if (value.Length == 0 || value.Any(char.IsControl)
            || new StringInfo(value).LengthInTextElements > MessageLimit)
            throw new RoomFault("INVALID_MESSAGE", "Pesan wajib diisi, maksimal 150 karakter.");
        return value;
    }

    public static string Code(string? raw)
    {
        string value = (raw ?? "").Trim().ToUpperInvariant();
        if (value.Length != 6 || value.Any(c => !CodeAlphabet.Contains(c)))
            throw new RoomFault("INVALID_CODE", "Kode room harus berisi 6 huruf/angka yang valid.");
        return value;
    }

    public static void Cat(int id)
    {
        if (id < 1 || id > 6) throw new RoomFault("INVALID_CAT", "Pilih kucing 1 sampai 6.");
    }

    private static string Normalize(string? raw, int maxRawLength, string error)
    {
        if (raw == null || raw.Length > maxRawLength)
            throw new RoomFault(error, "Teks kosong atau terlalu panjang.");
        try { return raw.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { throw new RoomFault(error, "Teks berisi Unicode yang tidak valid."); }
    }
}
