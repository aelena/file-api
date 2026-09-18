using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Services.Common;
using AwesomeAssertions;
using Xunit;

namespace Aelena.FileApi.Tests.Services;

public class EmailServiceTests
{
    private static byte[] CreateSimpleEml(
        string subject = "Test Subject",
        string from = "sender@example.com",
        string to = "recipient@example.com",
        string body = "Hello, this is a test email.")
    {
        var eml = $"""
            From: {from}
            To: {to}
            Subject: {subject}
            Date: Mon, 18 Mar 2026 12:00:00 +0000
            Message-ID: <test@example.com>
            MIME-Version: 1.0
            Content-Type: text/plain; charset=utf-8

            {body}
            """;
        return Encoding.UTF8.GetBytes(eml);
    }

    [Fact]
    public void Parse_SimpleEml_ExtractsHeaders()
    {
        var data = CreateSimpleEml();
        var result = EmailService.Parse(data, "test.eml");

        result.Subject.Should().Be("Test Subject");
        result.FromAddress.Should().Contain("sender@example.com");
        result.To.Should().HaveCount(1);
        result.To![0].Should().Contain("recipient@example.com");
        result.BodyText.Should().Contain("Hello, this is a test email.");
        result.MessageId.Should().Be("test@example.com");
    }

    [Fact]
    public void Parse_EmlWithDate_ExtractsDate()
    {
        var data = CreateSimpleEml();
        var result = EmailService.Parse(data, "mail.eml");

        result.Date.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Parse_UnsupportedFormat_Throws()
    {
        FluentActions.Invoking(() => EmailService.Parse([], "file.txt"))
            .Should().Throw<FileApiException>()
            .Where(e => e.StatusCode == 400);
    }

    [Fact]
    public void Parse_MsgFormat_IsReadRatherThanRefused()
    {
        // This pinned the 501 that .msg answered while the README advertised
        // support for it. Parsing now goes through MsgService; an empty buffer
        // is a malformed file, which is a 422 like any other.
        FluentActions.Invoking(() => EmailService.Parse([], "file.msg"))
            .Should().Throw<FileApiException>()
            .Where(e => e.StatusCode == 422);
    }

    [Fact]
    public void Parse_MultipartEml_ExtractsTextAndHtml()
    {
        var eml = """
            From: sender@example.com
            To: recipient@example.com
            Subject: Multipart Test
            MIME-Version: 1.0
            Content-Type: multipart/alternative; boundary="boundary123"

            --boundary123
            Content-Type: text/plain; charset=utf-8

            Plain text body.
            --boundary123
            Content-Type: text/html; charset=utf-8

            <html><body><p>HTML body.</p></body></html>
            --boundary123--
            """;

        var result = EmailService.Parse(Encoding.UTF8.GetBytes(eml), "multi.eml");

        result.BodyText.Should().Contain("Plain text body");
        result.BodyHtml.Should().Contain("HTML body");
    }
}
