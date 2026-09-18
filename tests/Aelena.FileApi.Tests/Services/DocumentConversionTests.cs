using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Services.Documents;
using AwesomeAssertions;
using Xunit;

namespace Aelena.FileApi.Tests.Services;

/// <summary>
/// Conversion tests for EPUB, MOBI, DjVu and legacy DOC, run against the real
/// public-domain files in <c>SampleFiles/</c> rather than against fixtures this
/// repository synthesised. See <c>SampleFiles/README.md</c> for provenance.
/// </summary>
public class DocumentConversionTests
{
    private const string Epub = "public-domain-pieces.epub";
    private const string Mobi = "public-domain-pieces.mobi";
    private const string Doc = "public-domain-pieces.doc";
    private const string DjvuWithText = "un-resolution-1837.djvu";
    private const string DjvuNoText = "hr-report-94-1476-p249.djvu";

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "SampleFiles", name));

    // ── Format detection ─────────────────────────────────────────────────

    [Theory]
    [InlineData(Epub, "EPUB")]
    [InlineData(Mobi, "MOBI")]
    [InlineData(Doc, "DOC (Word 97-2003)")]
    [InlineData(DjvuWithText, "DjVu")]
    [InlineData(DjvuNoText, "DjVu")]
    public void Detect_RecognisesEveryFixtureFromItsContent(string fixture, string expected)
    {
        var result = DocumentConversionService.Detect(Fixture(fixture), fixture);

        result.Format.Should().Be(expected);
        result.Supported.Should().BeTrue();
        result.ExtensionMismatch.Should().BeFalse();
        result.Capabilities.Should().Contain(["text", "markdown", "pdf"]);
    }

    [Fact]
    public void Detect_RenamedFile_ReportsTheMismatchWithoutRefusingIt()
    {
        // Content wins over the extension, and the disagreement is surfaced
        // rather than silently resolved either way.
        var result = DocumentConversionService.Detect(Fixture(Epub), "actually-an-epub.mobi");

        result.Format.Should().Be("EPUB");
        result.DeclaredFormat.Should().Be("MOBI");
        result.ExtensionMismatch.Should().BeTrue();
        result.Supported.Should().BeTrue();
    }

    [Fact]
    public void Detect_UnsupportedFormat_IsReportedRatherThanGuessed()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n% fake but correctly signed\n");

        var result = DocumentConversionService.Detect(pdf, "report.pdf");

        result.Format.Should().Be("PDF");
        result.Supported.Should().BeFalse();
        result.Capabilities.Should().BeEmpty();
    }

    [Fact]
    public void Extract_PlainZip_Is415AndPointsAtTheRightEndpoint()
    {
        var ex = FluentActions.Invoking(() => DocumentConversionService.ExtractText(PlainZip(), "archive.zip"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(415);
        ex.Detail.Should().Contain("ZIP").And.Contain("/zip/inspect");
    }

    [Fact]
    public void Extract_Pdf_Is415AndPointsAtThePdfEndpoints()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n% not a real document\n");

        var ex = FluentActions.Invoking(() => DocumentConversionService.ExtractText(pdf, "report.pdf"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(415);
        ex.Detail.Should().Contain("/pdf/*");
    }

    // ── EPUB ─────────────────────────────────────────────────────────────

    [Fact]
    public void Epub_Validate_CleanFileHasNoErrors()
    {
        var result = DocumentConversionService.Validate(Fixture(Epub), Epub);

        result.Format.Should().Be("EPUB");
        result.Valid.Should().BeTrue();
        result.CanExtractText.Should().BeTrue();
        result.ErrorCount.Should().Be(0);
        result.Details!["spineItems"].Should().NotBe(0);
    }

    [Fact]
    public void Epub_ExtractText_ReturnsTheSpineInReadingOrder()
    {
        var result = DocumentConversionService.ExtractText(Fixture(Epub), Epub);

        result.SectionCount.Should().BeGreaterThan(1);
        result.Sections.Select(s => s.Page).Should().BeInAscendingOrder();
        result.WordCount.Should().BeGreaterThan(100);
        result.Language.Should().Be("en");

        var all = string.Join("\n", result.Sections.Select(s => s.Text));
        all.Should().Contain("Shall I compare thee to a summer's day?");
        all.Should().Contain("The grapes are sour");
        all.Should().Contain("Necessity is the mother of invention");
    }

    [Fact]
    public void Epub_Metadata_ComesFromThePackageDocument()
    {
        var result = DocumentConversionService.GetMetadata(Fixture(Epub), Epub);

        result.Format.Should().Be("EPUB");
        result.Title.Should().Be("Three Public Domain Pieces");
        result.Authors.Should().Contain("Shakespeare");
        result.Language.Should().Be("en");
        result.Extra!["epubVersion"].Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Epub_Markdown_KeepsHeadingsAndEmphasis()
    {
        var result = DocumentConversionService.ExtractToMarkdown(Fixture(Epub), Epub);

        result.Markdown.Should().Contain("# Three Public Domain Pieces");
        result.Markdown.Should().Contain("Sonnet XVIII");
        // The fable's moral is bolded in the source and stays bolded here.
        result.Markdown.Should().Contain("**Necessity is the mother of invention.**");
    }

    [Fact]
    public void Epub_Truncated_IsRefusedAsUnreadableRatherThanThrowing500()
    {
        var truncated = Fixture(Epub)[..2000];

        var result = DocumentConversionService.Validate(truncated, Epub);
        result.Valid.Should().BeFalse();
        result.CanExtractText.Should().BeFalse();

        var ex = FluentActions.Invoking(() => DocumentConversionService.ExtractText(truncated, Epub))
            .Should().Throw<FileApiException>().Which;
        ex.StatusCode.Should().BeOneOf(415, 422);
    }

    [Fact]
    public void Epub_MissingContainer_NamesTheMissingPart()
    {
        // A ZIP with the right mimetype but no META-INF/container.xml sniffs as
        // an EPUB and then fails validation on exactly one thing.
        var result = DocumentConversionService.Validate(EpubWithoutContainer(), "broken.epub");

        result.Valid.Should().BeFalse();
        result.Issues.Should().Contain(i =>
            i.Severity == "error" && i.Message.Contains("META-INF/container.xml"));
    }

    // ── MOBI ─────────────────────────────────────────────────────────────

    [Fact]
    public void Mobi_Validate_ReportsThePalmDocHeader()
    {
        var result = DocumentConversionService.Validate(Fixture(Mobi), Mobi);

        result.Valid.Should().BeTrue();
        result.CanExtractText.Should().BeTrue();
        result.Details!["palmType"].Should().Be("BOOK");
        result.Details["palmCreator"].Should().Be("MOBI");
        result.Details["compression"].Should().Be("palmdoc");
        result.Details["encryptionType"].Should().Be(0);
    }

    [Fact]
    public void Mobi_ExtractText_DecompressesPalmDocLz77()
    {
        var result = DocumentConversionService.ExtractText(Fixture(Mobi), Mobi);

        var all = string.Join("\n", result.Sections.Select(s => s.Text));
        all.Should().Contain("Shall I compare thee to a summer's day?");
        all.Should().Contain("A crow perishing with thirst saw a pitcher");
        result.WordCount.Should().BeGreaterThan(100);
    }

    [Fact]
    public void Mobi_Metadata_ComesFromTheExthBlock()
    {
        var result = DocumentConversionService.GetMetadata(Fixture(Mobi), Mobi);

        result.Format.Should().Be("MOBI");
        result.Title.Should().Be("Three Public Domain Pieces");
        result.Authors.Should().Contain("Shakespeare");
    }

    [Fact]
    public void Mobi_HuffCdic_Is501AndSaysWhy()
    {
        var data = WithCompression(Fixture(Mobi), 17480);

        var validation = DocumentConversionService.Validate(data, Mobi);
        validation.CanExtractText.Should().BeFalse();

        var ex = FluentActions.Invoking(() => DocumentConversionService.ExtractText(data, Mobi))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(501);
        ex.Detail.Should().Contain("HUFF/CDIC");
    }

    [Fact]
    public void Mobi_DrmProtected_Is501RatherThanGarbage()
    {
        var data = WithEncryption(Fixture(Mobi), 2);

        var ex = FluentActions.Invoking(() => DocumentConversionService.ExtractText(data, Mobi))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(501);
        ex.Detail.Should().Contain("DRM-protected");
    }

    [Fact]
    public void Mobi_TruncatedRecordTable_IsRefusedBeforeAnySliceIsTaken()
    {
        var truncated = Fixture(Mobi)[..90];

        var result = DocumentConversionService.Validate(truncated, Mobi);

        result.Valid.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Message.Contains("truncated"));
    }

    [Fact]
    public void Mobi_WrongPalmType_IsNotTreatedAsABook()
    {
        var data = Fixture(Mobi).ToArray();
        Encoding.ASCII.GetBytes("DATA").CopyTo(data, 60);

        var ex = FluentActions.Invoking(() => DocumentConversionService.ExtractText(data, Mobi))
            .Should().Throw<FileApiException>().Which;

        // No longer a Palm book, so it never reaches the MOBI reader at all.
        ex.StatusCode.Should().Be(415);
    }

    // ── DjVu ─────────────────────────────────────────────────────────────

    [Fact]
    public void Djvu_Validate_WalksAMultiPageBundle()
    {
        var result = DocumentConversionService.Validate(Fixture(DjvuWithText), DjvuWithText);

        result.Format.Should().Be("DjVu");
        result.Valid.Should().BeTrue();
        result.Details!["formType"].Should().Be("DJVM");
        result.Details["pageCount"].Should().Be(4);
        result.Details["hasTextLayer"].Should().Be(true);
        result.Details["textChunksCompressed"].Should().Be(4);
    }

    [Fact]
    public void Djvu_CompressedTextLayer_Is501AndSaysWhereToGetTheText()
    {
        var result = DocumentConversionService.Validate(Fixture(DjvuWithText), DjvuWithText);
        result.CanExtractText.Should().BeFalse();

        var ex = FluentActions.Invoking(() =>
            DocumentConversionService.ExtractText(Fixture(DjvuWithText), DjvuWithText))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(501);
        ex.Detail.Should().Contain("TXTz").And.Contain("djvutxt");
    }

    [Fact]
    public void Djvu_NoTextLayerAtAll_Is422AndADifferentMessage()
    {
        // The distinction matters: "I cannot read this text layer" and "there is
        // no text layer" call for different things from the caller.
        var result = DocumentConversionService.Validate(Fixture(DjvuNoText), DjvuNoText);

        result.Valid.Should().BeTrue();
        result.CanExtractText.Should().BeFalse();
        result.Details!["formType"].Should().Be("DJVU");
        result.Details["pageCount"].Should().Be(1);
        result.Details["hasTextLayer"].Should().Be(false);

        var ex = FluentActions.Invoking(() =>
            DocumentConversionService.ExtractText(Fixture(DjvuNoText), DjvuNoText))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(422);
        ex.Detail.Should().Contain("no text layer");
    }

    [Fact]
    public void Djvu_Metadata_ReportsPageGeometry()
    {
        var result = DocumentConversionService.GetMetadata(Fixture(DjvuNoText), DjvuNoText);

        result.Format.Should().Be("DjVu");
        int.Parse(result.Extra!["pageWidth"], System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(0);
        int.Parse(result.Extra["dpi"], System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public void Djvu_Truncated_IsCaughtByTheDeclaredLength()
    {
        var truncated = Fixture(DjvuWithText)[..5000];

        var result = DocumentConversionService.Validate(truncated, DjvuWithText);

        result.Valid.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Message.Contains("truncated"));
    }

    // ── DOC ──────────────────────────────────────────────────────────────

    [Fact]
    public void Doc_Validate_ReadsTheFibAndPieceTable()
    {
        var result = DocumentConversionService.Validate(Fixture(Doc), Doc);

        result.Format.Should().Be("DOC");
        result.Valid.Should().BeTrue();
        result.CanExtractText.Should().BeTrue();
        result.Details!["nFib"].Should().Be("0x00C1");
        result.Details["encrypted"].Should().Be(false);
        result.Details["tableStream"].Should().BeOfType<string>()
            .Which.Should().BeOneOf("0Table", "1Table");
    }

    [Fact]
    public void Doc_ExtractText_WalksThePieceTable()
    {
        var result = DocumentConversionService.ExtractText(Fixture(Doc), Doc);

        var text = result.Sections[0].Text;
        text.Should().Contain("Shall I compare thee to a summer's day?");
        text.Should().Contain("The grapes are sour");
        text.Should().Contain("Necessity is the mother of invention");

        // Word's in-band control characters must not survive into the output.
        text.Should().NotContain("\u0007").And.NotContain("\u0013").And.NotContain("\u0014");
        result.WordCount.Should().BeGreaterThan(100);
    }

    [Fact]
    public void Doc_EncryptedFlag_Is422NamingThePassword()
    {
        var data = WithFibFlag(Fixture(Doc), 0x0100);

        var result = DocumentConversionService.Validate(data, Doc);
        result.Valid.Should().BeFalse();

        var ex = FluentActions.Invoking(() => DocumentConversionService.ExtractText(data, Doc))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(422);
        ex.Detail.Should().Contain("password-protected");
    }

    [Fact]
    public void Doc_NotACompoundFile_IsRefusedBeforeTheFatIsWalked()
    {
        var bogus = new byte[1024];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bogus, 0);
        Encoding.Unicode.GetBytes("WordDocument").CopyTo(bogus, 128);

        var result = DocumentConversionService.Validate(bogus, "bogus.doc");

        result.Valid.Should().BeFalse();
        result.ErrorCount.Should().BeGreaterThan(0);
    }

    // ── Downloads ────────────────────────────────────────────────────────

    [Fact]
    public void ToTextFile_RenamesAndReturnsUtf8()
    {
        var (name, bytes) = DocumentConversionService.ToTextFile(Fixture(Epub), Epub);

        name.Should().Be("public-domain-pieces.txt");
        Encoding.UTF8.GetString(bytes).Should().Contain("summer's day");
    }

    [Fact]
    public void ToMarkdownFile_RenamesAndReturnsUtf8()
    {
        var (name, bytes) = DocumentConversionService.ToMarkdownFile(Fixture(Doc), Doc);

        name.Should().Be("public-domain-pieces.md");
        Encoding.UTF8.GetString(bytes).Should().Contain("summer's day");
    }

    // ── Fixture surgery ──────────────────────────────────────────────────

    /// <summary>Offset of record 0, read from the Palm record offset table.</summary>
    private static int Record0(byte[] mobi) =>
        (int)BinaryPrimitives.ReadUInt32BigEndian(mobi.AsSpan(78, 4));

    private static byte[] WithCompression(byte[] mobi, ushort compression)
    {
        var copy = mobi.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(Record0(copy), 2), compression);
        return copy;
    }

    private static byte[] WithEncryption(byte[] mobi, ushort encryption)
    {
        var copy = mobi.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(Record0(copy) + 12, 2), encryption);
        return copy;
    }

    /// <summary>Set a bit in the FIB's flag word, which sits 10 bytes into the WordDocument stream.</summary>
    private static byte[] WithFibFlag(byte[] doc, ushort flag)
    {
        var copy = doc.ToArray();

        // The WordDocument stream starts at a sector boundary; find the FIB by
        // its 0xA5EC signature rather than re-walking the FAT here.
        for (var i = 0; i + 12 < copy.Length; i += 512)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(i, 2)) != 0xA5EC)
                continue;

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(i + 10, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(i + 10, 2), (ushort)(flags | flag));
            return copy;
        }

        throw new InvalidOperationException("No FIB signature found in the fixture.");
    }

    private static byte[] PlainZip()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("hello.txt").Open()))
        {
            writer.Write("not a book");
        }
        return ms.ToArray();
    }

    private static byte[] EpubWithoutContainer()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(
                archive.CreateEntry("mimetype", CompressionLevel.NoCompression).Open()))
            {
                writer.Write("application/epub+zip");
            }
            using (var writer = new StreamWriter(archive.CreateEntry("content.opf").Open()))
            {
                writer.Write("<package/>");
            }
        }
        return ms.ToArray();
    }
}
