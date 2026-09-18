using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Models;
using MimeKit;

namespace Aelena.FileApi.Core.Services.Common;

/// <summary>
/// Parses email files (.eml / .msg) into structured data.
/// EML (RFC 5322 / MIME) is handled natively via MimeKit; MSG (Outlook) is an
/// OLE2 compound file and is read by <see cref="MsgService"/> on top of the
/// same reader legacy .doc uses, so neither format costs an extra dependency.
/// </summary>
public static class EmailService
{
    /// <summary>
    /// Parse an email file and extract headers, body, and attachment metadata.
    /// </summary>
    /// <param name="data">Raw email file bytes.</param>
    /// <param name="fileName">Original file name (extension determines parser).</param>
    /// <returns>Structured email data.</returns>
    public static EmailParseResponse Parse(byte[] data, string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "eml" => ParseEml(data, fileName),
            "msg" => MsgService.Parse(data, fileName),
            _ => throw new FileApiException(400,
                $"Unsupported email format: .{ext}. Use .eml or .msg files.")
        };
    }

    /// <summary>Parse a .eml (RFC 5322 / MIME) file using MimeKit.</summary>
    private static EmailParseResponse ParseEml(byte[] data, string fileName)
    {
        MimeMessage message;
        try
        {
            using var stream = new MemoryStream(data);
            message = MimeMessage.Load(stream);
        }
        catch (Exception ex)
        {
            throw new FileApiException(400, $"Cannot parse EML file: {ex.Message}");
        }

        string? bodyText = null;
        string? bodyHtml = null;
        var attachments = new List<EmailAttachment>();

        // Extract body and attachments from MIME structure
        if (message.Body is Multipart multipart)
            WalkMultipart(multipart, ref bodyText, ref bodyHtml, attachments);
        else if (message.Body is TextPart textPart)
        {
            if (textPart.IsHtml) bodyHtml = textPart.Text;
            else bodyText = textPart.Text;
        }

        return new EmailParseResponse(
            FileName: fileName,
            Subject: message.Subject,
            FromAddress: message.From.ToString(),
            To: AddressList(message.To),
            Cc: AddressList(message.Cc),
            Bcc: AddressList(message.Bcc),
            Date: message.Date != default ? message.Date.ToString("o") : null,
            MessageId: message.MessageId,
            InReplyTo: message.InReplyTo,
            BodyText: bodyText,
            BodyHtml: bodyHtml,
            Attachments: attachments.Count > 0 ? attachments : null);
    }

    private static void WalkMultipart(
        Multipart multipart, ref string? bodyText, ref string? bodyHtml,
        List<EmailAttachment> attachments)
    {
        foreach (var part in multipart)
        {
            if (part is Multipart nested)
            {
                WalkMultipart(nested, ref bodyText, ref bodyHtml, attachments);
            }
            else if (part is MimePart mime && mime.IsAttachment)
            {
                using var ms = new MemoryStream();
                mime.Content?.DecodeTo(ms);
                attachments.Add(new EmailAttachment(
                    Filename: mime.FileName ?? "unnamed",
                    ContentType: mime.ContentType.MimeType,
                    SizeBytes: ms.Length));
            }
            else if (part is TextPart text)
            {
                if (text.IsHtml && bodyHtml is null)
                    bodyHtml = text.Text;
                else if (!text.IsHtml && bodyText is null)
                    bodyText = text.Text;
            }
        }
    }

    private static string[]? AddressList(InternetAddressList addresses) =>
        addresses.Count > 0 ? [.. addresses.Select(a => a.ToString())] : null;
}
