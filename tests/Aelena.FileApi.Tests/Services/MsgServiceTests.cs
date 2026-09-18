using System.Buffers.Binary;
using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Services.Common;
using AwesomeAssertions;
using Xunit;

namespace Aelena.FileApi.Tests.Services;

/// <summary>
/// Outlook <c>.msg</c> parsing, through the public <see cref="EmailService"/>
/// entry point.
/// <para>
/// The fixtures are built by <see cref="CompoundFileBuilder"/> rather than by
/// Outlook, which could not be automated on the build machine. That is a real
/// limitation and is stated plainly: these tests prove the MAPI property and
/// storage handling against a container written from the specification, not
/// against Outlook's own output. The container layer itself is separately
/// exercised against genuine Microsoft bytes — <c>public-domain-pieces.doc</c>
/// was written by Word and is read by the same reader.
/// </para>
/// </summary>
public class MsgServiceTests
{
    private const string Substg = "__substg1.0_";

    private static byte[] Unicode(string value) => Encoding.Unicode.GetBytes(value);

    /// <summary>The attachment payload, so its length can be asserted rather than guessed.</summary>
    private static readonly byte[] AttachmentBody =
        Encoding.UTF8.GetBytes("Sonnet XVIII, William Shakespeare, 1609.");

    /// <summary>A stream name for a property held as UTF-16.</summary>
    private static string Prop(ushort id) => $"{Substg}{id:X4}001F";

    /// <summary>A minimal but well-formed message with recipients and an attachment.</summary>
    private static byte[] Message()
    {
        var builder = new CompoundFileBuilder()
            .AddStream(Prop(0x0037), Unicode("Three public domain pieces"))
            .AddStream(Prop(0x0C1A), Unicode("Ada Lovelace"))
            .AddStream(Prop(0x5D01), Unicode("ada@example.org"))
            .AddStream(Prop(0x0E04), Unicode("Reader; Archivist"))
            .AddStream(Prop(0x1000), Unicode("Shall I compare thee to a summer's day?"))
            .AddStream(Prop(0x1035), Unicode("<abc123@example.org>"))
            .AddStream("__properties_version1.0", TopLevelProperties())

            .AddStream($"__recip_version1.0_#00000000/{Prop(0x3001)}", Unicode("Reader"))
            .AddStream($"__recip_version1.0_#00000000/{Prop(0x39FE)}", Unicode("reader@example.org"))
            .AddStream("__recip_version1.0_#00000000/__properties_version1.0", RecipientType(1))

            .AddStream($"__recip_version1.0_#00000001/{Prop(0x3001)}", Unicode("Archivist"))
            .AddStream($"__recip_version1.0_#00000001/{Prop(0x39FE)}", Unicode("archivist@example.org"))
            .AddStream("__recip_version1.0_#00000001/__properties_version1.0", RecipientType(2))

            .AddStream($"__attach_version1.0_#00000000/{Prop(0x3707)}", Unicode("sonnet.txt"))
            .AddStream($"__attach_version1.0_#00000000/{Prop(0x370E)}", Unicode("text/plain"))
            .AddStream("__attach_version1.0_#00000000/__substg1.0_37010102", AttachmentBody);

        return builder.Build();
    }

