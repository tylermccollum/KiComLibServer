using KiComLibBackend.Services.Reviews;
using System.Net;
using System.Net.Http.Headers;
using KiComLibBackend.Controllers;
using KiComLibBackend.Data;
using KiComLibBackend.Filters;
using KiComLibBackend.Services;
using KiComLibBackend.Services.Distributors;
using dotenv.net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

// Load environment variables without overwriting values already set in the process environment
DotEnv.Load(new DotEnvOptions(overwriteExistingVars: false));

string connectionString = $"Host={Environment.GetEnvironmentVariable("DB_HOST")};Port={Environment.GetEnvironmentVariable("DB_PORT")};Database={Environment.GetEnvironmentVariable("DB_NAME")};User Id={Environment.GetEnvironmentVariable("DB_USER")};Password={Environment.GetEnvironmentVariable("DB_PASSWORD")}";

int blobMaxSizeMb = 200;
if (int.TryParse(Environment.GetEnvironmentVariable("BLOB_MAX_SIZE_MB"), out int parsedBlobMb) && parsedBlobMb > 0)
{
    blobMaxSizeMb = parsedBlobMb;
}

int backupMaxMb = 2048;
if (int.TryParse(Environment.GetEnvironmentVariable("BACKUP_MAX_MB"), out int parsedBackupMb) && parsedBackupMb > 0)
{
    backupMaxMb = parsedBackupMb;
}

TimeSpan startupTimeout = TimeSpan.FromSeconds(120);
if (int.TryParse(Environment.GetEnvironmentVariable("DB_STARTUP_TIMEOUT_SECONDS"), out int parsedStartupSeconds) && parsedStartupSeconds >= 0)
{
    startupTimeout = TimeSpan.FromSeconds(parsedStartupSeconds);
}

long maxRequestBytes = Math.Max(blobMaxSizeMb, backupMaxMb) * 1024L * 1024L;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxRequestBytes;
});

builder.Services.AddControllers(options =>
{
    options.Filters.Add(new CatalogConstraintExceptionFilter());
    options.Filters.Add(new ReservedReviewFields());
});
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddSingleton(new DatabaseInitializerOptions
{
    ConnectionString = connectionString,
    ScriptDirectory = Path.Combine(builder.Environment.ContentRootPath, "Data", "Sql"),
    MaintenanceDatabase = Environment.GetEnvironmentVariable("DB_MAINTENANCE_NAME") ?? "postgres",
    Locale = Environment.GetEnvironmentVariable("DB_LOCALE") ?? "en_US.UTF-8",
    Owner = Environment.GetEnvironmentVariable("DB_OWNER"),
    StartupTimeout = startupTimeout
});
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddScoped<IBlobStore, DbBlobStore>();
builder.Services.AddScoped<CommitService>();
builder.Services.AddScoped<KiCadFieldBuilder>();
builder.Services.AddScoped<ReviewDatabase>();
builder.Services.AddScoped<ReviewDefinitionBuilder>();
builder.Services.AddScoped<IReviewValidator, ReviewValidator>();
builder.Services.AddScoped<ReviewService>();
builder.Services.AddScoped<CatalogMatcher>();
builder.Services.AddScoped<BomService>();
builder.Services.AddScoped<ProjectAnalysisApplier>();
builder.Services.AddScoped<ProjectRematchScheduler>();
builder.Services.AddSingleton<ProjectCache>();
builder.Services.AddScoped<DiffService>();
builder.Services.AddScoped<CatalogRevisionService>();
builder.Services.AddScoped<RevertService>();
builder.Services.AddScoped<AppSettingsService>();
builder.Services.AddScoped<TrustedPartsClient>();
builder.Services.AddScoped<DistributorSettingsService>();
builder.Services.AddScoped<MouserClient>();
builder.Services.AddScoped<DigiKeyClient>();
builder.Services.AddScoped<FarnellClient>();
builder.Services.AddScoped<ArrowClient>();
builder.Services.AddScoped<TmeClient>();
builder.Services.AddScoped<DistributorSearchService>();
builder.Services.AddScoped<DistributorDatasheetImporter>();
builder.Services.AddScoped<ProductImageStore>();
builder.Services.AddScoped<AccessService>();
builder.Services.AddScoped<IDirectoryClient, LdapDirectoryClient>();
builder.Services.AddScoped<DirectoryAuthService>();
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton<BackupService>();
string keysDirectory = DataPaths.Resolve(builder.Environment.ContentRootPath, "DATA_PROTECTION_KEYS_DIR", "Data", "Keys");
Directory.CreateDirectory(keysDirectory);
builder.Services.AddDataProtection()
    .SetApplicationName("KiComLib")
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<ProjectJobQueue>();
builder.Services.AddSingleton<ProjectJobProcessor>();
builder.Services.AddSingleton<GitCli>();
builder.Services.AddSingleton<GitMirrorService>();
builder.Services.AddScoped<RevisionDiscovery>();
builder.Services.AddScoped<ProjectAccessService>();
builder.Services.AddSingleton<IGitProviderAdapter, GitLabAdapter>();
builder.Services.AddSingleton<IGitProviderAdapter, GitHubAdapter>();
builder.Services.AddSingleton<IGitProviderAdapter, GenericGitAdapter>();
builder.Services.AddScoped<FetchJobHandler>();
builder.Services.AddScoped<DiscoverJobHandler>();
builder.Services.AddScoped<AnalyzeJobHandler>();
builder.Services.AddScoped<RematchJobHandler>();
builder.Services.AddScoped<RenderJobHandler>();
builder.Services.AddScoped<VisualDiffJobHandler>();
builder.Services.AddScoped<OutputsJobHandler>();
builder.Services.AddScoped<ProjectBlobStore>();
builder.Services.AddScoped<IProjectJobHandler>(services => services.GetRequiredService<FetchJobHandler>());
builder.Services.AddScoped<IProjectJobHandler>(services => services.GetRequiredService<DiscoverJobHandler>());
builder.Services.AddScoped<IProjectJobHandler>(services => services.GetRequiredService<AnalyzeJobHandler>());
builder.Services.AddScoped<IProjectJobHandler>(services => services.GetRequiredService<RematchJobHandler>());
builder.Services.AddScoped<IProjectJobHandler>(services => services.GetRequiredService<RenderJobHandler>());
builder.Services.AddScoped<IProjectJobHandler>(services => services.GetRequiredService<VisualDiffJobHandler>());
builder.Services.AddScoped<IProjectJobHandler>(services => services.GetRequiredService<OutputsJobHandler>());
builder.Services.AddSingleton<WebhookRateLimiter>();
builder.Services.AddSingleton<LoginRateLimiter>();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient(TrustedPartsClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://api.trustedparts.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
foreach (string distributorClient in new[]
{
    MouserClient.HttpClientName,
    DigiKeyClient.HttpClientName,
    FarnellClient.HttpClientName,
    ArrowClient.HttpClientName,
    TmeClient.HttpClientName
})
{
    builder.Services.AddHttpClient(distributorClient, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    });
}

