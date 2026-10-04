using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Security.Cryptography;

namespace PackPaste;

public record TransferProgress(string Phase, string Name, long Done, long Total);
public record Package(string Id, string Archive, long Bytes, string Sha256);
public enum ConflictChoice { Overwrite, Skip, Rename }
public record ConflictDecision(ConflictChoice Choice, bool All);
public sealed class TransferEngine
{
    internal static string? CacheOverride { get; set; }
    public static string CacheRoot => CacheOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackPaste", "Transfers");
    readonly Action<TransferProgress> progress;
    readonly Func<string, ConflictDecision> conflict;
    readonly CancellationToken token;
    ConflictChoice? all;
    public int Skipped { get; private set; }
    public TransferEngine(Action<TransferProgress> progress, Func<string, ConflictDecision> conflict, CancellationToken token)
    { this.progress = progress; this.conflict = conflict; this.token = token; }
    void Check() => token.ThrowIfCancellationRequested();
    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    static void NoLink(string path)
    {
        if (Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("不支持符号链接或目录联接：" + path);
    }
    public static void CheckAncestors(string path)
    {
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p)) NoLink(p);
    }
    static string Unique(string path)
    {
        string parent = Path.GetDirectoryName(path)!;
        string ext = Directory.Exists(path) ? "" : Path.GetExtension(path);
        string name = Path.GetFileName(path)[..^ext.Length];
        for (int i = 2; ; i++) { var candidate = Path.Combine(parent, $"{name} ({i}){ext}"); if (!Exists(candidate)) return candidate; }
    }
    public Package Pack(string[] sources)
    {
        Check();
        var roots = sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        roots = roots.Where(p => !roots.Any(q => !p.Equals(q, StringComparison.OrdinalIgnoreCase) && p.StartsWith(Path.TrimEndingDirectorySeparator(q) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (roots.Length == 0) throw new IOException("没有选择文件。");
        var entries = new List<(string Source, string Name, bool Directory, long Size)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        void Scan(string source, string name)
        {
            Check(); NoLink(source);
            progress(new("正在扫描", source, 0, 0));
            bool dir = Directory.Exists(source);
            long size = dir ? 0 : new FileInfo(source).Length;
            entries.Add((source, name, dir, size)); total += size;
            if (dir) foreach (string child in Directory.EnumerateFileSystemEntries(source)) Scan(child, name + "/" + Path.GetFileName(child));
        }
        foreach (var root in roots)
        {
            CheckAncestors(root);
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            if (string.IsNullOrEmpty(name)) throw new IOException("请选择磁盘中的文件夹，不要直接选择整个磁盘。");
            var original = name;
            for (int i = 2; !names.Add(name); i++) name = original + $" ({i})";
            Scan(root, name);
        }
        CheckAncestors(CacheRoot); Directory.CreateDirectory(CacheRoot);
        string id = Guid.NewGuid().ToString("N");
        string folder = Path.Combine(CacheRoot, id);
        Directory.CreateDirectory(folder);
        string archive = Path.Combine(folder, "文件包.tar.gz");
        long done = 0;
        try
        {
            using (var file = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                foreach (var item in entries)
                {
                    Check(); NoLink(item.Source);
                    var entry = new PaxTarEntry(item.Directory ? TarEntryType.Directory : TarEntryType.RegularFile, item.Name)
                    { ModificationTime = File.GetLastWriteTimeUtc(item.Source) };
                    if (item.Directory) writer.WriteEntry(entry);
                    else
                    {
                        using var input = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (input.Length != item.Size) throw new IOException("扫描后文件大小发生变化，请重试：" + item.Source);
                        using var meter = new MeterStream(input, n => { done += n; progress(new("正在打包压缩", item.Name, done, total)); }, token);
                        entry.DataStream = meter;
                        writer.WriteEntry(entry);
                    }
                }
            }
            Check();
            string digest; using (var verify = File.OpenRead(archive)) digest = Hash(verify, "正在校验文件包");
            var package = new Package(id, archive, total, digest);
            File.WriteAllText(Path.Combine(folder, "package.json"), JsonSerializer.Serialize(package));
            return package;
        }
        catch { Directory.Delete(folder, true); throw; }
    }
    public static Package LoadPackage(string archive)
    {
        string full = Path.GetFullPath(archive);
        string folder = Path.GetDirectoryName(full)!;
        string id = Path.GetFileName(folder);
        if (!Guid.TryParseExact(id, "N", out _) || !Path.GetDirectoryName(folder)!.Equals(Path.GetFullPath(CacheRoot), StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(full) != "文件包.tar.gz") throw new IOException("剪贴板不是本工具生成的文件包，请先使用“打包复制”。");
        CheckAncestors(full);
        var result = JsonSerializer.Deserialize<Package>(File.ReadAllText(Path.Combine(folder, "package.json"))) ?? throw new IOException("文件包信息损坏。");
        if (result.Id != id || !result.Archive.Equals(full, StringComparison.OrdinalIgnoreCase) || result.Bytes < 0) throw new IOException("文件包信息无效。");
        if (!File.Exists(full)) throw new IOException("临时压缩包已不存在，请重新复制。");
        return result;
    }
    public void Paste(Package package, string target)
    {
        Check(); target = Path.GetFullPath(target); CheckAncestors(target);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
        if (target.StartsWith(Path.GetDirectoryName(package.Archive)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || target.Equals(Path.GetDirectoryName(package.Archive), StringComparison.OrdinalIgnoreCase)) throw new IOException("不能粘贴到文件包缓存目录。");
        using var source = new FileStream(package.Archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        string work = Path.Combine(target, ".PackPaste-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string local = Path.Combine(work, "文件包.tar.gz");
        string stage = Path.Combine(work, "解压内容");
        bool complete = false;
        try
        {
            using (var output = new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Copy(source, output, "正在复制压缩包", Path.GetFileName(local), source.Length);
            using (var verify = File.OpenRead(local))
                if (!Hash(verify, "正在校验文件包").Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("文件包校验失败，请重新打包复制。");
            Directory.CreateDirectory(stage);
            using (var input = File.OpenRead(local))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var reader = new TarReader(gzip))
            {
                long done = 0;
                TarEntry? entry;
                while ((entry = reader.GetNextEntry()) != null)
                {
                    Check();
                    if (entry.EntryType != TarEntryType.Directory && entry.EntryType != TarEntryType.RegularFile && entry.EntryType != TarEntryType.V7RegularFile)
                        throw new IOException("文件包包含不支持的链接或特殊文件。");
                    var destination = SafeEntryPath(stage, entry.Name);
                    if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(destination);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            var buffer = new byte[1024 * 1024]; int count;
                            while (entry.DataStream != null && (count = entry.DataStream.Read(buffer)) > 0)
                            {
                                Check(); output.Write(buffer, 0, count); done += count;
                                progress(new("正在解压", entry.Name, done, package.Bytes));
                            }
                        }
                        File.SetLastWriteTimeUtc(destination, entry.ModificationTime.UtcDateTime);
                    }
                }
            }
            // Merge only fully extracted files. Cancellation never leaves a half-written destination file.
            var top = Directory.GetFileSystemEntries(stage);
            for (int i = 0; i < top.Length; i++)
            {
                Check(); progress(new("正在放置文件", Path.GetFileName(top[i]), i, top.Length));
                Merge(top[i], Path.Combine(target, Path.GetFileName(top[i])));
            }
            complete = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException(ex.Message + "\n\n临时目录已保留，可重试或手动处理：\n" + work, ex);
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("已取消。已放置的完整文件会保留，尚未放置的文件没有改动。\n源文件包可继续粘贴。临时目录：\n" + work);
        }
        finally
        {
            if (complete) Directory.Delete(work, true);
        }
    }
    public static string SafeEntryPath(string root, string name)
    {
        var parts = name.Replace('\\', '/').TrimEnd('/').Split('/');
        if (parts.Length == 0 || parts.Any(p => string.IsNullOrEmpty(p) || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')
            || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDevice(p))) throw new IOException("文件包包含不安全的路径：" + name);
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("文件包路径越界。");
        return full;
    }
    static bool IsDevice(string name)
    {
        string n = name.Split('.')[0].ToUpperInvariant();
        return n is "CON" or "PRN" or "AUX" or "NUL" || (n.Length == 4 && (n.StartsWith("COM") || n.StartsWith("LPT")) && "123456789¹²³".Contains(n[3]));
    }
    void Merge(string source, string dest)
    {
        Check(); CheckAncestors(dest);
        bool dir = Directory.Exists(source);
        if (dir && Directory.Exists(dest))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(source)) Merge(child, Path.Combine(dest, Path.GetFileName(child)));
            return;
        }
        if (Exists(dest))
        {
            var decision = all.HasValue ? new ConflictDecision(all.Value, true) : conflict(dest);
            Check(); if (decision.All) all = decision.Choice;
            if (decision.Choice == ConflictChoice.Skip) { Skipped++; return; }
            if (decision.Choice == ConflictChoice.Rename) dest = Unique(dest);
            else if (dir || Directory.Exists(dest))
            {
                // Preserve the old item until the replacement move succeeds.
                string backup = Path.Combine(Path.GetDirectoryName(dest)!, ".PackPaste-backup-" + Guid.NewGuid().ToString("N"));
                bool oldDir = Directory.Exists(dest);
                if (oldDir) Directory.Move(dest, backup); else File.Move(dest, backup);
                try { if (dir) Directory.Move(source, dest); else File.Move(source, dest); }
                catch { if (oldDir) Directory.Move(backup, dest); else File.Move(backup, dest); throw; }
                if (oldDir) Directory.Delete(backup, true); else File.Delete(backup);
                return;
            }
        }
        CheckAncestors(dest);
        if (dir) Directory.Move(source, dest); else File.Move(source, dest, true);
    }
    string Hash(Stream input, string phase)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024]; long done = 0; int count;
        while ((count = input.Read(buffer)) > 0) { Check(); hash.AppendData(buffer, 0, count); done += count; progress(new(phase, "SHA-256", done, input.Length)); }
        Check(); return Convert.ToHexString(hash.GetHashAndReset());
    }
    void Copy(Stream input, Stream output, string phase, string name, long total)
    {
        var buffer = new byte[1024 * 1024]; long done = 0; int count;
        while ((count = input.Read(buffer)) > 0) { Check(); output.Write(buffer, 0, count); done += count; progress(new(phase, name, done, total)); }
    }
    public static void DeletePackage(Package package)
    {
        _ = LoadPackage(package.Archive);
        Directory.Delete(Path.GetDirectoryName(package.Archive)!, true);
    }
}
internal sealed class MeterStream(Stream inner, Action<int> read, CancellationToken token) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); int n = inner.Read(buffer, offset, count); read(n); return n; }
    public override int Read(Span<byte> buffer) { token.ThrowIfCancellationRequested(); int n = inner.Read(buffer); read(n); return n; }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() => inner.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
