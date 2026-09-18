namespace Aelena.FileApi.Core.Models;

/// <summary>Metrics for a presentation.</summary>
public sealed record PptxMetrics(
    string FileName,
    long FileSizeBytes,
    int WordCount,
    int CharCount,
    int TokenCount,
    string? Language,
    string? CreationDate,
    string? LastModifiedDate,
    int SlideCount,
    int HiddenSlideCount,
    int ShapeCount,
    int ImageCount,
    int TableCount,
    int SlidesWithNotes) : BaseMetrics(
        FileName, FileSizeBytes, WordCount, CharCount, TokenCount,
        Language, CreationDate, LastModifiedDate);

/// <summary>
/// One slide's content.
/// <para>
/// <c>Notes</c> is the speaker-notes pane, which carries the actual argument
/// behind a deck far more often than the slide body does, and which most
/// extraction tools drop.
/// </para>
/// </summary>
public sealed record PptxSlide(
    int Number,
    string? Title,
    string Text,
    string? Notes,
    bool Hidden,
    int ShapeCount,
    int ImageCount,
    int TableCount);

/// <summary>Every slide in a presentation, in running order.</summary>
public sealed record PptxSlidesResponse(
    string FileName,
    int SlideCount,
    IReadOnlyList<PptxSlide> Slides);

/// <summary>A presentation rendered as one Markdown outline.</summary>
public sealed record PptxMarkdownResponse(
    string FileName,
    int SlideCount,
    string Markdown);

/// <summary>Core and custom properties from a presentation.</summary>
public sealed record PptxMetadataResponse(
    string FileName,
    string? Title = null,
    string? Author = null,
    string? Subject = null,
    string? Keywords = null,
    string? Category = null,
    string? Comments = null,
    string? LastModifiedBy = null,
    string? Created = null,
    string? Modified = null,
    int SlideCount = 0,
    IReadOnlyDictionary<string, string>? CustomMetadata = null);
