using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Aelena.FileApi.Core.Errors;
using Aelena.FileApi.Core.Models;
using Aelena.FileApi.Core.Services.Documents;

namespace Aelena.FileApi.Core.Services.Common;

/// <summary>
/// Reads Outlook <c>.msg</c> files, which are OLE2 compound files rather than
/// anything resembling RFC 5322.
/// <para>
/// A message's fields are individual streams named
/// <c>__substg1.0_&lt;tag&gt;&lt;type&gt;</c>, where the tag is the MAPI
/// property id and the type says how to decode the bytes — <c>001F</c> is
/// UTF-16, <c>001E</c> is 8-bit, <c>0102</c> is binary. Fixed-width properties
/// such as timestamps are not streams at all; they sit as 16-byte records in
/// <c>__properties_version1.0</c>. Recipients and attachments each get their
/// own storage, which is why reading this format needs a real directory walk
/// and not a scan for stream names.
/// </para>
/// <para>
/// This uses the <see cref="CompoundFile"/> reader written for legacy
/// <c>.doc</c>, so support costs no new dependency.
/// </para>
/// </summary>
internal static class MsgService
{
    // MAPI property ids, from the [MS-OXPROPS] property set.
    private const ushort PidSubject = 0x0037;
    private const ushort PidNormalizedSubject = 0x0E1D;
    private const ushort PidSenderName = 0x0C1A;
    private const ushort PidSenderEmail = 0x0C1F;
    private const ushort PidSenderSmtp = 0x5D01;
    private const ushort PidDisplayTo = 0x0E04;
    private const ushort PidDisplayCc = 0x0E03;
    private const ushort PidDisplayBcc = 0x0E02;
    private const ushort PidBody = 0x1000;
    private const ushort PidBodyHtml = 0x1013;
    private const ushort PidInternetMessageId = 0x1035;
    private const ushort PidInReplyToId = 0x1042;
    private const ushort PidClientSubmitTime = 0x0039;
    private const ushort PidDeliveryTime = 0x0E06;

    // Attachment and recipient properties.
    private const ushort PidAttachLongFilename = 0x3707;
    private const ushort PidAttachFilename = 0x3704;
    private const ushort PidAttachMimeTag = 0x370E;
    private const ushort PidAttachDataBinary = 0x3701;
    private const ushort PidAttachSize = 0x0E20;
    private const ushort PidRecipientSmtp = 0x39FE;
    private const ushort PidRecipientEmail = 0x3003;
    private const ushort PidRecipientName = 0x3001;
    private const ushort PidRecipientType = 0x0C15;

    // Property types, the low half of a stream's name.
    private const string TypeUnicode = "001F";
    private const string TypeAnsi = "001E";
    private const string TypeBinary = "0102";

    private const string SubstgPrefix = "__substg1.0_";
    private const string AttachPrefix = "__attach_version1.0_";
    private const string RecipPrefix = "__recip_version1.0_";
    private const string PropertiesStream = "__properties_version1.0";

    /// <summary>Parse a .msg file into the same shape a .eml parses into.</summary>
    /// <exception cref="FileApiException">422 when the file is not a readable message.</exception>
    public static EmailParseResponse Parse(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!CompoundFile.TryOpen(data, out var cfb, out var error))
        {
            throw new FileApiException(422,
                $"This .msg is not a readable compound file: {error}", title: "Invalid MSG");
        }

        var nodes = cfb!.Nodes;

        // Top-level message properties are the streams directly under the root.
        var top = nodes
            .Where(n => !n.IsStorage && !n.Path.Contains('/', StringComparison.Ordinal))
            .ToList();

        if (!top.Exists(n => n.Name.StartsWith(SubstgPrefix, StringComparison.Ordinal)))
        {
            throw new FileApiException(422,
                "This compound file carries no MAPI property streams, so it is not an Outlook message. "
                + "A .doc or .xls renamed to .msg would look like this.",
                title: "Invalid MSG");
        }

        string? Text(ushort id)
        {
            return ReadProperty(cfb, top, id);
        }

        var subject = Text(PidSubject) ?? Text(PidNormalizedSubject);
        var senderName = Text(PidSenderName);
        var senderEmail = Text(PidSenderSmtp) ?? Text(PidSenderEmail);

