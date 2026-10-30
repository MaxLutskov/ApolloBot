using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ApolloBreakdowns.Telegram;

// The signed payload fits Telegram's 64-byte callback_data limit. It binds the
// confirmation to its user, chat, source message, action, report and expiry.
public sealed record ActionConfirmation(int ReportId, long UserId, long ChatId, int MessageId, bool Start)
{
    public string Sign(string key, DateTimeOffset now)
    {
        var bytes = new byte[41];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0), ReportId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), UserId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), ChatId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), MessageId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), checked((uint)now.AddMinutes(5).ToUnixTimeSeconds()));
        bytes[28] = Start ? (byte)1 : (byte)0;
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), bytes.AsSpan(0, 29)).AsSpan(0, 12).CopyTo(bytes.AsSpan(29));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static ActionConfirmation? Read(string token, string key, long userId, long chatId, DateTimeOffset now)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/').PadRight((token.Length + 3) / 4 * 4, '=')); }
        catch (FormatException) { return null; }
        if (bytes.Length != 41 || bytes[28] > 1) return null;
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), bytes.AsSpan(0, 29));
        if (!CryptographicOperations.FixedTimeEquals(bytes.AsSpan(29), signature.AsSpan(0, 12))) return null;
        if (BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(4)) != userId ||
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(12)) != chatId ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)) < now.ToUnixTimeSeconds()) return null;
        return new(BinaryPrimitives.ReadInt32LittleEndian(bytes), userId, chatId,
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20)), bytes[28] == 1);
    }
}