    /// <summary>
    /// The fixed-width property table: 32 bytes of header, then 16-byte records
    /// of type, id, flags and value. A submit time lives here, not in a stream.
    /// </summary>
    private static byte[] TopLevelProperties()
    {
        var buffer = new byte[32 + 16];
        var submitted = new DateTime(2026, 9, 18, 9, 30, 0, DateTimeKind.Utc).ToFileTimeUtc();

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(32, 2), 0x0040);   // PT_SYSTIME
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(34, 2), 0x0039);   // ClientSubmitTime
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(40, 8), submitted);

        return buffer;
    }

    private static byte[] RecipientType(int type)
    {
        var buffer = new byte[32 + 16];

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(32, 2), 0x0003);   // PT_LONG
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(34, 2), 0x0C15);   // RecipientType
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40, 4), type);

        return buffer;
    }

    // ── Headers and body ─────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsSubjectSenderAndBody()
    {
        var result = EmailService.Parse(Message(), "mail.msg");

        result.FileName.Should().Be("mail.msg");
        result.Subject.Should().Be("Three public domain pieces");
        result.FromAddress.Should().Be("Ada Lovelace <ada@example.org>");
        result.BodyText.Should().Contain("summer's day");
        result.MessageId.Should().Be("<abc123@example.org>");
    }

    [Fact]
    public void Parse_ResolvesRecipientsFromTheirOwnStorages()
    {
        // The display-to string holds names only; the addresses are in the
        // recipient storages, and a scan by stream name would find only the
        // first of them.
        var result = EmailService.Parse(Message(), "mail.msg");

        result.To.Should().ContainSingle().Which.Should().Be("Reader <reader@example.org>");
        result.Cc.Should().ContainSingle().Which.Should().Be("Archivist <archivist@example.org>");
    }

    [Fact]
    public void Parse_ReadsTheSubmitTimeFromThePropertyTable()
    {
        var result = EmailService.Parse(Message(), "mail.msg");

        result.Date.Should().NotBeNull();
        DateTime.Parse(result.Date!, System.Globalization.CultureInfo.InvariantCulture)
            .ToUniversalTime().Should().BeCloseTo(
                new DateTime(2026, 9, 18, 9, 30, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Parse_ListsAttachmentsWithTheirRealSize()
    {
        var result = EmailService.Parse(Message(), "mail.msg");

        var attachment = result.Attachments.Should().ContainSingle().Which;
        attachment.Filename.Should().Be("sonnet.txt");
        attachment.ContentType.Should().Be("text/plain");

        // The data stream's own length, not the size MAPI declares — the
        // declared one includes per-attachment overhead.
        attachment.SizeBytes.Should().Be(AttachmentBody.Length);
    }

    // ── Encodings ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_FallsBackToTheEightBitPropertyWhenThereIsNoUnicodeOne()
    {
        // Older senders and some gateways write 001E rather than 001F.
        var data = new CompoundFileBuilder()
            .AddStream("__substg1.0_0037001E", Encoding.Latin1.GetBytes("Ansi subject"))
            .AddStream("__substg1.0_1000001E", Encoding.Latin1.GetBytes("Ansi body"))
            .Build();

        var result = EmailService.Parse(data, "old.msg");

        result.Subject.Should().Be("Ansi subject");
        result.BodyText.Should().Be("Ansi body");
    }

    [Fact]
    public void Parse_FallsBackToTheNormalizedSubject()
    {
        var data = new CompoundFileBuilder()
            .AddStream(Prop(0x0E1D), Unicode("Normalized only"))
            .Build();

        EmailService.Parse(data, "x.msg").Subject.Should().Be("Normalized only");
    }

    [Fact]
    public void Parse_SenderWithNoDisplayNameUsesTheAddress()
    {
        var data = new CompoundFileBuilder()
            .AddStream(Prop(0x0037), Unicode("s"))
            .AddStream(Prop(0x0C1F), Unicode("bare@example.org"))
            .Build();

        EmailService.Parse(data, "x.msg").FromAddress.Should().Be("bare@example.org");
    }

    [Fact]
    public void Parse_DisplayStringIsUsedWhenThereAreNoRecipientStorages()
    {
        var data = new CompoundFileBuilder()
            .AddStream(Prop(0x0037), Unicode("s"))
            .AddStream(Prop(0x0E04), Unicode("Alice; Bob"))
            .Build();

        EmailService.Parse(data, "x.msg").To.Should().Equal("Alice", "Bob");
    }

    // ── Refusals ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_CompoundFileThatIsNotAMessage_Is422AndSaysWhat()
    {
        // A real Word document: valid OLE2, no MAPI properties anywhere.
        var doc = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "SampleFiles", "public-domain-pieces.doc"));

        var ex = FluentActions.Invoking(() => EmailService.Parse(doc, "actually-a-doc.msg"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(422);
        ex.Detail.Should().Contain("not an Outlook message");
    }

    [Fact]
    public void Parse_NotACompoundFileAtAll_Is422()
    {
        var ex = FluentActions.Invoking(() =>
            EmailService.Parse(Encoding.UTF8.GetBytes("plain text"), "x.msg"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(422);
        ex.Title.Should().Be("Invalid MSG");
    }

    [Fact]
    public void Parse_NoLongerReturns501()
    {
        // The route advertised .msg support and answered 501 until this shipped.
        FluentActions.Invoking(() => EmailService.Parse(Message(), "mail.msg"))
            .Should().NotThrow();
    }
}
