using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Magicians.Native;

public static class PyCompat
{
    public static string StringRepr(string text)
    {
        var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
        var result = new StringBuilder().Append(quote);
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value == quote || value == '\\') result.Append('\\').Append((char)value);
            else if (value == '\n') result.Append("\\n");
            else if (value == '\r') result.Append("\\r");
            else if (value == '\t') result.Append("\\t");
            else if (Rune.IsControl(rune)) result.Append(value <= 255 ? "\\x" + value.ToString("x2") : value <= 65535 ? "\\u" + value.ToString("x4") : "\\U" + value.ToString("x8"));
            else result.Append(rune.ToString());
        }
        return result.Append(quote).ToString();
    }
    public static string TupleRepr(IEnumerable<string> strings)
    {
        var values = strings.Select(StringRepr).ToArray();
        return "(" + string.Join(", ", values) + (values.Length == 1 ? "," : "") + ")";
    }
    public static BigInteger BattleSeed(BigInteger seed, IEnumerable<string> deck)
    {
        var representation = "(" + seed.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", " + TupleRepr(deck) + ")";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(representation));
        return new BigInteger(BinaryPrimitives.ReadUInt64BigEndian(bytes));
    }
}
