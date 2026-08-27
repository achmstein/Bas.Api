using System.Security.Cryptography;
using System.Text;

namespace Bas.Api.Security;

/// <summary>
/// Authenticated encryption for the few values that must not sit in the database as plaintext —
/// signing-key private material today, worker TFNs when phase 3b lands.
/// </summary>
public interface IDataEncryptor
{
    /// <param name="purpose">Bound into the ciphertext as associated data, so a blob encrypted for
    /// one purpose cannot be replayed into another column that happens to share the key.</param>
    byte[] Encrypt(ReadOnlySpan<byte> plaintext, string purpose);

    byte[] Decrypt(ReadOnlySpan<byte> protectedBytes, string purpose);
}

/// <summary>
/// AES-256-GCM. Layout is <c>nonce(12) ‖ tag(16) ‖ ciphertext</c>, all in one column.
/// </summary>
public sealed class AesGcmDataEncryptor : IDataEncryptor
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmDataEncryptor(byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("A 256-bit (32-byte) key is required.", nameof(key));

        _key = key;
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, string purpose)
    {
        var output = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = output.AsSpan(0, NonceSize);
        var tag = output.AsSpan(NonceSize, TagSize);
        var ciphertext = output.AsSpan(NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(purpose));

        return output;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> protectedBytes, string purpose)
    {
        if (protectedBytes.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext is too short to be well-formed.");

        var nonce = protectedBytes[..NonceSize];
        var tag = protectedBytes.Slice(NonceSize, TagSize);
        var ciphertext = protectedBytes[(NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(purpose));

        return plaintext;
    }

    /// <summary>Purpose string for signing-key private material.</summary>
    public const string SigningKeyPurpose = "bas.signing-key.v1";
}
