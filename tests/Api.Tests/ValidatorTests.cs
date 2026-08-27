using Bas.Api.Bas;
using Shouldly;

namespace Bas.Api.Tests;

/// <summary>
/// The ATO checksums. These run before anything reaches Practice Manager, because PM creates a
/// client in two calls and only the second validates the TFN — so a bad number leaves a
/// fully-created client behind, and a retrying reconciler orphans another one on every attempt.
/// </summary>
public sealed class TfnValidatorTests
{
    [Theory]
    // Published ATO test numbers - structurally valid, issued to nobody.
    [InlineData("123456782")]
    [InlineData("123 456 782")]
    [InlineData("876543210")]
    public void Accepts_a_well_formed_TFN(string tfn)
    {
        TfnValidator.IsValid(tfn, out var reason).ShouldBeTrue(reason);
    }

    [Theory]
    [InlineData("", "no TFN")]
    [InlineData("12345678", "9 digits")]
    [InlineData("1234567890", "9 digits")]
    [InlineData("123456789", "checksum")]
    public void Refuses_a_malformed_TFN(string tfn, string expectedInReason)
    {
        TfnValidator.IsValid(tfn, out var reason).ShouldBeFalse();
        reason.ShouldContain(expectedInReason);
    }

    [Fact]
    public void The_refusal_reason_never_contains_the_TFN()
    {
        // It ends up in logs and in an API response, so it must not carry the number itself.
        TfnValidator.IsValid("123456789", out var reason);

        reason.ShouldNotContain("123456789");
        reason.ShouldNotContain("123");
    }

    [Theory]
    [InlineData("123456782", "******782")]
    [InlineData("12", "**")]
    public void Masks_everything_but_the_last_three_digits(string tfn, string expected)
    {
        TfnValidator.Mask(tfn).ShouldBe(expected);
    }
}

public sealed class AbnValidatorTests
{
    [Theory]
    // The ATO's own ABN, and a published test value.
    [InlineData("51824753556")]
    [InlineData("53 004 085 616")]
    public void Accepts_a_well_formed_ABN(string abn)
    {
        AbnValidator.IsValid(abn, out var reason).ShouldBeTrue(reason);
    }

    [Theory]
    [InlineData("", "no ABN")]
    [InlineData("5182475355", "11 digits")]
    [InlineData("51824753557", "checksum")]
    public void Refuses_a_malformed_ABN(string abn, string expectedInReason)
    {
        AbnValidator.IsValid(abn, out var reason).ShouldBeFalse();
        reason.ShouldContain(expectedInReason);
    }
}
