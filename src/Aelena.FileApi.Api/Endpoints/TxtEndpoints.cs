using Aelena.FileApi.Core.Services.Common;

namespace Aelena.FileApi.Api.Endpoints;

/// <summary>Plain text file endpoints — metrics and search.</summary>
public static class TxtEndpoints
{
    public static RouteGroupBuilder MapTxtEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/metrics", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
        {
            return Results.Ok(TxtService.GetMetrics(await file.ReadAllBytesAsync(ctx, ct), file.FileName));
        })
        .WithName("TxtMetrics")
        .WithDescription("Return word, character, token, and line counts for a TXT file.")
        .DisableAntiforgery()
        .Produces<TxtMetrics>(200);

        group.MapPost("/search", async (IFormFile file, string? query, string? pattern,
            HttpContext ctx, CancellationToken ct) =>
        {
            // TextSearch reports a bad query/pattern combination as a 400 itself now,
            // so the ArgumentException translation that used to live here is gone.
            var (fileName, matches) = TxtService.Search(
                await file.ReadAllBytesAsync(ctx, ct), file.FileName, query, pattern);

            return Results.Ok(new SearchResponse(fileName, matches.Count, matches));
        })
        .WithName("TxtSearch")
        .WithDescription("Search a TXT file for literal text or regex matches.")
        .DisableAntiforgery()
        .Produces<SearchResponse>(200);

        group.MapPost("/detect-encoding", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
        {
            return Results.Ok(TextEncodingService.Detect(await file.ReadAllBytesAsync(ctx, ct), file.FileName));
        })
        .WithName("TxtDetectEncoding")
        .WithDescription(
            "Encoding, byte-order mark, line endings, and any control bytes that do not belong in "
            + "text — with the line and column of each, because a stray 0x08 is invisible in an "
            + "editor and only surfaces when something downstream rejects the file.")
        .DisableAntiforgery()
        .Produces<TextEncodingResponse>(200);

        group.MapPost("/normalise", async (IFormFile file, string? lineEnding,
            bool? stripBom, bool? stripControls, HttpContext ctx, CancellationToken ct) =>
        {
            var (name, bytes) = TextEncodingService.Normalise(
                await file.ReadAllBytesAsync(ctx, ct), file.FileName,
                lineEnding ?? "lf", stripBom ?? true, stripControls ?? true);

            return Results.File(bytes, "text/plain; charset=utf-8", name);
        })
        .WithName("TxtNormalise")
        .WithDescription("Rewrite as UTF-8 with consistent line endings, no BOM and no control bytes.")
        .DisableAntiforgery();

        return group;
    }
}
