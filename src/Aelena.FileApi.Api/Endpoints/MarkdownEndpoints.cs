#if INCLUDE_PDF
using System.Text;
using Aelena.FileApi.Core.Services.Pdf;
#endif

namespace Aelena.FileApi.Api.Endpoints;

/// <summary>Markdown to PDF conversion endpoint.</summary>
public static class MarkdownEndpoints
{
    public static RouteGroupBuilder MapMarkdownEndpoints(this RouteGroupBuilder group)
    {
#if INCLUDE_PDF
        // Typeset by MarkdownPdfService, the same renderer /convert/to-pdf uses.
        // It has no HTML stage, so there is nothing for a stylesheet to apply
        // to; `css` is gone rather than accepted and ignored.
        group.MapPost("/to-pdf", async (IFormFile file, HttpContext ctx, CancellationToken ct) =>
        {
            var data = await file.ReadAllBytesAsync(ctx, ct);
            var (name, bytes) = MarkdownPdfService.FromMarkdown(
                Encoding.UTF8.GetString(data), file.FileName);
            return Results.File(bytes, "application/pdf", name);
        }).WithName("MarkdownToPdf").DisableAntiforgery();
#else
        // The renderer is built on iText and ships in the AGPL package, so the
        // MIT-only build cannot answer this route.
        group.MapPost("/to-pdf", (IFormFile file) => Unavailable())
            .WithName("MarkdownToPdf").DisableAntiforgery();
#endif

        return group;
    }

#if !INCLUDE_PDF
    private static IResult Unavailable() => throw new FileApiException(501,
        "Markdown to PDF rendering lives in Aelena.FileApi.Core.Pdf, which this build excludes " +
        "(-p:IncludePdf=false). Use /convert/to-md for Markdown output.");
#endif
}
