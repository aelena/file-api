using Aelena.FileApi.Core.Services.Documents;
#if INCLUDE_PDF
using Aelena.FileApi.Core.Services.Pdf;
#endif

namespace Aelena.FileApi.Api.Endpoints;

/// <summary>
/// Conversion endpoints for formats without a toolkit of their own — EPUB,
/// MOBI/PalmDOC, DjVu and legacy binary <c>.doc</c>.
/// <para>
/// Every route sniffs the upload before it reads it. A file whose content is
/// not one of the five supported formats gets a 415 naming what it actually is;
/// a file that is the right format but structurally broken gets a 422 saying
/// where; a file that is well-formed but locked or compressed in a scheme this
/// service does not decode gets a 501 saying so. Nothing here returns an empty
/// success for a file it could not read.
/// </para>
/// </summary>
public static class ConvertEndpoints
{
    public static RouteGroupBuilder MapConvertEndpoints(this RouteGroupBuilder group)
    {
        // ── Inspection ───────────────────────────────────────────────────

        group.MapPost("/detect", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(DocumentConversionService.Detect(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("ConvertDetect").DisableAntiforgery().Produces<DocumentDetectionResponse>(200);

        group.MapPost("/validate", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(DocumentConversionService.Validate(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("ConvertValidate").DisableAntiforgery().Produces<DocumentValidationResponse>(200);

        group.MapPost("/metadata", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(DocumentConversionService.GetMetadata(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("ConvertMetadata").DisableAntiforgery().Produces<DocumentMetadataResponse>(200);

        // ── Extraction ───────────────────────────────────────────────────

        group.MapPost("/text", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(DocumentConversionService.ExtractText(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("ConvertText").DisableAntiforgery().Produces<DocumentTextResponse>(200);

        group.MapPost("/markdown", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(DocumentConversionService.ExtractToMarkdown(
                await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("ConvertMarkdown").DisableAntiforgery().Produces<DocumentMarkdownResponse>(200);

        // ── Downloads ────────────────────────────────────────────────────

        group.MapPost("/to-txt", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
        {
            var (name, bytes) = DocumentConversionService.ToTextFile(
                await file.ReadAllBytesAsync(ctx, ct), file.FileName);
            return Results.File(bytes, "text/plain; charset=utf-8", name);
        }).WithName("ConvertToTxt").DisableAntiforgery();

        group.MapPost("/to-md", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
        {
            var (name, bytes) = DocumentConversionService.ToMarkdownFile(
                await file.ReadAllBytesAsync(ctx, ct), file.FileName);
            return Results.File(bytes, "text/markdown; charset=utf-8", name);
        }).WithName("ConvertToMd").DisableAntiforgery();

#if INCLUDE_PDF
        // PDF rendering lives in the AGPL package, so this route is dropped
        // along with it by -p:IncludePdf=false. Everything above stays.
        group.MapPost("/to-pdf", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
        {
            var (name, bytes) = MarkdownPdfService.ConvertDocument(
                await file.ReadAllBytesAsync(ctx, ct), file.FileName);
            return Results.File(bytes, "application/pdf", name);
        }).WithName("ConvertToPdf").DisableAntiforgery();
#endif

        return group;
    }
}
