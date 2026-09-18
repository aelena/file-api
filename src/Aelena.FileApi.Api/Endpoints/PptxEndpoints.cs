using Aelena.FileApi.Core.Services.Pptx;

namespace Aelena.FileApi.Api.Endpoints;

/// <summary>
/// PPTX endpoints — metrics, slides, speaker notes, Markdown outline, metadata
/// and search.
/// </summary>
public static class PptxEndpoints
{
    private const string PptxMime =
        "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    public static RouteGroupBuilder MapPptxEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/metrics", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(PptxService.GetMetrics(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("PptxMetrics").DisableAntiforgery().Produces<PptxMetrics>(200);

        group.MapPost("/slides", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(PptxService.GetSlides(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("PptxSlides")
         .WithDescription("Every slide's title, body text and speaker notes, in presentation order.")
         .DisableAntiforgery().Produces<PptxSlidesResponse>(200);

        group.MapPost("/notes", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(PptxService.GetNotes(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("PptxNotes")
         .WithDescription(
            "Speaker notes only. A deck's slides are headlines; the argument behind them is "
            + "usually in the notes pane, which most extractors drop.")
         .DisableAntiforgery().Produces<PptxSlidesResponse>(200);

        group.MapPost("/extract-markdown", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(PptxService.ExtractToMarkdown(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("PptxExtractMarkdown").DisableAntiforgery().Produces<PptxMarkdownResponse>(200);

        group.MapPost("/metadata", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(PptxService.GetMetadata(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("PptxMetadata").DisableAntiforgery().Produces<PptxMetadataResponse>(200);

        group.MapPost("/search", async (IFormFile file, string? query, string? pattern,
            HttpContext ctx, CancellationToken ct) =>
        {
            var (name, matches) = PptxService.Search(
                await file.ReadAllBytesAsync(ctx, ct), file.FileName, query, pattern);
            return Results.Ok(new SearchResponse(name, matches.Count, matches));
        }).WithName("PptxSearch")
          .WithDescription("Search slide bodies and speaker notes.")
          .DisableAntiforgery().Produces<SearchResponse>(200);

        group.MapPost("/remove-metadata", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
        {
            var (name, bytes) = PptxService.RemoveMetadata(
                await file.ReadAllBytesAsync(ctx, ct), file.FileName);
            return Results.File(bytes, PptxMime, name);
        }).WithName("PptxRemoveMetadata").DisableAntiforgery();

        return group;
    }
}
