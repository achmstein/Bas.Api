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
    /// The private key, PKCS#8 PEM.
    ///
    /// <para>Anyone holding this can mint an access token for any worker, so a database dump is a
    /// credential. It is regenerable, though: delete the row and a new key is created on the next
    /// start, which invalidates only the tokens issued in the preceding ten minutes.</para>
    /// </summary>
    public required string PrivateKeyPem { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
