using System.Security.Cryptography;
using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Bas.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Bas.Api.Auth;

/// <summary>The key currently signing access tokens.</summary>
public sealed record ActiveSigningKey(string Kid, SigningCredentials Credentials);

/// <summary>
/// Owns the key pair that signs access tokens: the one we sign with, and every key still trusted
/// for verification.
/// </summary>
public interface ISigningKeyStore
{
    /// <summary>Loads existing keys, creating the first one if the table is empty.</summary>
    Task EnsureCurrentAsync(CancellationToken cancellationToken);

    /// <summary>Credentials for signing a new token.</summary>
    Task<ActiveSigningKey> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Keys the bearer middleware validates against. Synchronous because
    /// <see cref="TokenValidationParameters.IssuerSigningKeyResolver"/> is, and because it runs on
    /// every authenticated request — it reads a snapshot rather than touching the database.
    /// </summary>
    IReadOnlyList<SecurityKey> CurrentValidationKeys { get; }
}

/// <summary>
/// One RSA key pair, persisted with its private half encrypted.
///
/// <para>Persisted rather than generated per process for one practical reason: a redeploy would
/// otherwise invalidate every token in flight at once, and every worker mid-form would take a 401.
/// Tokens live ten minutes, so the alternative is a small burst of avoidable failures on each
/// deploy.</para>
///
/// <para>Rotation is manual and expected to be rare — delete the row and restart, or insert a
/// newer one. Every key in the table stays trusted for verification, so replacing the signing key
/// does not invalidate tokens already issued under the old one. There is no automatic schedule
/// here: a key rotating itself unattended is machinery worth having when many parties verify these
/// tokens, and this service is not that.</para>
/// </summary>
public sealed class SigningKeyStore(
    IServiceScopeFactory scopeFactory,
    IDataEncryptor encryptor,
    IOptions<SigningKeyOptions> options,
    TimeProvider timeProvider,
    ILogger<SigningKeyStore> logger) : ISigningKeyStore, IDisposable
{
    private readonly SigningKeyOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Snapshot _snapshot = Snapshot.Empty;

    public IReadOnlyList<SecurityKey> CurrentValidationKeys => _snapshot.ValidationKeys;

    public async Task EnsureCurrentAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();

            var keys = await LoadAsync(db, cancellationToken);

            if (keys.Count == 0)
            {
                await CreateKeyAsync(db, cancellationToken);
                keys = await LoadAsync(db, cancellationToken);
            }

            _snapshot = BuildSnapshot(keys);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ActiveSigningKey> GetActiveAsync(CancellationToken cancellationToken)
    {
        if (_snapshot.Active is not null)
            return _snapshot.Active;

        await EnsureCurrentAsync(cancellationToken);

        return _snapshot.Active
            ?? throw new InvalidOperationException("No signing key is available to mint access tokens.");
    }

    private static async Task<List<SigningKey>> LoadAsync(
        BasDbContext db, CancellationToken cancellationToken) =>
        await db.SigningKeys
            .AsNoTracking()
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);

    private async Task CreateKeyAsync(BasDbContext db, CancellationToken cancellationToken)
    {
        using var rsa = RSA.Create(_options.KeySizeBits);

        // Thumbprint of the public key rather than a random id, so the same key always presents
        // the same kid.
        var kid = Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16];

        db.SigningKeys.Add(new SigningKey
        {
            Kid = kid,
            Algorithm = SecurityAlgorithms.RsaSha256,
            PrivateKeyProtected = encryptor.Encrypt(
                rsa.ExportPkcs8PrivateKey(), AesGcmDataEncryptor.SigningKeyPurpose),
            CreatedAt = timeProvider.GetUtcNow()
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Signing key {Kid} created.", kid);
        }
        catch (DbUpdateException)
        {
            // Another instance got there first. Its key is as good as ours — the caller re-reads.
            db.ChangeTracker.Clear();
            logger.LogInformation("Signing key creation lost a race with another instance; using the winner.");
        }
    }

    private Snapshot BuildSnapshot(List<SigningKey> keys)
    {
        var validationKeys = new List<SecurityKey>(keys.Count);
        ActiveSigningKey? active = null;

        foreach (var key in keys)
        {
            // The RSA instance outlives this method by design — RsaSecurityKey holds it for as
            // long as the snapshot is live, and a superseded snapshot is collected with its keys.
            var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(
                encryptor.Decrypt(key.PrivateKeyProtected, AesGcmDataEncryptor.SigningKeyPurpose), out _);

            var securityKey = new RsaSecurityKey(rsa) { KeyId = key.Kid };
            validationKeys.Add(securityKey);

            // `keys` is newest-first, so the newest signs and every older one still verifies.
            active ??= new ActiveSigningKey(
                key.Kid, new SigningCredentials(securityKey, key.Algorithm));
        }

        return new Snapshot(active, validationKeys);
    }

    public void Dispose() => _gate.Dispose();

    private sealed record Snapshot(ActiveSigningKey? Active, IReadOnlyList<SecurityKey> ValidationKeys)
    {
        public static readonly Snapshot Empty = new(null, []);
    }
}
