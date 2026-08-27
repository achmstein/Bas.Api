using Bas.Api.Admin;
using Microsoft.AspNetCore.DataProtection;
using Bas.Api.Auth;
using Bas.Api.Statements;
using Bas.Api.Data;
using Bas.Api.Components;
using Bas.Api.Statements;
using Bas.Api.Infrastructure;
using Bas.Api.Sync;
using Bas.Api.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton(TimeProvider.System);

// Postgres, supplied by Aspire in every deployed environment.
//
// Resolved from the service provider rather than read here, because top-level statements run to
// completion before the host is built: anything read at this point is fixed before a test host or
// any other late configuration source gets a chance to contribute.
builder.Services.AddDbContext<BasDbContext>((serviceProvider, options) =>
{
    var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("basdb")
        ?? throw new InvalidOperationException(
            "No 'basdb' connection string. Run through the AppHost, or set ConnectionStrings__basdb.");

    options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure());
});

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName));

builder.Services.AddOptions<PartnerRegistrationOptions>()
    .Bind(builder.Configuration.GetSection(PartnerRegistrationOptions.SectionName))
    .ValidateOnStart();

// The Data Protection key ring, in Postgres. It protects antiforgery tokens and the admin auth
// cookie, and the default on-disk location is inside a container with no volume - so every deploy
// invalidated both. A fixed application name keeps the ring stable across image rebuilds.
builder.Services.AddDataProtection()
    .SetApplicationName("bas-api")
    .PersistKeysToDbContext<BasDbContext>();

builder.AddPartnerAuthentication();
builder.AddAdminSurface();

// Static server rendering only. An admin console is forms and tables; a SignalR circuit per
// operator would buy nothing and would put a reconnect banner between David and a kill switch.
builder.Services.AddRazorComponents();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddScoped<WorkerIdentityService>();
builder.Services.AddScoped<BasPeriodService>();

// The push into Practice Manager, and the ledger that owns retrying it.
builder.Services.AddOptions<PracticeManagerOptions>()
    .Bind(builder.Configuration.GetSection(PracticeManagerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ReconcilerOptions>()
    .Bind(builder.Configuration.GetSection(ReconcilerOptions.SectionName));

builder.Services
    .AddGrpcClient<PracticeManager.Api.Contracts.PracticeManagerApi.PracticeManagerApiClient>((sp, o) =>
        o.Address = new Uri(sp.GetRequiredService<IOptions<PracticeManagerOptions>>().Value.Endpoint));

builder.Services.AddScoped<IPracticeManagerGateway, PracticeManagerGateway>();
builder.Services.AddHostedService<BasReconciler>();

// Outbound status webhooks. Optional for a partner - polling the status route works whether or not
// they register a URL - so nothing here is on the critical path of a lodgement.
builder.Services.AddOptions<WebhookOptions>()
    .Bind(builder.Configuration.GetSection(WebhookOptions.SectionName));

builder.Services.AddHttpClient(WebhookDispatcher.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // A redirect would deliver a signed payload to an address the partner did not register.
        AllowAutoRedirect = false
    });

builder.Services.AddScoped<WebhookPublisher>();
builder.Services.AddHostedService<WebhookDispatcher>();

builder.Services.AddHostedService<DatabaseStartupService>();

// CORS matters here in a way it did not for PracticeManager.Api: our JS runs on the partner's
// origin and calls this API cross-origin. Credentials are deliberately NOT allowed — the whole
// point of bearer tokens is that no ambient cookie travels, and AllowCredentials would reopen the
// CSRF surface the design removed.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .WithHeaders("Authorization", "Content-Type")
        .WithMethods("GET", "POST", "PUT", "DELETE")
        .SetPreflightMaxAge(TimeSpan.FromHours(1)));
});

builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer<PartnerApiDocument>());

// A separate document: a partner generating a client from /openapi/v1.json should not end up with
// a suspendPartner() in their SDK.
builder.Services.AddOpenApi(AdminEndpoints.DocumentName);

builder.Services.AddProblemDetails();

// DataAnnotations on request records are not enforced without this, so [Range(0, int.MaxValue)] on
// a money field would sit there looking reassuring while a negative amount saved happily.
builder.Services.AddValidation();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();

// After authentication, so there is a principal to read; before authorization, so a 403 is stamped
// with the partner it was refused for.
app.UsePartnerRequestAudit();

app.UseAuthorization();
// Before UseAntiforgery: it works by catching what that middleware throws.
app.UseAntiforgeryRecovery();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();

app.MapPartnerAuthEndpoints();
app.MapWorkerEndpoints();
app.MapBasEndpoints();
app.MapAdminEndpoints();
app.MapAdminUi();

// The partner document, and browsable reference docs over it. Both anonymous: this is the
// specification a partner integrates against, and it describes nothing that is not already public
// once they hold a client id.
app.MapOpenApi("/openapi/{documentName}.json")
    .AllowAnonymous()
    // The admin document is a separate matter - it is a map of the operations surface, and while
    // every route on it is protected, publishing the map serves nobody outside the practice.
    .AddEndpointFilter(async (context, next) =>
    {
        var documentName = context.HttpContext.Request.RouteValues["documentName"] as string;

        if (documentName == AdminEndpoints.DocumentName
            && context.HttpContext.User.Identity?.IsAuthenticated is not true)
        {
            return Results.NotFound();
        }

        return await next(context);
    });

app.MapScalarApiReference("/docs", options => options
    .WithTitle("Bas.Api")
    .AddDocument("v1", "Partner API", "/openapi/v1.json"))
    .AllowAnonymous();

app.MapDefaultEndpoints();

app.Run();

/// <summary>Exposed so the integration tests can drive the real pipeline through WebApplicationFactory.</summary>
public partial class Program;
