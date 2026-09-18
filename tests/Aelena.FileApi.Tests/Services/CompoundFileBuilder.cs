using System.Buffers.Binary;
using System.Text;

namespace Aelena.FileApi.Tests.Services;

/// <summary>
/// Writes OLE2 compound files, so the readers built on that format can be
/// tested against bytes rather than against mocks.
/// <para>
/// This exists because Outlook could not be automated on the build machine to
/// produce a genuine <c>.msg</c>. It is a deliberate second implementation of
/// the container from the specification — sector table, mini stream, mini FAT,
/// directory tree — written independently of
/// <c>CompoundFile</c>, which reads it. Two implementations agreeing is worth
/// considerably more than one implementation agreeing with itself.
/// </para>
/// <para>
/// The container layer is also checked against real Microsoft output elsewhere:
/// <c>public-domain-pieces.doc</c> was written by Word and is read by the same
/// <c>CompoundFile</c>.
/// </para>
/// </summary>
public sealed class CompoundFileBuilder
{
    private const int SectorSize = 512;
    private const int MiniSectorSize = 64;
    private const int MiniCutoff = 4096;
    private const int DirectoryEntrySize = 128;

    private const uint Fatsect = 0xFFFFFFFD;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint Free = 0xFFFFFFFF;
    private const uint NoStream = 0xFFFFFFFF;

    private readonly Node _root = new("Root Entry", IsStorage: true);