builder.Services.AddHttpClient(ProductImageStore.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() => OutboundUrl.CreatePinnedHandler(DecompressionMethods.None));
builder.Services.AddHttpClient(DistributorDatasheetImporter.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() => OutboundUrl.CreatePinnedHandler(DecompressionMethods.All));
builder.Services.AddHttpClient(KiCadRunnerClient.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromMinutes(11);
});
builder.Services.AddScoped<IKiCadRunnerClient, KiCadRunnerClient>();
if (ProjectWorker.Enabled)
{
    builder.Services.AddHostedService<ProjectWorker>();
    builder.Services.AddHostedService<GitPollingService>();
}
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxRequestBytes;
});

List<IPAddress> trustedProxies = new();
string? trustedProxySetting = Environment.GetEnvironmentVariable("TRUSTED_PROXIES");
if (!string.IsNullOrWhiteSpace(trustedProxySetting))
{
    foreach (string entry in trustedProxySetting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!IPAddress.TryParse(entry, out IPAddress? address))
        {
            throw new InvalidOperationException($"TRUSTED_PROXIES entry '{entry}' is not an IP address.");
        }

        trustedProxies.Add(address);
    }

    if (trustedProxies.Count == 0)
    {
        throw new InvalidOperationException("TRUSTED_PROXIES is set but contains no addresses.");
    }

    // Only these addresses may supply X-Forwarded-For and X-Forwarded-Proto.
    // compose_ssl.yml does not publish the app port, and nginx overwrites both headers.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        foreach (IPAddress address in trustedProxies)
        {
            options.KnownProxies.Add(address);
        }
    });
}

string? corsOrigins = Environment.GetEnvironmentVariable("CORS_ORIGINS");
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policyBuilder =>
    {
        policyBuilder.AllowAnyHeader().AllowAnyMethod();
        if (string.IsNullOrWhiteSpace(corsOrigins))
        {
            // Same-origin requests do not need an allow-list. Cross-origin browsers are refused
            // unless CORS_ORIGINS lists them, for example http://localhost:4200.
            policyBuilder.SetIsOriginAllowed(_ => false);
            return;
        }

        string[] origins = corsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        policyBuilder.WithOrigins(origins);
    });
});

var app = builder.Build();

if (trustedProxies.Count > 0)
{
    app.UseForwardedHeaders();
}

if (!string.Equals(Environment.GetEnvironmentVariable("DB_AUTO_INIT"), "false", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    }
    catch (Exception exception)
    {
        app.Logger.LogCritical(exception, "Database migration failed; refusing to start.");
        throw;
    }
}

else
{
    await app.Services.GetRequiredService<DatabaseInitializer>().ValidateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com data:; img-src 'self' data: blob:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    await next();
});
app.UseCors();

app.UseStaticFiles();
app.UseRouting();
app.UseMiddleware<CatalogAccessMiddleware>();

app.UseAuthorization();
app.MapControllers();

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program { }
