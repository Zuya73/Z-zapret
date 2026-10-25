using System.Diagnostics;

namespace Zapret.Core;

public sealed record SpaceEntry(string Path, long Bytes, bool Directory)
{ public string Size => Bytes >= 1073741824 ? $"{Bytes / 1073741824d:0.00} ГиБ" : $"{Bytes / 1048576d:0.00} МиБ"; }
public sealed record SpaceScan(string Root, long Bytes, long Files, long Skipped, bool Partial, IReadOnlyList<SpaceEntry> Folders, IReadOnlyList<SpaceEntry> LargestFiles);
public sealed record SpaceProgress(long Files, long Bytes, long Skipped);

public static class SpaceAnalyzer
{
    public static SpaceScan Scan(string root, IProgress<SpaceProgress>? progress, CancellationToken token,
        long maximumEntries = 2_000_000, TimeSpan? maximumTime = null)
    {
        root = Path.GetFullPath(root);
        if (OperatingSystem.IsWindows() && (root.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(Path.GetPathRoot(root)!).DriveType is not (DriveType.Fixed or DriveType.Removable)))
            throw new ArgumentException("Выберите папку на локальном или съёмном накопителе.");
        if (!System.IO.Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Папка недоступна или является ссылкой.");
        var folders = new Dictionary<string, long>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var largest = new PriorityQueue<SpaceEntry, long>();
        var pending = new Stack<(IEnumerator<string> Iterator, string Group)>();
        long files = 0, bytes = 0, skipped = 0, visited = 0; bool partial = false;
        var watch = Stopwatch.StartNew();
        pending.Push((System.IO.Directory.EnumerateFileSystemEntries(root).GetEnumerator(), root));
        try
        {
            while (pending.Count > 0)
            {
                if (token.IsCancellationRequested || ++visited > maximumEntries || watch.Elapsed > (maximumTime ?? TimeSpan.FromMinutes(10))) { partial = true; break; }
                var current = pending.Peek(); string path;
                try
                {
                    if (!current.Iterator.MoveNext()) { pending.Pop().Iterator.Dispose(); continue; }
                    path = current.Iterator.Current;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { skipped++; pending.Pop().Iterator.Dispose(); continue; }
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        string group = current.Group == root ? path : current.Group;
                        if (!folders.ContainsKey(group) && folders.Count >= 10_000) { skipped++; continue; }
                        folders.TryAdd(group, 0);
                        if (pending.Count >= 256) { skipped++; continue; }
                        pending.Push((System.IO.Directory.EnumerateFileSystemEntries(path).GetEnumerator(), group));
                    }
                    else
                    {
                        long size = new FileInfo(path).Length; files++; bytes += size;
                        folders[current.Group] = folders.GetValueOrDefault(current.Group) + size;
                        largest.Enqueue(new(path, size, false), size);
                        if (largest.Count > 100) largest.Dequeue();
                        if (files % 1000 == 0) progress?.Report(new(files, bytes, skipped));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
            }
        }
        finally { while (pending.TryPop(out var item)) item.Iterator.Dispose(); }
        progress?.Report(new(files, bytes, skipped));
        return new(root, bytes, files, skipped, partial, folders.Select(x => new SpaceEntry(x.Key, x.Value, true)).OrderByDescending(x => x.Bytes).Take(100).ToArray(),
            largest.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Bytes).ToArray());
    }
}
