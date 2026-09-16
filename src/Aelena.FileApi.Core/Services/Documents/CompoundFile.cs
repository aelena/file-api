using System.Buffers.Binary;
using System.Text;

namespace Aelena.FileApi.Core.Services.Documents;

/// <summary>
/// A read-only reader for the OLE2 Compound File Binary format — the container
/// that holds a legacy <c>.doc</c>, <c>.xls</c>, <c>.ppt</c> or <c>.msg</c>.
/// <para>
/// The format is a FAT filesystem in a file: a header, a sector allocation
/// table, a directory of named streams, and a second smaller allocation table
/// for streams below the mini-stream cutoff. Only the parts needed to pull a
/// named stream out are implemented — no writing, no storages-within-storages
/// traversal beyond finding entries by name.
/// </para>
/// <para>
/// Every chain walk here is bounded by the sector count. A compound file that
/// arrives over HTTP can have a FAT whose entries point at each other in a
/// cycle, and an unbounded walk would hang the request thread rather than
/// returning a 422.
/// </para>
/// </summary>
internal sealed class CompoundFile
{
    /// <summary>
    /// Lowest reserved value; anything below it is a real sector number. The
    /// reserved range holds DIFSECT (0xFFFFFFFC), FATSECT (0xFFFFFFFD),
    /// ENDOFCHAIN (0xFFFFFFFE) and FREESECT (0xFFFFFFFF), all of which end a
    /// chain walk, so one comparison covers them.
    /// </summary>
    private const uint MaxRegularSector = 0xFFFFFFFA;

    private const int DirectoryEntrySize = 128;

    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly int _miniCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly List<Entry> _entries;
    private readonly byte[] _miniStream;

    private CompoundFile(
        byte[] data, int sectorSize, int miniSectorSize, int miniCutoff,
        uint[] fat, uint[] miniFat, List<Entry> entries, byte[] miniStream)
    {
        _data = data;
        _sectorSize = sectorSize;
        _miniSectorSize = miniSectorSize;
        _miniCutoff = miniCutoff;
        _fat = fat;
        _miniFat = miniFat;
        _entries = entries;
        _miniStream = miniStream;
    }

    /// <summary>Names of every stream in the file, in directory order.</summary>
    public IEnumerable<string> StreamNames => _entries.Where(e => e.Type == 2).Select(e => e.Name);

