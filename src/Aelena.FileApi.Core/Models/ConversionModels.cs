namespace Aelena.FileApi.Core.Models;

/// <summary>What a buffer turned out to be, and whether the conversion family can read it.</summary>
public sealed record DocumentDetectionResponse(
    string FileName,
    long FileSizeBytes,
    string Format,
    string? DeclaredFormat,
    bool Supported,
    bool ExtensionMismatch,
    IReadOnlyList<string> Capabilities);

/// <summary>
/// The result of the structural checks a file passes before any conversion is
/// attempted. <c>Valid</c> means the container parsed; <c>CanExtractText</c>
/// means there is text in it to extract, which is a separate question — a
/// perfectly well-formed scanned DjVu has no text layer at all.
/// </summary>
public sealed record DocumentValidationResponse(
    string FileName,
    string Format,
    bool Valid,
    bool CanExtractText,
    int IssueCount,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<HealthIssue> Issues,
    IReadOnlyDictionary<string, object>? Details = null);

/// <summary>
/// Bibliographic metadata, normalised across EPUB, MOBI, DjVu and DOC.
/// <para>
/// <c>SectionCount</c> means what the container declares: spine items for an
/// EPUB, text records for a MOBI, pages for a DjVu, and 1 for a flat DOC. On a
/// response that came back from an extraction it is instead the number of
/// sections actually returned, which is lower whenever a declared section had
/// no readable content.
/// </para>
/// </summary>
public sealed record DocumentMetadataResponse(
    string FileName,
    string Format,
    string? Title = null,
    IReadOnlyList<string>? Authors = null,
    string? Language = null,
    string? Publisher = null,
    string? Identifier = null,
    string? Published = null,
    string? Description = null,
    string? Rights = null,
    IReadOnlyList<string>? Subjects = null,
    int SectionCount = 0,
    IReadOnlyDictionary<string, string>? Extra = null);

/// <summary>Extracted text, split into the sections the source format defines.</summary>
public sealed record DocumentTextResponse(
    string FileName,
    string Format,
    int SectionCount,
    int WordCount,
    int CharCount,
    int TokenCount,
    string? Language,
    IReadOnlyList<PageContent> Sections);

/// <summary>A whole document rendered as one Markdown string.</summary>
public sealed record DocumentMarkdownResponse(
    string FileName,
    string Format,
    int SectionCount,
    string Markdown);
