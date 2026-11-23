using System.Buffers.Binary;

namespace Launcher.Core.Runners;

/// <summary>
/// Clears the cache partitions (X, Y, Z) of xemu's Xbox hard disk image before a launch.
/// Crazy Taxi 3 hangs on "now loading" for Glitter Oasis when its cache partition holds data from an earlier
/// xemu boot (1 load in about 14 worked); with fresh caches it loaded every time (2026-11-05). The Xbox treats
/// these partitions as scratch space: with the cache table (sector 4) and the partitions' FATX headers zeroed,
/// it formats a fresh cache for the game. Saves and the dashboard (partitions E and C) are not touched.
/// Works on the qcow2 image in place: zeroes the bytes inside clusters that are already allocated (unallocated
/// clusters read as zero anyway), so qcow2 metadata never changes.
/// </summary>
public static class XboxHddCache
{
    const uint QcowMagic = 0x514649FB;
    const ulong OffsetMask = 0x00FF_FFFF_FFFF_FE00;
    const ulong CompressedFlag = 1UL << 62;
    const ulong ZeroFlag = 1UL;

    // Byte ranges to zero: the cache table, then the first 64 KB (FATX header + start of the FAT) of X, Y and Z.
    static readonly (long Offset, int Length)[] Ranges =
    [
        (4 * 512, 512),
        (0x0008_0000, 0x10000),
        (0x2EE8_0000, 0x10000),
        (0x5DC8_0000, 0x10000),
    ];

    /// <summary>Returns a short description of what happened, for the log.</summary>
    public static string Clear(string qcowPath)
    {
        if (!File.Exists(qcowPath)) return $"no HDD image at {qcowPath}";
        using var f = new FileStream(qcowPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Span<byte> h = stackalloc byte[104];
        f.ReadExactly(h);
        if (BinaryPrimitives.ReadUInt32BigEndian(h) != QcowMagic) return "not a qcow2 image, left alone";
        uint version = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
        if (BinaryPrimitives.ReadUInt64BigEndian(h[8..]) != 0) return "qcow2 with a backing file, left alone";
        int clusterBits = (int)BinaryPrimitives.ReadUInt32BigEndian(h[20..]);
        if (BinaryPrimitives.ReadUInt32BigEndian(h[32..]) != 0) return "encrypted qcow2, left alone";
        uint l1Size = BinaryPrimitives.ReadUInt32BigEndian(h[36..]);
        long l1Offset = (long)BinaryPrimitives.ReadUInt64BigEndian(h[40..]);
        // Version 3 feature bits other than "dirty" (bit 0) change the layout (e.g. extended L2 entries).
        if (version >= 3 && (BinaryPrimitives.ReadUInt64BigEndian(h[72..]) & ~1UL) != 0)
            return "qcow2 with unsupported features, left alone";

        long clusterSize = 1L << clusterBits;
        long l2Entries = clusterSize / 8;
        var l1 = new byte[l1Size * 8];
        f.Position = l1Offset;
        f.ReadExactly(l1);

        // First pass: find the host offset of every piece to zero; give up before writing anything if a
        // cluster is compressed.
        var writes = new List<(long Host, int Length)>();
        Span<byte> e = stackalloc byte[8];
        foreach (var (start, length) in Ranges)
        {
            long off = start, left = length;
            while (left > 0)
            {
                int n = (int)Math.Min(left, clusterSize - (off & (clusterSize - 1)));
                long ci = off >> clusterBits;
                long l1i = ci / l2Entries;
                if (l1i >= l1Size) return "HDD image smaller than expected, left alone";
                long l2Table = (long)(BinaryPrimitives.ReadUInt64BigEndian(l1.AsSpan((int)l1i * 8)) & OffsetMask);
                if (l2Table != 0)
                {
                    f.Position = l2Table + 8 * (ci % l2Entries);
                    f.ReadExactly(e);
                    ulong entry = BinaryPrimitives.ReadUInt64BigEndian(e);
                    if ((entry & CompressedFlag) != 0) return "compressed qcow2 clusters, left alone";
                    long host = (long)(entry & OffsetMask);
                    if (host != 0 && (entry & ZeroFlag) == 0)
                        writes.Add((host + (off & (clusterSize - 1)), n));
                }
                off += n;
                left -= n;
            }
        }

        var zeros = new byte[64 * 1024];
        foreach (var (host, length) in writes)
        {
            f.Position = host;
            f.Write(zeros, 0, length);
        }
        f.Flush(flushToDisk: true);
        return $"cleared cache partitions X/Y/Z ({writes.Count} pieces written)";
    }
}
