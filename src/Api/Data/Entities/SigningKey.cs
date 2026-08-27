namespace Bas.Api.Data.Entities;

/// <summary>
/// The asymmetric key pair used to sign access tokens. Asymmetric rather than a shared secret so
/// the private half never has to be given to anyone — including partners, who do not need it.
///
/// <para>Persisted rather than generated per process so a redeploy does not invalidate every token
/// in flight. Rotation is manual: insert a newer row, or delete this one and restart. Older keys
/// stay trusted for verification, so a rotation never strands a token that was legitimately
/// issued minutes earlier.</para>
/// </summary>
public sealed class SigningKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>JWK <c>kid</c>, written into the JWT header so a verifier can pick the right key.</summary>
    public required string Kid { get; set; }

    /// <summary>JWS algorithm, e.g. <c>RS256</c>.</summary>
    public required string Algorithm { get; set; }

    /// <summary>
    /// The private key, PKCS#8, encrypted with AES-GCM before it ever reaches the database — a
    /// database backup should not be enough to mint tokens.
    /// </summary>
    public required byte[] PrivateKeyProtected { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
