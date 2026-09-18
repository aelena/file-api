using Aelena.FileApi.Core.Services.Common;

namespace Aelena.FileApi.Api.Endpoints;

/// <summary>
/// CSV endpoints — dialect detection, load validation, column profiling and
/// conversion.
/// <para>
/// Nothing here is told how the file is delimited. The dialect is inferred from
/// the bytes and reported back, because a file named <c>.csv</c> is
/// semicolon-separated about as often as it is comma-separated and the caller
/// usually does not know which they have.
/// </para>
/// </summary>
public static class CsvEndpoints
{
    public static RouteGroupBuilder MapCsvEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/inspect", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(CsvService.Inspect(await file.ReadAllBytesAsync(ctx, ct), file.FileName))
        ).WithName("CsvInspect")
         .WithDescription(
            "Detected delimiter, quote character, line ending and encoding, whether there is a "
            + "header, and every reason the file would fail to load.")
         .DisableAntiforgery().Produces<CsvInspectResponse>(200);

        group.MapPost("/profile", async (IFormFile file, int? sampleSize,
            HttpContext ctx, CancellationToken ct) =>
            Results.Ok(CsvService.Profile(
                await file.ReadAllBytesAsync(ctx, ct), file.FileName, sampleSize ?? 5))
        ).WithName("CsvProfile")
         .WithDescription("Per column: inferred type, empty count, distinct count, range and samples.")
         .DisableAntiforgery().Produces<CsvProfileResponse>(200);

        group.MapPost("/rows", async (IFormFile file, int? limit, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(CsvService.GetRows(await file.ReadAllBytesAsync(ctx, ct), file.FileName, limit))
        ).WithName("CsvRows").DisableAntiforgery().Produces<CsvRowsResponse>(200);

        group.MapPost("/to-json", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
        {
            var (name, bytes) = CsvService.ToJson(await file.ReadAllBytesAsync(ctx, ct), file.FileName);
            return Results.File(bytes, "application/json; charset=utf-8", name);
        }).WithName("CsvToJson").DisableAntiforgery();

        group.MapPost("/to-md", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
        {
            var (name, bytes) = CsvService.ToMarkdown(await file.ReadAllBytesAsync(ctx, ct), file.FileName);
            return Results.File(bytes, "text/markdown; charset=utf-8", name);
        }).WithName("CsvToMarkdown").DisableAntiforgery();

        return group;
    }
}
