using System.Security.Cryptography;
using System.Text.Json;

namespace Magicians.Application;

// Accidental/tamper detection; the public digest is not a cryptographic trust anchor.
public static class ReleaseIntegrity
{
    public static void Verify(string directory)
    {
        var manifest = Path.Combine(directory, "integrity.json");
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest))
            ?? throw new InvalidDataException("Missing release file list.");
        foreach (var (relative, expected) in hashes)
        {
            var path = Path.GetFullPath(Path.Combine(directory, relative));
            if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path) || !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("游戏文件缺失或已被修改，请重新解压完整发布包：" + relative);
        }
    }
}
