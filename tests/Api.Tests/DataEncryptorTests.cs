using System.Security.Cryptography;
using System.Text;
using Bas.Api.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bas.Api.Tests;

/// <summary>
/// Covers the at-rest encryption that protects signing-key private material today, and worker TFNs
/// once phase 3b lands. Getting this wrong is quiet — the service keeps working either way — so the
/// properties are worth pinning down explicitly.
/// </summary>
public sealed class DataEncryptorTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Round_trips_a_value()
    {
        var encryptor = new AesGcmDataEncryptor(Key);
        var plaintext = Encoding.UTF8.GetBytes("123 456 789");

        var protectedBytes = encryptor.Encrypt(plaintext, "tfn");

        encryptor.Decrypt(protectedBytes, "tfn").ShouldBe(plaintext);
    }

    [Fact]
    public void Ciphertext_does_not_contain_the_plaintext()
    {
        var encryptor = new AesGcmDataEncryptor(Key);
        var plaintext = Encoding.UTF8.GetBytes("123456789");

        var protectedBytes = encryptor.Encrypt(plaintext, "tfn");

        Convert.ToHexString(protectedBytes).ShouldNotContain(Convert.ToHexString(plaintext));
    }

    [Fact]
    public void Encrypting_the_same_value_twice_produces_different_ciphertext()
    {
        // A fresh nonce each time. Without it, equal values would be visibly equal in the database
        // — which for a column of TFNs would leak that two workers share one.
        var encryptor = new AesGcmDataEncryptor(Key);
        var plaintext = Encoding.UTF8.GetBytes("123456789");

        encryptor.Encrypt(plaintext, "tfn").ShouldNotBe(encryptor.Encrypt(plaintext, "tfn"));
    }

    [Fact]
    public void Decrypting_under_a_different_purpose_fails()
    {
        // The purpose is bound in as associated data, so a blob lifted from one column cannot be
        // replayed into another that happens to share the key.
        var encryptor = new AesGcmDataEncryptor(Key);
        var protectedBytes = encryptor.Encrypt(Encoding.UTF8.GetBytes("secret"), "signing-key");

        Should.Throw<CryptographicException>(() => encryptor.Decrypt(protectedBytes, "tfn"));
    }

    [Fact]
    public void Tampered_ciphertext_is_rejected_rather_than_silently_decrypted()
    {
        var encryptor = new AesGcmDataEncryptor(Key);
        var protectedBytes = encryptor.Encrypt(Encoding.UTF8.GetBytes("secret"), "tfn");

        protectedBytes[^1] ^= 0xFF;

        Should.Throw<CryptographicException>(() => encryptor.Decrypt(protectedBytes, "tfn"));
    }

    [Fact]
    public void Decrypting_with_a_different_key_fails()
    {
        var protectedBytes = new AesGcmDataEncryptor(Key).Encrypt(Encoding.UTF8.GetBytes("secret"), "tfn");
        var other = new AesGcmDataEncryptor(RandomNumberGenerator.GetBytes(32));

        Should.Throw<CryptographicException>(() => other.Decrypt(protectedBytes, "tfn"));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(64)]
    public void A_key_of_the_wrong_size_is_refused(int size)
    {
        Should.Throw<ArgumentException>(() => new AesGcmDataEncryptor(new byte[size]));
    }

    // ------------------------------------------------------------------ key resolution

    [Fact]
    public void Missing_key_outside_development_fails_fast()
    {
        // Generating one silently would produce a service that starts cleanly, mints tokens all
        // day, and then cannot decrypt its own signing key after the next restart. A failed deploy
        // is the kinder outcome.
        Should.Throw<InvalidOperationException>(() =>
            DataEncryptionKey.Resolve(Configuration(null), Environment(Environments.Production)));
    }

    [Fact]
    public void Missing_key_in_development_falls_back_to_a_deterministic_one()
    {
        var first = DataEncryptionKey.Resolve(Configuration(null), Environment(Environments.Development));
        var second = DataEncryptionKey.Resolve(Configuration(null), Environment(Environments.Development));

        var protectedBytes = first.Encrypt(Encoding.UTF8.GetBytes("secret"), "tfn");

        // Deterministic, so a developer's local signing keys survive a restart.
        Encoding.UTF8.GetString(second.Decrypt(protectedBytes, "tfn")).ShouldBe("secret");
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("c2hvcnQ=")]
    public void A_malformed_configured_key_fails_fast(string configured)
    {
        Should.Throw<InvalidOperationException>(() =>
            DataEncryptionKey.Resolve(Configuration(configured), Environment(Environments.Production)));
    }

    private static IConfiguration Configuration(string? key) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DataEncryptionKey.ConfigurationKey] = key })
            .Build();

    private static IHostEnvironment Environment(string name) => new StubEnvironment { EnvironmentName = name };

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Bas.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