    /// <summary>
    /// Open a compound file. Returns false with a caller-facing reason rather
    /// than throwing, because every failure here is a malformed upload.
    /// </summary>
    public static bool TryOpen(byte[] data, out CompoundFile? file, out string error)
    {
        file = null;
        error = "";

        if (data.Length < 512)
        {
            error = $"File is {data.Length} bytes, shorter than the 512-byte compound file header.";
            return false;
        }

        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(30, 2));
        var miniSectorShift = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(32, 2));

        if (sectorShift is not (9 or 12))
        {
            error = $"Header declares a sector shift of {sectorShift}; only 9 (512 bytes) and 12 (4096) are valid.";
            return false;
        }

        if (miniSectorShift != 6)
        {
            error = $"Header declares a mini sector shift of {miniSectorShift}; only 6 (64 bytes) is valid.";
            return false;
        }

        var sectorSize = 1 << sectorShift;
        var miniSectorSize = 1 << miniSectorShift;
        var totalSectors = (data.Length - 512) / sectorSize;

        if (totalSectors <= 0)
        {
            error = "The file holds no sectors after its header.";
            return false;
        }

        var miniCutoff = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(56, 4));
        if (miniCutoff <= 0) miniCutoff = 4096;

        var fat = ReadFat(data, sectorSize, totalSectors, out var fatError);
        if (fat is null)
        {
            error = fatError;
            return false;
        }

        var dirStart = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48, 4));
        var directory = ReadChain(data, fat, sectorSize, dirStart, limit: int.MaxValue);
        if (directory.Length < DirectoryEntrySize)
        {
            error = "The directory chain is empty, so the file names no streams.";
            return false;
        }

        var entries = ReadDirectory(directory);
        if (entries.Count == 0 || entries[0].Type != 5)
        {
            error = "The first directory entry is not a root storage entry.";
            return false;
        }

        // The mini stream is itself a stream, held by the root entry and read
        // through the ordinary FAT. Mini sectors are then carved out of it.
        var root = entries[0];
        var miniStream = root.Size > 0
            ? ReadChain(data, fat, sectorSize, root.StartSector, limit: (int)Math.Min(root.Size, int.MaxValue))
            : [];

        var miniFatStart = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(60, 4));
        var miniFatBytes = ReadChain(data, fat, sectorSize, miniFatStart, limit: int.MaxValue);
        var miniFat = new uint[miniFatBytes.Length / 4];
        for (var i = 0; i < miniFat.Length; i++)
            miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i * 4, 4));

        file = new CompoundFile(
            data, sectorSize, miniSectorSize, miniCutoff, fat, miniFat, entries, miniStream);
        return true;
    }

    /// <summary>Read a named stream, or null when the file has no such stream.</summary>
    public byte[]? ReadStream(string name)
    {
        var entry = _entries.Find(e => e.Type == 2 && e.Name == name);
        if (entry is null)
            return null;

        var size = (int)Math.Min(entry.Size, int.MaxValue);
        if (size == 0)
            return [];

        // Below the cutoff a stream lives in the mini stream, above it in the
        // ordinary sectors. Getting this the wrong way round is how a small
        // table stream comes back as unrelated bytes rather than as an error.
        return size < _miniCutoff
            ? ReadMiniChain(entry.StartSector, size)
            : ReadChain(_data, _fat, _sectorSize, entry.StartSector, size);
    }

    // ── Allocation tables ────────────────────────────────────────────────

    private static uint[]? ReadFat(byte[] data, int sectorSize, int totalSectors, out string error)
    {
        error = "";

        var fatSectorCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44, 4));
        if (fatSectorCount < 0 || fatSectorCount > totalSectors)
        {
            error = $"Header declares {fatSectorCount} FAT sectors, more than the {totalSectors} the file holds.";
            return null;
        }

        var perSector = sectorSize / 4;
        var fatSectors = new List<uint>(fatSectorCount);

        // The first 109 FAT sector numbers are in the header itself.
        for (var i = 0; i < 109 && fatSectors.Count < fatSectorCount; i++)
        {
            var sector = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(76 + i * 4, 4));
            if (sector >= MaxRegularSector) break;
            fatSectors.Add(sector);
        }

        // Anything beyond that is chained through DIFAT sectors, each of which
        // ends with a pointer to the next.
        var difat = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(68, 4));
        var difatSeen = 0;

        while (difat < MaxRegularSector && fatSectors.Count < fatSectorCount)
        {
            if (++difatSeen > totalSectors)
            {
                error = "The DIFAT chain does not terminate; the file's allocation table is corrupt.";
                return null;
            }

            var offset = SectorOffset(difat, sectorSize);
            if (offset < 0 || offset + sectorSize > data.Length)
            {
                error = $"A DIFAT sector points to byte {offset}, outside the file.";
                return null;
            }

            for (var i = 0; i < perSector - 1 && fatSectors.Count < fatSectorCount; i++)
            {
                var sector = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + i * 4, 4));
                if (sector >= MaxRegularSector) continue;
                fatSectors.Add(sector);
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + (perSector - 1) * 4, 4));
        }

        var fat = new uint[fatSectors.Count * perSector];
        var index = 0;

        foreach (var sector in fatSectors)
        {
            var offset = SectorOffset(sector, sectorSize);
            if (offset < 0 || offset + sectorSize > data.Length)
            {
                error = $"A FAT sector points to byte {offset}, outside the file.";
                return null;
            }

            for (var i = 0; i < perSector; i++)
                fat[index++] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + i * 4, 4));
        }

        return fat;
    }

    private static byte[] ReadChain(byte[] data, uint[] fat, int sectorSize, uint start, int limit)
    {
        if (start >= MaxRegularSector || limit <= 0)
            return [];

        var output = new MemoryStream();
        var sector = start;
        var steps = 0;

        while (sector < MaxRegularSector && output.Length < limit)
        {
            // The chain cannot be longer than the table it indexes; anything
            // more means a loop.
            if (++steps > fat.Length + 1)
                break;

            var offset = SectorOffset(sector, sectorSize);
            if (offset < 0 || offset + sectorSize > data.Length)
                break;

            var take = (int)Math.Min(sectorSize, limit - output.Length);
            output.Write(data, offset, take);

            if (sector >= fat.Length)
                break;

            sector = fat[sector];
        }

        return output.ToArray();
    }

    private byte[] ReadMiniChain(uint start, int size)
    {
        if (start >= MaxRegularSector)
            return [];

        var output = new MemoryStream(size);
        var sector = start;
        var steps = 0;

        while (sector < MaxRegularSector && output.Length < size)
        {
            if (++steps > _miniFat.Length + 1)
                break;

            var offset = (int)sector * _miniSectorSize;
            if (offset < 0 || offset + _miniSectorSize > _miniStream.Length)
                break;

            var take = (int)Math.Min(_miniSectorSize, size - output.Length);
            output.Write(_miniStream, offset, take);

            if (sector >= _miniFat.Length)
                break;

            sector = _miniFat[sector];
        }

        return output.ToArray();
    }

    // ── Directory ────────────────────────────────────────────────────────

    private static List<Entry> ReadDirectory(byte[] directory)
    {
        var entries = new List<Entry>(directory.Length / DirectoryEntrySize);

        for (var offset = 0; offset + DirectoryEntrySize <= directory.Length; offset += DirectoryEntrySize)
        {
            var type = directory[offset + 66];
            if (type is not (1 or 2 or 5))
                continue;

            var nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 64, 2));
            // The declared length counts the terminating NUL, which is not part
            // of the name, and cannot exceed the 64-byte field.
            nameBytes = (ushort)Math.Clamp(nameBytes - 2, 0, 64);

            var name = Encoding.Unicode.GetString(directory, offset, nameBytes);
            var start = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 116, 4));
            var size = BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(offset + 120, 8));

            entries.Add(new Entry(name, type, start, size));
        }

        return entries;
    }

    private static int SectorOffset(uint sector, int sectorSize)
    {
        // Sector 0 starts immediately after the 512-byte header, which occupies
        // the whole of the first sector whatever the sector size is.
        var offset = ((long)sector + 1) * sectorSize;
        return offset > int.MaxValue ? -1 : (int)offset;
    }

    private sealed record Entry(string Name, byte Type, uint StartSector, ulong Size);
}