        var from = (senderName, senderEmail) switch
        {
            ({ Length: > 0 } n, { Length: > 0 } e) => $"{n} <{e}>",
            (_, { Length: > 0 } e) => e,
            ({ Length: > 0 } n, _) => n,
            _ => null
        };

        var (toList, ccList, bccList) = ReadRecipients(cfb, nodes);
        var properties = ReadPropertyTable(cfb, top);

        return new EmailParseResponse(
            FileName: fileName,
            Subject: subject,
            FromAddress: from,
            To: Merge(Split(Text(PidDisplayTo)), toList),
            Cc: Merge(Split(Text(PidDisplayCc)), ccList),
            Bcc: Merge(Split(Text(PidDisplayBcc)), bccList),
            Date: FileTime(properties, PidClientSubmitTime) ?? FileTime(properties, PidDeliveryTime),
            MessageId: Text(PidInternetMessageId),
            InReplyTo: Text(PidInReplyToId),
            BodyText: Text(PidBody),
            BodyHtml: Text(PidBodyHtml),
            Attachments: ReadAttachments(cfb, nodes));
    }

    // ── Properties ───────────────────────────────────────────────────────

    /// <summary>
    /// Read one property as text, trying each encoding the format allows. A
    /// message written by a Unicode-era Outlook stores <c>001F</c>; older ones
    /// and some gateways store <c>001E</c>; HTML bodies are often <c>0102</c>
    /// even though the content is text.
    /// </summary>
    private static string? ReadProperty(
        CompoundFile cfb, List<CompoundFile.CompoundFileNode> scope, ushort id)
    {
        foreach (var type in new[] { TypeUnicode, TypeAnsi, TypeBinary })
        {
            var name = SubstgPrefix + id.ToString("X4", CultureInfo.InvariantCulture) + type;
            var node = scope.Find(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (node is null) continue;

            var bytes = cfb.ReadStream(node);
            if (bytes is null or { Length: 0 }) continue;

            var text = type switch
            {
                TypeUnicode => Encoding.Unicode.GetString(bytes),
                TypeAnsi => Cp1252.GetString(bytes),
                _ => DecodeUnknown(bytes)
            };

            text = text.TrimEnd('\0');
            if (text.Length > 0) return text;
        }

        return null;
    }

    /// <summary>
    /// A binary property holding text may be UTF-16 or single-byte. Interleaved
    /// NULs are the tell for UTF-16, which is otherwise indistinguishable.
    /// </summary>
    private static string DecodeUnknown(byte[] bytes)
    {
        if (bytes.Length >= 4)
        {
            var nuls = 0;
            var sample = Math.Min(bytes.Length, 256);
            for (var i = 1; i < sample; i += 2)
                if (bytes[i] == 0) nuls++;

            if (nuls > sample / 4)
                return Encoding.Unicode.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// The fixed-width property table: a 32-byte header on a top-level message,
    /// then 16-byte records of type, id, flags and an 8-byte value.
    /// </summary>
    private static Dictionary<ushort, byte[]> ReadPropertyTable(
        CompoundFile cfb, List<CompoundFile.CompoundFileNode> scope)
    {
        var result = new Dictionary<ushort, byte[]>();

        var node = scope.Find(n =>
            n.Name.Equals(PropertiesStream, StringComparison.OrdinalIgnoreCase));
        if (node is null || cfb.ReadStream(node) is not { Length: > 32 } bytes)
            return result;

        for (var offset = 32; offset + 16 <= bytes.Length; offset += 16)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
            var id = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 2, 2));

            // 0x0040 is PT_SYSTIME; the rest are read on demand by id.
            if (type is 0x0040 or 0x0003 or 0x0014 or 0x000B)
                result[id] = bytes[(offset + 8)..(offset + 16)];
        }

        return result;
    }

    /// <summary>A Windows FILETIME, as an ISO 8601 string.</summary>
    private static string? FileTime(Dictionary<ushort, byte[]> properties, ushort id)
    {
        if (!properties.TryGetValue(id, out var bytes) || bytes.Length < 8)
            return null;

        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        if (ticks <= 0) return null;

        try
        {
            return DateTime.FromFileTimeUtc(ticks).ToString("o", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A corrupt or uninitialised timestamp is not worth failing a parse over.
            return null;
        }
    }

    // ── Recipients ───────────────────────────────────────────────────────

    /// <summary>
    /// Recipients from their own storages, which carry the real addresses.
    /// The display-to/cc/bcc strings hold names only, so both are read and
    /// merged.
    /// </summary>
    private static (List<string> To, List<string> Cc, List<string> Bcc) ReadRecipients(
        CompoundFile cfb, IReadOnlyList<CompoundFile.CompoundFileNode> nodes)
    {
        List<string> to = [], cc = [], bcc = [];

        foreach (var storage in nodes.Where(n =>
            n.IsStorage && n.Name.StartsWith(RecipPrefix, StringComparison.Ordinal)))
        {
            var scope = ChildrenOf(nodes, storage);

            var address = ReadProperty(cfb, scope, PidRecipientSmtp)
                       ?? ReadProperty(cfb, scope, PidRecipientEmail);
            var name = ReadProperty(cfb, scope, PidRecipientName);

            var display = (name, address) switch
            {
                ({ Length: > 0 } n, { Length: > 0 } a) when n != a => $"{n} <{a}>",
                (_, { Length: > 0 } a) => a,
                ({ Length: > 0 } n, _) => n,
                _ => null
            };

            if (display is null) continue;

            // PidTagRecipientType: 1 is To, 2 is Cc, 3 is Bcc.
            var type = ReadProperty(cfb, scope, PidRecipientType);
            var bucket = ReadPropertyTable(cfb, scope).TryGetValue(PidRecipientType, out var raw)
                && raw.Length >= 4
                    ? BinaryPrimitives.ReadInt32LittleEndian(raw)
                    : type is not null && int.TryParse(type, out var parsed) ? parsed : 1;

            (bucket switch { 2 => cc, 3 => bcc, _ => to }).Add(display);
        }

        return (to, cc, bcc);
    }

    // ── Attachments ──────────────────────────────────────────────────────

    private static List<EmailAttachment> ReadAttachments(
        CompoundFile cfb, IReadOnlyList<CompoundFile.CompoundFileNode> nodes)
    {
        var result = new List<EmailAttachment>();

        foreach (var storage in nodes.Where(n =>
            n.IsStorage && n.Name.StartsWith(AttachPrefix, StringComparison.Ordinal)))
        {
            var scope = ChildrenOf(nodes, storage);

            var name = ReadProperty(cfb, scope, PidAttachLongFilename)
                    ?? ReadProperty(cfb, scope, PidAttachFilename)
                    ?? "(unnamed)";

            var mime = ReadProperty(cfb, scope, PidAttachMimeTag) ?? "application/octet-stream";

            // The declared size includes MAPI overhead; the data stream's own
            // length is what the file actually weighs, so prefer it.
            var data = scope.Find(n => n.Name.StartsWith(
                SubstgPrefix + PidAttachDataBinary.ToString("X4", CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase));

            var size = data?.Size
                ?? (ReadPropertyTable(cfb, scope).TryGetValue(PidAttachSize, out var raw) && raw.Length >= 4
                    ? BinaryPrimitives.ReadInt32LittleEndian(raw)
                    : 0);

            result.Add(new EmailAttachment(name, mime, size));
        }

        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Streams directly inside one storage, by path prefix.</summary>
    private static List<CompoundFile.CompoundFileNode> ChildrenOf(
        IReadOnlyList<CompoundFile.CompoundFileNode> nodes, CompoundFile.CompoundFileNode storage)
    {
        var prefix = storage.Path + "/";

        return [.. nodes.Where(n =>
            !n.IsStorage
            && n.Path.StartsWith(prefix, StringComparison.Ordinal)
            && n.Path.IndexOf('/', prefix.Length) < 0)];
    }

    /// <summary>Outlook joins display names with semicolons.</summary>
    private static List<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// Prefer the recipient storages, which carry addresses, and fall back to
    /// the display string when a message has none — some senders strip them.
    /// </summary>
    private static List<string>? Merge(List<string> display, List<string> resolved)
    {
        if (resolved.Count > 0) return resolved;
        return display.Count > 0 ? display : null;
    }
}
