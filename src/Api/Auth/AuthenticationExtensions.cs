using System.Security.Claims;
using System.Threading.RateLimiting;
using Bas.Api.Contracts.Partner;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bas.Api.Auth;

/// <summary>Registration for everything under <c>Auth/</c>.</summary>
public static class AuthenticationExtensions
{
    public static IHostApplicationBuilder AddPartnerAuthentication(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<PartnerAuthOptions>()
            .Bind(builder.Configuration.GetSection(PartnerAuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddOptions<SigningKeyOptions>()
            .Bind(builder.Configuration.GetSection(SigningKeyOptions.SectionName))
            .ValidateOnStart();

        builder.Services.AddMemoryCache();

        builder.Services.AddSingleton<ISigningKeyStore, SigningKeyStore>();
        builder.Services.AddSingleton<IPartnerKeyStore, PartnerKeyStore>();
        builder.Services.AddSingleton<IAssertionReplayGuard, MemoryAssertionReplayGuard>();
        builder.Services.AddSingleton<AccessTokenIssuer>();

        // Scoped: these reach the database through the request's DbContext.
        builder.Services.AddScoped<PartnerAssertionValidator>();
        builder.Services.AddScoped<WorkerProvisioner>();
        builder.Services.AddScoped<PartnerTokenExchangeService>();

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICallerContext, CallerContext>();

        AddBearerAuthentication(builder);
        AddTokenEndpointRateLimiting(builder);

        return builder;
    }

    private static void AddBearerAuthentication(IHostApplicationBuilder builder)
    {
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Claims arrive as written. Without this, ASP.NET rewrites `sub` into a long
                // WS-Federation URI and lookups start disagreeing with what the token said.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,

                    // We sign these ourselves, so exactly one algorithm is ever legitimate.
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],

                    NameClaimType = JwtRegisteredClaimNames.Sub
                };
            });

        // Issuer, audience and the key resolver are filled in here rather than in the lambda above,
        // because they need services. The resolver closes over the singleton key store and reads
        // its cached snapshot, so authentication never makes a database round trip.
        builder.Services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, ConfigureBearerFromKeyStore>();

        builder.Services.AddSingleton<IAuthorizationHandler, ScopeAuthorizationHandler>();

        builder.Services.AddAuthorizationBuilder()
            .AddScopePolicies()
            // Nothing partner-facing is anonymous by accident: an endpoint that forgets to state a
            // policy still requires an authenticated caller, and the token endpoint opts out
            // explicitly with AllowAnonymous.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());
    }

    private sealed class ConfigureBearerFromKeyStore(
        ISigningKeyStore keyStore, IOptions<PartnerAuthOptions> authOptions, TimeProvider timeProvider)
        : IPostConfigureOptions<JwtBearerOptions>
    {
        public void PostConfigure(string? name, JwtBearerOptions options)
        {
            if (name != JwtBearerDefaults.AuthenticationScheme)
                return;

            var skew = authOptions.Value.ClockSkew;
            var parameters = options.TokenValidationParameters;

            parameters.ValidIssuer = authOptions.Value.Issuer;
            parameters.ValidAudience = authOptions.Value.Audience;
            parameters.ClockSkew = skew;
            parameters.IssuerSigningKeyResolver = (_, _, _, _) => keyStore.CurrentValidationKeys;

            // Same reason as on the assertion path: one clock decides what "now" means here, and
            // it is the one this service was given rather than DateTime.UtcNow.
            parameters.LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;

                if (notBefore.HasValue && now + skew < notBefore.Value)
                    return false;

                return !expires.HasValue || now - skew <= expires.Value;
            };
        }
    }

    private static void AddTokenEndpointRateLimiting(IHostApplicationBuilder builder)
    {
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(PartnerAuthEndpoints.TokenRateLimitPolicy, context =>
            {
                // Signature verification is the expensive part of this endpoint and it happens
                // before we know whether the caller is genuine — so the limit has to bind to
                // something available before validation. Partition on the claimed client_id where
                // there is one, and on the remote address otherwise.
                var partition = TryReadClientIdHint(context)
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
            });
        });
    }

    /// <summary>
    /// Reads the unverified <c>iss</c> out of the posted assertion, purely to partition the rate
    /// limiter. It is a hint and treated as one — a caller can lie, which at worst puts them in
    /// someone else's bucket, and the address fallback covers them omitting it.
    /// </summary>
    private static string? TryReadClientIdHint(HttpContext context)
    {
        if (!context.Request.HasFormContentType)
            return null;

        // Safe to read here: the framework buffers and caches the form, so the endpoint's own
        // ReadFormAsync gets the same instance rather than an already-consumed stream.
        if (!context.Request.Form.TryGetValue(TokenExchange.Fields.ClientAssertion, out var assertion))
            return null;

        try
        {
            var issuer = new JsonWebToken(assertion.ToString()).Issuer;
            return string.IsNullOrEmpty(issuer) ? null : $"client:{issuer}";
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>The authenticated caller, as the rest of the service should see them.</summary>
public interface ICallerContext
{
    /// <summary>The <see cref="Data.Entities.Worker"/> this token was minted for.</summary>
    Guid WorkerId { get; }

    /// <summary>The partner whose assertion produced this token. Stamped on logs for audit.</summary>
    string PartnerId { get; }

    /// <summary>Token id, for correlating a request back to the mint that authorised it.</summary>
    string TokenId { get; }
}

/// <inheritdoc />
public sealed class CallerContext(IHttpContextAccessor accessor) : ICallerContext
{
    public Guid WorkerId =>
        Guid.TryParse(Claim(JwtRegisteredClaimNames.Sub), out var id)
            ? id
            : throw new InvalidOperationException("The access token carries no usable subject.");

    public string PartnerId => Claim(BasClaims.PartnerId) ?? string.Empty;

    public string TokenId => Claim(JwtRegisteredClaimNames.Jti) ?? string.Empty;

    private string? Claim(string type) =>
        (accessor.HttpContext?.User.Identity as ClaimsIdentity)?.FindFirst(type)?.Value;
}
