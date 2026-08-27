using Bas.Api.Admin;
using Bas.Api.Sync;
using Bas.Api.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Bas.Api.Tests;

/// <summary>
/// A user entry with an email but no password used to seed nothing and log an error nobody reads.
/// The validator turns that misconfiguration into a failed deploy that names the variable to set.
/// </summary>
public sealed class AdminOptionsValidatorTests
{
    private static readonly AdminOptionsValidator Validator = new();

    [Fact]
    public void A_user_with_an_email_but_no_password_fails_naming_the_variable()
    {
        var options = new AdminOptions
        {
            Users = [new AdminUserSeed { Email = "david@nighttax.com.au" }]
        };

        var result = Validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("david@nighttax.com.au");
        result.FailureMessage.ShouldContain("Admin__Users__0__InitialPassword");
    }

    [Fact]
    public void A_blank_email_entry_is_a_placeholder_and_passes()
    {
        // The test host disables seeding with exactly this shape.
        var options = new AdminOptions
        {
            Users = [new AdminUserSeed { Email = "" }]
        };

        Validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void No_users_at_all_passes()
    {
        Validator.Validate(null, new AdminOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_complete_user_entry_passes()
    {
        var options = new AdminOptions
        {
            Users = [new AdminUserSeed { Email = "david@nighttax.com.au", InitialPassword = "long-enough-password" }]
        };

        Validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }
}

/// <summary>
/// The worker options fail the deploy rather than binding nonsense — a zero poll interval or an
/// empty-entry schedule would otherwise turn a sweep loop into a hot spin at runtime.
/// Driven through the real AddSync()/AddWebhooks() registrations, not a re-declared copy.
/// </summary>
public sealed class WorkerOptionsValidationTests
{
    [Theory]
    [InlineData("Reconciler:PollInterval", "00:00:00")]
    [InlineData("Reconciler:BatchSize", "0")]
    [InlineData("Reconciler:RetrySchedule:0", "00:00:00")]
    public void Bad_reconciler_configuration_is_refused(string key, string value)
    {
        using var host = BuildHost(key, value);

        Should.Throw<OptionsValidationException>(
            () => host.Services.GetRequiredService<IOptions<ReconcilerOptions>>().Value);
    }

    [Theory]
    [InlineData("Webhooks:PollInterval", "00:00:00")]
    [InlineData("Webhooks:MaxAttempts", "0")]
    [InlineData("Webhooks:Retention", "00:00:00")]
    public void Bad_webhook_configuration_is_refused(string key, string value)
    {
        using var host = BuildHost(key, value);

        Should.Throw<OptionsValidationException>(
            () => host.Services.GetRequiredService<IOptions<WebhookOptions>>().Value);
    }

    [Fact]
    public void The_defaults_pass()
    {
        using var host = BuildHost(null, null);

        host.Services.GetRequiredService<IOptions<ReconcilerOptions>>().Value.ShouldNotBeNull();
        host.Services.GetRequiredService<IOptions<WebhookOptions>>().Value.ShouldNotBeNull();
    }

    private static IHost BuildHost(string? key, string? value)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        var settings = new Dictionary<string, string?>
        {
            ["PracticeManager:Endpoint"] = "http://practicemanager.invalid:8081"
        };
        if (key is not null)
            settings[key] = value;

        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddSync();
        builder.AddWebhooks();

        return builder.Build();
    }
}
