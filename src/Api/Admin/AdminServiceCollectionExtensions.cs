using Bas.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bas.Api.Admin;

/// <summary>Registration for the admin surface: who may reach it, and how.</summary>
public static class AdminServiceCollectionExtensions
{
    public static IHostApplicationBuilder AddAdminSurface(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<AdminOptions>()
            .Bind(builder.Configuration.GetSection(AdminOptions.SectionName));

        builder.Services
            .AddIdentityCore<AdminUser>(options =>
            {
                options.User.RequireUniqueEmail = true;

                // A handful of accounts that each guard a surface able to suspend a partner and
                // display taxpayer data. Length does more than character classes, so the floor is
                // long rather than fussy.
                options.Password.RequiredLength = 12;
                options.Password.RequireNonAlphanumeric = false;

                // Ten minutes of lockout after five wrong guesses. Enough to make online guessing
                // pointless, short enough that locking yourself out is an inconvenience rather than
                // a call to whoever holds the database.
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);

                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<BasDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        // Cookies for the browser. The partner API stays on bearer tokens: JwtBearer remains the
        // DEFAULT scheme, so nothing here changes how /api/v1 authenticates.
        builder.Services.AddAuthentication()
            .AddCookie(IdentityConstants.ApplicationScheme, options =>
            {
                options.Cookie.Name = "bas-admin";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

                options.LoginPath = "/admin/login";
                options.LogoutPath = "/admin/logout";
                options.AccessDeniedPath = "/admin/login";

                // Short for an admin console. Sliding, so working through a backlog does not end
                // with being signed out mid-change.
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
            })
            .AddCookie(IdentityConstants.TwoFactorUserIdScheme, options =>
            {
                // Registered but unused: two-factor is not enforced. It is left here so that
                // enabling it on an account later is a toggle rather than a runtime failure -
                // SignInManager reaches for this scheme the moment a user has it switched on.
                options.Cookie.Name = "bas-admin-2fa";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            })
            .AddScheme<AuthenticationSchemeOptions, AdminAuthenticationHandler>(
                AdminAuthenticationHandler.SchemeName, _ => { });

        builder.Services.AddScoped<IAdminActor, AdminActor>();
        builder.Services.AddScoped<AdminService>();
        builder.Services.AddScoped<AdminIdentitySeeder>();

        return builder;
    }
}

/// <summary>
/// Creates the accounts named in configuration, if they do not exist yet.
///
/// <para>Bootstrap only — it never updates an existing account, so changing a password here does
/// nothing and rotating one properly means doing it through the UI. The initial password is
/// expected to be replaced on first sign-in.</para>
/// </summary>
public sealed class AdminIdentitySeeder(
    UserManager<AdminUser> users,
    IOptions<AdminOptions> options,
    TimeProvider timeProvider,
    ILogger<AdminIdentitySeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value.Users.Where(u => !string.IsNullOrWhiteSpace(u.Email)).ToList();

        if (configured.Count == 0)
        {
            logger.LogWarning(
                "No admin accounts are configured. Nobody can sign in to the admin surface until " +
                "one is added under {Section}:Users.", AdminOptions.SectionName);
            return;
        }

        foreach (var seed in configured)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await users.FindByEmailAsync(seed.Email) is not null)
                continue;

            if (string.IsNullOrWhiteSpace(seed.InitialPassword))
            {
                logger.LogError(
                    "Admin account {Email} has no initial password configured and was not created.", seed.Email);
                continue;
            }

            var user = new AdminUser
            {
                UserName = seed.Email,
                Email = seed.Email,
                EmailConfirmed = true,
                DisplayName = string.IsNullOrWhiteSpace(seed.DisplayName) ? seed.Email : seed.DisplayName,
                CreatedAt = timeProvider.GetUtcNow()
            };

            var result = await users.CreateAsync(user, seed.InitialPassword);

            if (result.Succeeded)
            {
                logger.LogWarning(
                    "Created admin account {Email} from configuration. It is still using the configured " +
                    "initial password — sign in and change it now.", seed.Email);
            }
            else
            {
                logger.LogError(
                    "Could not create admin account {Email}: {Errors}",
                    seed.Email, string.Join("; ", result.Errors.Select(e => e.Description)));
            }
        }
    }
}
