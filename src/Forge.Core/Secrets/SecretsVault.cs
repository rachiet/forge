using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.Core.Secrets;

/// <summary>
/// Encrypted-at-rest secret store under ForgeDataRoot/vault. Values are AES-GCM
/// encrypted with a machine-local key file (owner-only permissions). Secret
/// VALUES exist only here and in process arguments at exec time — never in the
/// database, agent context, or logs.
/// </summary>
public sealed class SecretsVault
{
    private readonly string _keyPath;
    private readonly string _storePath;

    /// <summary>Opens the vault in a directory, creating it if it does not exist.</summary>
    public SecretsVault(string vaultDir)
    {
        Directory.CreateDirectory(vaultDir);
        _keyPath = Path.Combine(vaultDir, "key.bin");
        _storePath = Path.Combine(vaultDir, "secrets.json");
    }

    /// <summary>Stores a secret, replacing any value already held under that name.</summary>
    public void Set(string name, string value)
    {
        ValidateName(name);
        var store = LoadStore();
        store[name] = Encrypt(value);
        var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
        WriteOwnerOnly(_storePath, Encoding.UTF8.GetBytes(json));
    }

    /// <summary>The value of a stored secret; throws when there is none by that name.</summary>
    public string Get(string name)
    {
        ValidateName(name);
        return LoadStore().TryGetValue(name, out var blob)
            ? Decrypt(blob)
            : throw new KeyNotFoundException($"Secret '{name}' is not in the vault.");
    }

    /// <summary>Whether a secret is stored under this name.</summary>
    public bool Contains(string name) => LoadStore().ContainsKey(name);

    /// <summary>The names of every stored secret, alphabetically. Never their values.</summary>
    public IReadOnlyList<string> Names() => LoadStore().Keys.OrderBy(n => n).ToList();

    /// <summary>Secret names appear in agent context as {{secret:NAME}}; keep them shell-safe.</summary>
    /// <summary>Throws unless the name is one a {{secret:NAME}} placeholder could reference.</summary>
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            throw new ArgumentException(
                $"Invalid secret name '{name}': use letters, digits, or '_'.", nameof(name));
        }
    }

    /// <summary>The decrypted store, or an empty one when the vault is new.</summary>
    private Dictionary<string, string> LoadStore() =>
        File.Exists(_storePath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_storePath)) ?? []
            : [];

    /// <summary>Encrypts the store with the machine-local key.</summary>
    private string Encrypt(string plaintext)
    {
        var key = GetOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>Decrypts a stored blob with the machine-local key.</summary>
    private string Decrypt(string blob)
    {
        var key = GetOrCreateKey();
        var bytes = Convert.FromBase64String(blob);
        var nonceLen = AesGcm.NonceByteSizes.MaxSize;
        var tagLen = AesGcm.TagByteSizes.MaxSize;
        var nonce = bytes.AsSpan(0, nonceLen);
        var tag = bytes.AsSpan(nonceLen, tagLen);
        var cipher = bytes.AsSpan(nonceLen + tagLen);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key.AsSpan(), tagLen);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>The vault's key, generating and writing one on first use.</summary>
    private byte[] GetOrCreateKey()
    {
        if (!File.Exists(_keyPath))
            WriteOwnerOnly(_keyPath, RandomNumberGenerator.GetBytes(32));
        return File.ReadAllBytes(_keyPath);
    }

    /// <summary>Writes a file only its owner can read.</summary>
    private static void WriteOwnerOnly(string path, byte[] content)
    {
        File.WriteAllBytes(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
