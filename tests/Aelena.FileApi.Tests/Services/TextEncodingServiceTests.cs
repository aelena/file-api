using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Services.Common;
using AwesomeAssertions;
using Xunit;

namespace Aelena.FileApi.Tests.Services;

/// <summary>
/// Encoding and control-byte detection. The control-byte case is not
/// hypothetical: a stray <c>0x08</c> in a README shipped in this repository and
/// was only caught when nuget.org rejected the package.
/// </summary>
public class TextEncodingServiceTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // ── Encoding ─────────────────────────────────────────────────────────

    [Fact]
    public void Detect_PlainAscii_IsCertain()
    {
        var result = TextEncodingService.Detect(Utf8("hello world\n"), "a.txt");

        result.Encoding.Should().Be("us-ascii");
        result.Confidence.Should().Be("certain");
        result.IsValidUtf8.Should().BeTrue();
        result.HasBom.Should().BeFalse();
    }

    [Fact]
    public void Detect_Utf8WithoutBom_IsReportedAsUtf8()
    {
        var result = TextEncodingService.Detect(Utf8("café crème — 5 €\n"), "a.txt");

        result.Encoding.Should().Be("utf-8");
        result.IsValidUtf8.Should().BeTrue();
        result.HasBom.Should().BeFalse();
    }

    [Fact]
    public void Detect_Utf8Bom_IsNamed()
    {
        var data = Encoding.UTF8.GetPreamble().Concat(Utf8("x")).ToArray();

        var result = TextEncodingService.Detect(data, "a.txt");

        result.HasBom.Should().BeTrue();
        result.Bom.Should().Be("UTF-8");
        result.Encoding.Should().Be("utf-8");
        result.Confidence.Should().Be("certain");
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x78, 0x00 }, "UTF-16 LE")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x78 }, "UTF-16 BE")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x78, 0x00, 0x00, 0x00 }, "UTF-32 LE")]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x78 }, "UTF-32 BE")]
    public void Detect_EveryByteOrderMark(byte[] data, string expected)
    {
        // UTF-32 LE opens with the UTF-16 LE mark, so order of testing matters.
        TextEncodingService.Detect(data, "a.txt").Bom.Should().Be(expected);
    }

    [Fact]
    public void Detect_InvalidUtf8_SaysWhichByteAndWhere()
    {
        // 0xC3 starts a two-byte sequence; 0x28 cannot continue it.
        var result = TextEncodingService.Detect([0x61, 0xC3, 0x28, 0x62], "a.txt");

        result.IsValidUtf8.Should().BeFalse();
        result.Utf8Error.Should().Contain("0x28").And.Contain("offset 2");
        result.Encoding.Should().Be("unknown");
        result.Confidence.Should().Be("low");
    }

    [Fact]
    public void Detect_TruncatedSequence_IsReported()
    {
        var result = TextEncodingService.Detect([0x61, 0xE2, 0x82], "a.txt");

        result.IsValidUtf8.Should().BeFalse();
        result.Utf8Error.Should().Contain("truncated");
    }

    // ── Line endings ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("a\nb\n", "lf", 0, 2, 0)]
    [InlineData("a\r\nb\r\n", "crlf", 2, 0, 0)]
    [InlineData("a\rb\r", "cr", 0, 0, 2)]
    [InlineData("a\r\nb\n", "mixed", 1, 1, 0)]
    [InlineData("no newline", "none", 0, 0, 0)]
    public void Detect_LineEndings(string text, string expected, int crlf, int lf, int cr)
    {
        var result = TextEncodingService.Detect(Utf8(text), "a.txt");

        result.LineEnding.Should().Be(expected);
        result.CrlfCount.Should().Be(crlf);
        result.LfCount.Should().Be(lf);
        result.CrCount.Should().Be(cr);
    }

    [Fact]
    public void Detect_CrlfIsNotCountedAsBothCrAndLf()
    {
        var result = TextEncodingService.Detect(Utf8("a\r\nb"), "a.txt");

        result.CrlfCount.Should().Be(1);
        result.LfCount.Should().Be(0);
        result.CrCount.Should().Be(0);
    }

    // ── Control bytes ────────────────────────────────────────────────────

    [Fact]
    public void Detect_ControlByte_IsLocatedByLineAndColumn()
    {
        // This is the 0.4.1 failure, reproduced: a backspace inside otherwise
        // valid UTF-8, invisible in an editor, fatal at the far end.
        var data = Utf8("first line\nsecond \u0008line\n");

        var result = TextEncodingService.Detect(data, "README.md");

        result.IsValidUtf8.Should().BeTrue();
        result.HasControlBytes.Should().BeTrue();

        var hit = result.ControlBytes.Should().ContainSingle().Which;
        hit.Byte.Should().Be("0x08");
        hit.Name.Should().Be("BS");
        hit.Line.Should().Be(2);
        hit.Column.Should().Be(8);
        hit.Occurrences.Should().Be(1);
    }

    [Fact]
    public void Detect_RepeatedControlByte_IsCountedOnce()
    {
        var result = TextEncodingService.Detect(Utf8("a\u0008b\u0008c\u0008"), "a.txt");

        var hit = result.ControlBytes.Should().ContainSingle().Which;
        hit.Occurrences.Should().Be(3);
    }

    [Theory]
    [InlineData((byte)0x00, "NUL")]
    [InlineData((byte)0x1B, "ESC")]
    [InlineData((byte)0x7F, "DEL")]
    [InlineData((byte)0x0C, "FF")]
    public void Detect_ControlBytesAreNamed(byte value, string name)
    {
        var result = TextEncodingService.Detect([0x61, value, 0x62], "a.txt");

        result.ControlBytes.Should().ContainSingle().Which.Name.Should().Be(name);
    }

    [Fact]
    public void Detect_TabAndNewlineAreNotControlBytes()
    {
        var result = TextEncodingService.Detect(Utf8("a\tb\nc\r\nd"), "a.txt");

        result.HasControlBytes.Should().BeFalse();
        result.ControlBytes.Should().BeEmpty();
    }

    [Fact]
    public void Detect_ReplacementCharacter_IsFlagged()
    {
        var result = TextEncodingService.Detect(Utf8("mojibake � here"), "a.txt");

        result.HasReplacementChar.Should().BeTrue();
    }

    // ── Normalisation ────────────────────────────────────────────────────

    [Fact]
    public void Normalise_ConvertsCrlfToLfAndDropsTheBom()
    {
        var data = Encoding.UTF8.GetPreamble().Concat(Utf8("a\r\nb\r\n")).ToArray();

        var (_, bytes) = TextEncodingService.Normalise(data, "a.txt");

        bytes.Should().StartWith("a"u8.ToArray());
        Encoding.UTF8.GetString(bytes).Should().Be("a\nb\n");
    }

    [Fact]
    public void Normalise_ToCrlf_DoesNotDoubleUpExistingCrlf()
    {
        var (_, bytes) = TextEncodingService.Normalise(Utf8("a\r\nb\nc"), "a.txt", lineEnding: "crlf");

        Encoding.UTF8.GetString(bytes).Should().Be("a\r\nb\r\nc");
    }

    [Fact]
    public void Normalise_StripsControlBytes()
    {
        var (_, bytes) = TextEncodingService.Normalise(Utf8("clean\u0008er\ttext\n"), "a.txt");

        Encoding.UTF8.GetString(bytes).Should().Be("cleaner\ttext\n");
    }

    [Fact]
    public void Normalise_KeepsControlBytesWhenAsked()
    {
        var (_, bytes) = TextEncodingService.Normalise(
            Utf8("a\u0008b"), "a.txt", stripControls: false);

        bytes.Should().Contain((byte)0x08);
    }

    [Fact]
    public void Normalise_KeepBoth_LeavesLineEndingsAlone()
    {
        var (_, bytes) = TextEncodingService.Normalise(Utf8("a\r\nb\nc\r"), "a.txt", lineEnding: "keep");

        Encoding.UTF8.GetString(bytes).Should().Be("a\r\nb\nc\r");
    }

    [Fact]
    public void Normalise_UnknownLineEnding_IsBadRequest()
    {
        var ex = FluentActions.Invoking(() =>
            TextEncodingService.Normalise(Utf8("a"), "a.txt", lineEnding: "wobbly"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(400);
        ex.Detail.Should().Contain("lf, crlf, cr or keep");
    }

    [Fact]
    public void Normalise_UndecodableInput_Is422AndPointsAtDetection()
    {
        var ex = FluentActions.Invoking(() =>
            TextEncodingService.Normalise([0x61, 0xC3, 0x28], "a.txt"))
            .Should().Throw<FileApiException>().Which;

        ex.StatusCode.Should().Be(422);
        ex.Detail.Should().Contain("/txt/detect-encoding");
    }

    [Fact]
    public void Normalise_RoundTripsThroughDetectionClean()
    {
        var dirty = Utf8("a\u0008b\r\nc\u001B\r\n");

        var (_, clean) = TextEncodingService.Normalise(dirty, "a.txt");
        var result = TextEncodingService.Detect(clean, "a.txt");

        result.HasControlBytes.Should().BeFalse();
        result.LineEnding.Should().Be("lf");
        result.HasBom.Should().BeFalse();
    }
}
