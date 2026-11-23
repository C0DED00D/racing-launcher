namespace Launcher.Core.Runners;

/// <summary>
/// Pools Xenia's "shareable" shader caches between its installs before a launch, so a shader compiled once (in
/// 1-player, seat 1 or seat 2) isn't compiled again elsewhere mid-race. Xenia compiles everything listed in these
/// files at startup instead of stuttering the first time an effect appears.
///
/// Files, in &lt;storage root&gt;/cache_host/shaders/shareable/ (format from Xenia's gpu/shader_storage.h):
///   {TITLE}.xsh               header 'XESH' + version (8 bytes); entries = u64 ucode hash, u32 dword count (bit 31
///                             = shader type), then count * 4 bytes of microcode. Same format in every build.
///   {TITLE}.*.d3d12.xpso      header 'XEPS' + api magic + version (12 bytes); fixed 72-byte records starting with a
///                             u64 description hash. The version differs between Xenia builds, so only files with
///                             an identical header are merged.
/// Entries are de-duplicated by hash; a truncated tail is dropped. Must only run while no copy is running.
/// </summary>
public static class XeniaShaderCache
{
    const int XshHeader = 8, XshEntryHeader = 12;
    const int XpsoHeader = 12, XpsoRecord = 72;

    /// <summary>Merges the caches in all given storage roots; returns a line for the log.</summary>
    public static string Sync(IEnumerable<string> storageRoots)
    {
        var dirs = storageRoots
            .Select(r => Path.Combine(r, "cache_host", "shaders", "shareable"))
            .Where(Directory.Exists).ToList();
        if (dirs.Count < 2) return "shader caches: nothing to share yet";

        var names = dirs.SelectMany(d => Directory.GetFiles(d).Select(Path.GetFileName))
            .Where(n => n!.EndsWith(".xsh", StringComparison.OrdinalIgnoreCase) ||
                        n.EndsWith(".xpso", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var report = new List<string>();
        foreach (var name in names)
        {
            try
            {
                report.Add(name!.EndsWith(".xsh", StringComparison.OrdinalIgnoreCase)
                    ? MergeXsh(dirs, name) : MergeXpso(dirs, name));
            }
            catch (IOException ex) { report.Add($"{name}: skipped ({ex.Message})"); }
        }
        return "shader caches: " + string.Join("; ", report);
    }

    static string MergeXsh(List<string> dirs, string name)
    {
        byte[]? header = null;
        var entries = new Dictionary<ulong, byte[]>();
        var order = new List<ulong>();
        foreach (var d in dirs)
        {
            var path = Path.Combine(d, name);
            if (!File.Exists(path)) continue;
            var b = File.ReadAllBytes(path);
            if (b.Length < XshHeader) continue;
            header ??= b[..XshHeader];
            if (!b.AsSpan(0, XshHeader).SequenceEqual(header)) continue;
            for (int pos = XshHeader; pos + XshEntryHeader <= b.Length;)
            {
                ulong hash = BitConverter.ToUInt64(b, pos);
                uint dwords = BitConverter.ToUInt32(b, pos + 8) & 0x7FFFFFFF;
                long size = XshEntryHeader + dwords * 4L;
                if (pos + size > b.Length) break;
                if (entries.TryAdd(hash, b[pos..(int)(pos + size)])) order.Add(hash);
                pos += (int)size;
            }
        }
        if (header == null) return $"{name}: empty";

        using var ms = new MemoryStream();
        ms.Write(header);
        foreach (var h in order) ms.Write(entries[h]);
        var merged = ms.ToArray();
        // The game's shaders are the same everywhere, so every install gets the full set.
        foreach (var d in dirs) WriteIfDifferent(Path.Combine(d, name), merged);
        return $"{name}: {order.Count} shaders in {dirs.Count} installs";
    }

    static string MergeXpso(List<string> dirs, string name)
    {
        var groups = new Dictionary<string, (List<string> Paths, Dictionary<ulong, byte[]> Records, List<ulong> Order, byte[] Header)>();
        foreach (var d in dirs)
        {
            var path = Path.Combine(d, name);
            if (!File.Exists(path)) continue;
            var b = File.ReadAllBytes(path);
            if (b.Length < XpsoHeader) continue;
            var key = Convert.ToHexString(b, 0, XpsoHeader);
            if (!groups.TryGetValue(key, out var g))
                groups[key] = g = (new List<string>(), new Dictionary<ulong, byte[]>(), new List<ulong>(), b[..XpsoHeader]);
            g.Paths.Add(path);
            for (int pos = XpsoHeader; pos + XpsoRecord <= b.Length; pos += XpsoRecord)
            {
                ulong hash = BitConverter.ToUInt64(b, pos);
                if (g.Records.TryAdd(hash, b[pos..(pos + XpsoRecord)])) g.Order.Add(hash);
            }
        }

        var parts = new List<string>();
        foreach (var g in groups.Values)
        {
            using var ms = new MemoryStream();
            ms.Write(g.Header);
            foreach (var h in g.Order) ms.Write(g.Records[h]);
            var merged = ms.ToArray();
            foreach (var p in g.Paths) WriteIfDifferent(p, merged);
            parts.Add($"{g.Order.Count} pipelines in {g.Paths.Count} install(s)");
        }
        return $"{name}: " + string.Join(", ", parts);
    }

    static void WriteIfDifferent(string path, byte[] data)
    {
        if (File.Exists(path) && new FileInfo(path).Length == data.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
            return;
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }
}
