using System.Security.Cryptography;
using Bas.Api.Auth;
using Bas.Api.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace Bas.Api.Tests;

/// <summary>
/// Covers what the service accepts as a partner's registered key. Partners will send whatever their
/// platform's tooling produces, so both common families have to work — and anything else has to
/// fail as a refusal rather than an unhandled exception on the token path.
/// </summary>
public sealed class PartnerKeyStoreTests
{
    [Fact]
    public void Accepts_an_RSA_public_key()
    {
        using var rsa = RSA.Create(2048);

        var key = Store().GetKey(PartnerWith(rsa.ExportSubjectPublicKeyInfoPem()));

        key.ShouldBeOfType<RsaSecurityKey>();
    }

    [Fact]
    public void Accepts_an_ECDSA_public_key()
    {
        // Node's jose and Go's crypto both reach for P-256 by default, so a partner arriving with
        // an EC key is the likely case rather than the exotic one.
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var key = Store().GetKey(PartnerWith(ecdsa.ExportSubjectPublicKeyInfoPem()));

        key.ShouldBeOfType<ECDsaSecurityKey>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a pem at all")]
    [InlineData("-----BEGIN PUBLIC KEY-----\nbm90IGEga2V5\n-----END PUBLIC KEY-----")]
    public void Refuses_anything_that_is_not_a_public_key(string pem)
    {
        // Null rather than an exception: a partner registered with a typo should be refused at the
        // token endpoint, not turn every request into a 500.
        Store().GetKey(PartnerWith(pem)).ShouldBeNull();
    }

    [Fact]
    public void A_private_key_in_the_registration_is_not_treated_as_a_public_one()
    {
        // If a partner pastes the wrong half, that is a serious mistake on their side and we must
        // not quietly carry on. ImportFromPem would accept a PRIVATE KEY block here, which is why
        // the store rejects one explicitly rather than relying on the import to fail.
        using var rsa = RSA.Create(2048);

        Store().GetKey(PartnerWith(rsa.ExportPkcs8PrivateKeyPem())).ShouldBeNull();
    }

    [Fact]
    public void Updating_the_registered_key_takes_effect_without_a_restart()
    {
        var store = Store();
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);

        var partner = PartnerWith(first.ExportSubjectPublicKeyInfoPem());
        var before = store.GetKey(partner);

        partner.PublicKeyPem = second.ExportSubjectPublicKeyInfoPem();
        var after = store.GetKey(partner);

        after.ShouldNotBeNull();
        // The cache is keyed on the material, not just the client id, so a rotation deployed into
        // configuration is picked up on the next request.
        ((RsaSecurityKey)after).Rsa.ExportSubjectPublicKeyInfoPem()
            .ShouldNotBe(((RsaSecurityKey)before!).Rsa.ExportSubjectPublicKeyInfoPem());
    }

    private static PartnerKeyStore Store() => new(NullLogger<PartnerKeyStore>.Instance);

    private static Partner PartnerWith(string pem) => new()
    {
        ClientId = "mygigsters",
        Name = "MyGigsters",
        PublicKeyPem = pem,
        AllowedScopes = "bas:read"
    };
}
