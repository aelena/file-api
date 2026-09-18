using Aelena.FileApi.Core.Services.Xlsx;

namespace Aelena.FileApi.Api.Endpoints;

/// <summary>
/// XLSX endpoints — metrics, sheets, cell data, conversion, metadata, search,
/// and the two audits that answer questions a spreadsheet does not volunteer:
/// what it links to, and what it is hiding.
/// </summary>
public static class XlsxEndpoints
{
    private const string XlsxMime =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static RouteGroupBuilder MapXlsxEndpoints(this RouteGroupBuilder group)
    {
        // ── Read ─────────────────────────────────────────────────────────

        group.MapPost("/metrics", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(XlsxService.GetMetrics(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("XlsxMetrics").DisableAntiforgery().Produces<XlsxMetrics>(200);

        group.MapPost("/sheets", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(XlsxService.GetSheets(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("XlsxSheets").DisableAntiforgery().Produces<XlsxSheetsResponse>(200);

        group.MapPost("/sheet", async (IFormFile file, string? sheet, int? limit,
            HttpContext ctx, CancellationToken ct) =>
            Results.Ok(XlsxService.GetSheet(await file.ReadAllBytesAsync(ctx, ct), file.FileName, sheet, limit))
        ).WithName("XlsxSheet")
         .WithDescription("Read one sheet by name or zero-based index; the first sheet by default.")
         .DisableAntiforgery().Produces<XlsxSheetDataResponse>(200);

        group.MapPost("/metadata", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(XlsxService.GetMetadata(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("XlsxMetadata").DisableAntiforgery().Produces<XlsxMetadataResponse>(200);

        group.MapPost("/health", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(XlsxService.HealthCheck(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("XlsxHealth").DisableAntiforgery().Produces<XlsxHealthResponse>(200);

        group.MapPost("/search", async (IFormFile file, string? query, string? pattern,
            HttpContext ctx, CancellationToken ct) =>
        {
            var (name, matches) = XlsxService.Search(
                await file.ReadAllBytesAsync(ctx, ct), file.FileName, query, pattern);
            return Results.Ok(new SearchResponse(name, matches.Count, matches));
        }).WithName("XlsxSearch").DisableAntiforgery().Produces<SearchResponse>(200);

        // ── Audits ───────────────────────────────────────────────────────

        group.MapPost("/audit-links", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(XlsxService.AuditLinks(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("XlsxAuditLinks")
         .WithDescription(
            "External workbook references, hyperlinks, formulas that call out to the network "
            + "or the shell, and whether the workbook carries macros.")
         .DisableAntiforgery().Produces<XlsxLinkAuditResponse>(200);

        group.MapPost("/hidden", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(XlsxService.FindHidden(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("XlsxHidden")
         .WithDescription(
            "Hidden sheets, rows and columns. Hidden is not removed: the data travels with the "
            + "file and reappears with one right-click.")
         .DisableAntiforgery().Produces<XlsxHiddenResponse>(200);

        // ── Write ────────────────────────────────────────────────────────

        group.MapPost("/to-csv", async (IFormFile file, string? sheet,
            HttpContext ctx, CancellationToken ct) =>
        {
            var (name, bytes) = XlsxService.SheetToCsv(
                await file.ReadAllBytesAsync(ctx, ct), file.FileName, sheet);
            return Results.File(bytes, "text/csv; charset=utf-8", name);
        }).WithName("XlsxToCsv").DisableAntiforgery();

        group.MapPost("/to-md", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
        {
            var (name, bytes) = XlsxService.ToMarkdown(await file.ReadAllBytesAsync(ctx, ct), file.FileName);
            return Results.File(bytes, "text/markdown; charset=utf-8", name);
        }).WithName("XlsxToMarkdown").DisableAntiforgery();

        group.MapPost("/remove-metadata", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
        {
            var (name, bytes) = XlsxService.RemoveMetadata(
                await file.ReadAllBytesAsync(ctx, ct), file.FileName);
            return Results.File(bytes, XlsxMime, name);
        }).WithName("XlsxRemoveMetadata").DisableAntiforgery();

        return group;
    }
}
