using System.Globalization;
using Aelena.FileApi.Api.Configuration;
using Aelena.FileApi.Api.Endpoints;
using Aelena.FileApi.Api.Middleware;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

// ── Bootstrap Serilog early (before host builds) ────────────────────────────

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Configuration ────────────────────────────────────────────────────
    builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("AppSettings"));
    var appSettings = builder.Configuration.GetSection("AppSettings").Get<AppSettings>() ?? new AppSettings();

    // Fail at startup rather than serving traffic that would accept forged sessions:
    // the checked-in placeholder JWT secret lets anyone who has read the repository
    // mint a valid token.
    appSettings.ThrowIfUnsafeForProduction(builder.Environment.IsDevelopment());

    // ── Serilog ──────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration));

    // ── OpenTelemetry ────────────────────────────────────────────────────
    var otelEndpoint = builder.Configuration["OpenTelemetry:Endpoint"];

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("Aelena.FileApi"))
        .WithTracing(t =>
        {
            t.AddAspNetCoreInstrumentation()
             .AddHttpClientInstrumentation();
            if (!string.IsNullOrWhiteSpace(otelEndpoint))
                t.AddOtlpExporter(o => o.Endpoint = new Uri(otelEndpoint));
        })
        .WithMetrics(m =>
        {
            m.AddAspNetCoreInstrumentation()
             .AddHttpClientInstrumentation();
            if (!string.IsNullOrWhiteSpace(otelEndpoint))
                m.AddOtlpExporter(o => o.Endpoint = new Uri(otelEndpoint));
        });

    // ── Infrastructure services (Api-layer concerns) ───────────────────
    builder.Services.AddSingleton(new Aelena.FileApi.Core.Services.Llm.LlmConfig());
    builder.Services.AddSingleton(new Aelena.FileApi.Core.Services.Jobs.InMemoryJobStore<ComparisonReport>(appSettings.MaxInMemoryJobs));
    builder.Services.AddSingleton(new Aelena.FileApi.Core.Services.Jobs.InMemoryJobStore<SummarizeJobReport>(appSettings.MaxInMemoryJobs));
    builder.Services.AddSingleton(new Aelena.FileApi.Core.Services.Jobs.InMemoryJobStore<BatchJobResponse>(appSettings.MaxInMemoryBatches));
    builder.Services.AddSingleton(new Aelena.FileApi.Core.Services.Persistence.ShareRepository("data/shares.db"));
    builder.Services.AddSingleton(new Aelena.FileApi.Core.Services.Llm.PromptRenderer());
    builder.Services.AddSingleton<Aelena.FileApi.Api.Services.WebhookService>();

    // ── HTTP / CORS ──────────────────────────────────────────────────────
    builder.Services.AddCors(opt => opt.AddDefaultPolicy(p =>
        p.WithOrigins(appSettings.GetCorsOriginList())
         .AllowAnyMethod()
         .AllowAnyHeader()
         .AllowCredentials()));

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new()
        {
            Title = "FileApi — Document Processing & AI Analysis",
            Version = "v1",
            Description = """
            Comprehensive document analysis, comparison, transformation, and AI-powered insights
            across PDF, DOCX, images, geospatial, email, video, and more.

            **Authentication**: JWT token as `auth_token` httpOnly cookie.
            **Error format**: RFC 9457 Problem Details.
            """
        });
    });

    builder.Services.AddHealthChecks();
    builder.Services.AddHttpClient();

    var app = builder.Build();

    // ── Middleware pipeline (order matters: outermost first) ──────────────
    //
    // Audit sits outside the exception handler so it records the status the caller
    // actually received. Inside it — where it used to be — it ran before
    // ExceptionMiddleware had mapped the failure, so every failed request was logged
    // as its pre-failure status, and requests rejected by the rate limiter never
    // reached it at all.
    app.UseSerilogRequestLogging();
    app.UseMiddleware<AuditLogMiddleware>();
    app.UseMiddleware<ExceptionMiddleware>();
    app.UseMiddleware<AuthRateLimitMiddleware>();
    app.UseCors();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    // ── Health ────────────────────────────────────────────────────────────
    app.MapHealthChecks("/health");
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

    // ── Endpoint groups ──────────────────────────────────────────────────
    app.MapGroup("/api/auth").WithTags("Auth").MapAuthEndpoints();

    // ── Phase 2 endpoints ───────────────────────────────────────────────
    app.MapGroup("/hash").WithTags("Hash").MapHashEndpoints();
    app.MapGroup("/txt").WithTags("TXT").MapTxtEndpoints();
    app.MapGroup("/zip").WithTags("ZIP").MapZipEndpoints();
    app.MapGroup("/readability").WithTags("Readability").MapReadabilityEndpoints();
    app.MapGroup("/search").WithTags("Search").MapSearchEndpoints();
    app.MapGroup("/strip").WithTags("Strip").MapStripEndpoints();
    app.MapGroup("/redact").WithTags("Redact").MapRedactEndpoints();

    // ── Phase 3 endpoints ───────────────────────────────────────────────
#if INCLUDE_PDF
    app.MapGroup("/pdf").WithTags("PDF").MapPdfEndpoints();
#endif

    // ── Phase 4 endpoints ───────────────────────────────────────────────
    app.MapGroup("/docx").WithTags("DOCX").MapDocxEndpoints();
    app.MapGroup("/email").WithTags("Email").MapEmailEndpoints();

    // ── Phase 5 endpoints ───────────────────────────────────────────────
    app.MapGroup("/image").WithTags("Image").MapImageEndpoints();
    app.MapGroup("/image-ai").WithTags("Image AI").MapImageAiEndpoints();

    // ── Phase 6 endpoints ───────────────────────────────────────────────
    app.MapGroup("/compare").WithTags("Compare").MapCompareEndpoints();
    app.MapGroup("/summarize").WithTags("Summarize").MapSummarizeEndpoints();
    app.MapGroup("/batch").WithTags("Batch").MapBatchEndpoints();
    app.MapGroup("/pii").WithTags("PII").MapPiiEndpoints();
    app.MapGroup("/classify").WithTags("Classify").MapClassifyEndpoints();
    app.MapGroup("/qa").WithTags("Q&A").MapQaEndpoints();
    app.MapGroup("/share").WithTags("Share").MapShareEndpoints();

    // ── Phase 7 endpoints ───────────────────────────────────────────────
    app.MapGroup("/geospatial").WithTags("Geospatial").MapGeospatialEndpoints();
    app.MapGroup("/video").WithTags("Video").MapVideoEndpoints();
    app.MapGroup("/markdown").WithTags("Markdown").MapMarkdownEndpoints();

    // ── Phase 8 endpoints ───────────────────────────────────────────────
    // EPUB, MOBI/PalmDOC, DjVu and legacy .doc — formats with no toolkit of
    // their own, converted to text, Markdown and PDF through one group.
    app.MapGroup("/convert").WithTags("Convert").MapConvertEndpoints();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");

    // Exit non-zero so that a failed startup is visible as a failure. Without
    // this the process fell off the end of Main and returned 0, so a container
    // that refused to start — on the placeholder JWT secret, say — looked to
    // Docker and Kubernetes exactly like a clean shutdown: `restart: on-failure`
    // would not restart it and nothing would alert.
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;

// Make Program visible to WebApplicationFactory in test projects
public partial class Program;
