using Bas.Api.Auth;
using Bas.Api.Bas;
using Bas.Api.Data;
using Bas.Api.Endpoints;
using Bas.Api.Infrastructure;
using Bas.Api.Security;
using Bas.Api.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

builder.Services.AddSingleton<IDataEncryptor>(serviceProvider => DataEncryptionKey.Resolve(
    serviceProvider.GetRequiredService<IConfiguration>(),
    serviceProvider.GetRequiredService<IHostEnvironment>()));

builder.AddPartnerAuthentication();

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
app.UseAuthorization();

app.MapPartnerAuthEndpoints();
app.MapWorkerEndpoints();
app.MapBasEndpoints();

// The document the MyGigsters team generates their Next.js and Flutter clients from.
app.MapOpenApi().AllowAnonymous();

app.MapDefaultEndpoints();

app.Run();

/// <summary>Exposed so the integration tests can drive the real pipeline through WebApplicationFactory.</summary>
public partial class Program;
