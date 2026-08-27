using System.Security.Cryptography;
using System.Text;

namespace Bas.Api.Security;

/// <summary>
/// Resolves the key that protects at-rest secrets — signing-key private material now, worker TFNs
/// when phase 3b lands.
/// </summary>
public static class DataEncryptionKey
{
    /// <summary>Configuration path for the base64-encoded 256-bit key.</summary>
    public const string ConfigurationKey = "Security:DataEncryptionKey";

    /// <summary>
    /// Returns the configured encryptor.
    ///
    /// <para>Outside Development a missing key is fatal, and deliberately so. The alternative —
    /// quietly generating one — produces a service that starts cleanly, mints tokens for a day,
    /// and then cannot decrypt its own signing keys after the next restart. Failing the deploy is
    /// the kinder outcome.</para>
    /// </summary>
    public static IDataEncryptor Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration[ConfigurationKey];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(configured);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"{ConfigurationKey} must be base64-encoded.", ex);
            }

            if (key.Length != 32)
                throw new InvalidOperationException(
                    $"{ConfigurationKey} must decode to 32 bytes (256 bits); got {key.Length}.");

            return new AesGcmDataEncryptor(key);
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} is required outside Development. Generate one with: " +
                "openssl rand -base64 32");
        }

        // Deterministic, so a developer's local signing keys survive a restart, and obviously
        // worthless to anyone reading the source — which is the point.
        return new AesGcmDataEncryptor(
            SHA256.HashData(Encoding.UTF8.GetBytes("bas.api.development-only-data-encryption-key")));
    }
}
