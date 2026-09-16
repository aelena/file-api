using Aelena.FileApi.Core.Models;

namespace Aelena.FileApi.Core.Services.Documents;

/// <summary>
/// One readable unit of a document: a spine item in an EPUB, a text record
/// boundary in a MOBI, a page in a DjVu, the whole body of a DOC.
/// </summary>
/// <param name="Index">1-based position, so it lines up with <see cref="PageContent.Page"/>.</param>
/// <param name="Title">Section heading where the format records one.</param>
/// <param name="Text">Plain text, block structure preserved, markup removed.</param>
/// <param name="Markdown">The same content as Markdown.</param>
public sealed record DocumentSection(int Index, string? Title, string Text, string Markdown);

/// <summary>
/// What every reader in this namespace produces: the sections, plus whatever
/// metadata the container carried. Keeping text and Markdown together means a
/// file is parsed once no matter which of the two the caller asked for.
/// </summary>
public sealed record ExtractedDocument(
    DocumentFormat Format,
    IReadOnlyList<DocumentSection> Sections,
    DocumentMetadataResponse Metadata);