    /// <summary>Add a stream at a slash-separated path, creating storages as needed.</summary>
    public CompoundFileBuilder AddStream(string path, byte[] data)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parent = _root;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            var existing = parent.Children.Find(c => c.Name == parts[i] && c.IsStorage);
            if (existing is null)
            {
                existing = new Node(parts[i], IsStorage: true);
                parent.Children.Add(existing);
            }
            parent = existing;
        }

        parent.Children.Add(new Node(parts[^1], IsStorage: false) { Data = data });
        return this;
    }

    /// <summary>Add a storage that holds nothing, which is legal and worth testing.</summary>
    public CompoundFileBuilder AddStorage(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parent = _root;

        foreach (var part in parts)
        {
            var existing = parent.Children.Find(c => c.Name == part && c.IsStorage);
            if (existing is null)
            {
                existing = new Node(part, IsStorage: true);
                parent.Children.Add(existing);
            }
            parent = existing;
        }

        return this;
    }

    /// <summary>Serialise the whole container.</summary>
    public byte[] Build()
    {
        // Flatten into directory order: the root first, then every node in the
        // order its parent lists them.
        var entries = new List<Node> { _root };
        Flatten(_root, entries);

        // Siblings are written as a right-leaning chain. A balanced red-black
        // tree is not required to be readable — only a consistent one — and a
        // chain keeps this builder honest about what it is testing.
        foreach (var node in entries)
        {
            for (var i = 0; i < node.Children.Count; i++)
            {
                node.Children[i].Right = i + 1 < node.Children.Count
                    ? (uint)entries.IndexOf(node.Children[i + 1])
                    : NoStream;
            }

            node.Child = node.Children.Count > 0 ? (uint)entries.IndexOf(node.Children[0]) : NoStream;
        }

        // Small streams go into the mini stream; large ones get their own sectors.
        var mini = new MemoryStream();
        var miniChains = new List<uint>();
        var large = new List<Node>();

        foreach (var node in entries)
        {
            if (node.IsStorage || node.Data.Length == 0) continue;

            if (node.Data.Length < MiniCutoff)
            {
                node.Start = (uint)(mini.Length / MiniSectorSize);
                var sectors = Ceil(node.Data.Length, MiniSectorSize);

                for (var i = 0; i < sectors; i++)
                {
                    var index = (uint)(node.Start + i);
                    miniChains.Add(i + 1 < sectors ? index + 1 : EndOfChain);
                }

                mini.Write(node.Data);
                Pad(mini, MiniSectorSize);
            }
            else
            {
                large.Add(node);
            }
        }

        var miniStream = mini.ToArray();
        var miniFatBytes = Flatten32(miniChains);

        // The directory's size is known from the entry count, but its bytes
        // cannot be written until the root entry points at the mini stream —
        // so reserve the space now and serialise it further down.
        var directoryLength =
            Math.Max(1, Ceil(entries.Count * DirectoryEntrySize, SectorSize)) * SectorSize;

        // Sector 0 is the FAT; everything else follows in a fixed order so the
        // chains can be computed once the sizes are known.
        uint next = 1;

        uint Allocate(byte[] data)
        {
            if (data.Length == 0) return EndOfChain;
            var first = next;
            next += (uint)Ceil(data.Length, SectorSize);
            return first;
        }

        var miniFatStart = Allocate(miniFatBytes);
        var miniStreamStart = Allocate(miniStream);
        var directoryStart = Allocate(new byte[directoryLength]);
        foreach (var node in large) node.Start = Allocate(node.Data);

        // The root entry owns the mini stream. Set this before serialising the
        // directory, or every small stream reads back as zero bytes.
        _root.Start = miniStreamStart;
        _root.Size = miniStream.Length;

        var directory = BuildDirectory(entries);

        var totalSectors = (int)next;
        var fat = new uint[SectorSize / 4];
        Array.Fill(fat, Free);
        fat[0] = Fatsect;

        void Chain(uint start, int length)
        {
            if (length == 0 || start >= Fatsect) return;
            var count = Ceil(length, SectorSize);
            for (var i = 0; i < count; i++)
                fat[start + i] = i + 1 < count ? start + (uint)i + 1 : EndOfChain;
        }

        Chain(miniFatStart, miniFatBytes.Length);
        Chain(miniStreamStart, miniStream.Length);
        Chain(directoryStart, directory.Length);
        foreach (var node in large) Chain(node.Start, node.Data.Length);

        // ── Emit ─────────────────────────────────────────────────────────
        var output = new MemoryStream();
        output.Write(BuildHeader(
            fatSector: 0,
            directoryStart: directoryStart,
            miniFatStart: miniFatStart,
            miniFatSectors: Ceil(miniFatBytes.Length, SectorSize)));

        var body = new MemoryStream();
        WriteAt(body, 0, Flatten32([.. fat]));
        WriteAt(body, (int)miniFatStart * SectorSize, miniFatBytes);
        WriteAt(body, (int)miniStreamStart * SectorSize, miniStream);
        WriteAt(body, (int)directoryStart * SectorSize, directory);
        foreach (var node in large)
            WriteAt(body, (int)node.Start * SectorSize, node.Data);

        var bodyBytes = body.ToArray();
        Array.Resize(ref bodyBytes, totalSectors * SectorSize);
        output.Write(bodyBytes);

        return output.ToArray();
    }

    // ── Pieces ───────────────────────────────────────────────────────────

    private static void Flatten(Node parent, List<Node> into)
    {
        foreach (var child in parent.Children)
        {
            into.Add(child);
            if (child.IsStorage) Flatten(child, into);
        }
    }

    private static byte[] BuildDirectory(List<Node> entries)
    {
        var sectors = Math.Max(1, Ceil(entries.Count * DirectoryEntrySize, SectorSize));
        var buffer = new byte[sectors * SectorSize];

        // Unused directory slots must read as free rather than as garbage.
        for (var i = entries.Count; i < buffer.Length / DirectoryEntrySize; i++)
        {
            var slot = i * DirectoryEntrySize;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(slot + 68, 4), NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(slot + 72, 4), NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(slot + 76, 4), NoStream);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var node = entries[i];
            var offset = i * DirectoryEntrySize;

            var name = Encoding.Unicode.GetBytes(node.Name);
            name.CopyTo(buffer, offset);
            BinaryPrimitives.WriteUInt16LittleEndian(
                buffer.AsSpan(offset + 64, 2), (ushort)(name.Length + 2));

            buffer[offset + 66] = (byte)(i == 0 ? 5 : node.IsStorage ? 1 : 2);
            buffer[offset + 67] = 1;   // black

            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 68, 4), NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 72, 4), node.Right);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 76, 4), node.Child);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 116, 4), node.Start);
            BinaryPrimitives.WriteUInt64LittleEndian(
                buffer.AsSpan(offset + 120, 8),
                (ulong)(node.IsStorage && i != 0 ? 0 : node.Size > 0 ? node.Size : node.Data.Length));
        }

        return buffer;
    }

    private static byte[] BuildHeader(
        uint fatSector, uint directoryStart, uint miniFatStart, int miniFatSectors)
    {
        var header = new byte[SectorSize];

        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24, 2), 0x003E);   // minor
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26, 2), 3);        // major
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28, 2), 0xFFFE);   // byte order
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30, 2), 9);        // 512-byte sectors
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32, 2), 6);        // 64-byte mini
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), 1);        // FAT sectors
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48, 4), directoryStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56, 4), MiniCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60, 4), miniFatStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64, 4), (uint)miniFatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68, 4), EndOfChain);   // no DIFAT
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(72, 4), 0);

        // DIFAT: the first FAT sector, then free.
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76, 4), fatSector);
        for (var i = 1; i < 109; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76 + i * 4, 4), Free);

        return header;
    }

    private static byte[] Flatten32(List<uint> values)
    {
        var buffer = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i * 4, 4), values[i]);

        return buffer;
    }

    private static void WriteAt(MemoryStream stream, int offset, byte[] data)
    {
        if (data.Length == 0) return;
        if (stream.Length < offset + data.Length)
            stream.SetLength(offset + data.Length);

        stream.Position = offset;
        stream.Write(data);
    }

    private static void Pad(MemoryStream stream, int alignment)
    {
        var remainder = (int)(stream.Length % alignment);
        if (remainder != 0)
            stream.Write(new byte[alignment - remainder]);
    }

    private static int Ceil(int value, int unit) => (value + unit - 1) / unit;

    private sealed class Node(string Name, bool IsStorage)
    {
        public string Name { get; } = Name;
        public bool IsStorage { get; } = IsStorage;
        public byte[] Data { get; init; } = [];
        public List<Node> Children { get; } = [];
        public uint Right { get; set; } = NoStream;
        public uint Child { get; set; } = NoStream;
        public uint Start { get; set; } = EndOfChain;
        public long Size { get; set; }
    }
}
