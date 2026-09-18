namespace Aelena.FileApi.Core.Models;

/// <summary>
/// What a text file is actually encoded as, and what is in it that should not be.
/// <para>
/// The control-byte report exists because the failure it catches is invisible:
/// a stray <c>0x08</c> or <c>0x1B</c> in a file that is otherwise valid UTF-8
/// will pass an encoding check, render as nothing in most editors, and be
/// rejected by whatever consumes it downstream.
/// </para>
/// </summary>
public sealed record TextEncodingResponse(
    string FileName,
    long FileSizeBytes,
    string Encoding,
    string Confidence,
    bool HasBom,
    string? Bom,
    bool IsValidUtf8,
    string? Utf8Error,
    string LineEnding,
    int CrlfCount,
    int LfCount,
    int CrCount,
    int LineCount,
    bool EndsWithNewline,
    bool HasControlBytes,
    IReadOnlyList<ControlByteHit> ControlBytes,
    bool HasReplacementChar);

/// <summary>One control byte found in a text file, located for the caller to fix.</summary>
public sealed record ControlByteHit(
    string Byte,
    string Name,
    int Offset,
    int Line,
    int Column,
    int Occurrences);
