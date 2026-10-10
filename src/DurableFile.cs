using System.Text;
using System.Security.Cryptography;

namespace SeedSearch;

internal static class DurableFile
{
    public const int TailHashBytes = 64 * 1024;
    public static void WriteAtomic(string path, string text, bool keepBackup = true)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, keepBackup ? path + ".bak" : null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Flush(StreamWriter writer)
    {
        writer.Flush();
        if (writer.BaseStream is FileStream stream) stream.Flush(flushToDisk: true);
    }

    public static string TailSha256(string path) => TailSha256(path, null);

    public static string TailSha256(string path, long? length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long targetLength = length ?? stream.Length;
        if (targetLength < 0 || targetLength > stream.Length) throw new ArgumentOutOfRangeException(nameof(length));
        long start = Math.Max(0, targetLength - TailHashBytes);
        stream.Position = start;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        int read;
        long remaining = targetLength - start;
        while (remaining > 0 && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
        {
            hash.AppendData(buffer, 0, read); remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static void Truncate(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (length < 0 || length > stream.Length) throw new ArgumentOutOfRangeException(nameof(length));
        stream.SetLength(length);
        stream.Flush(flushToDisk: true);
    }

    public static FileStream AcquireLock(string output)
    {
        string path = Path.GetFullPath(output + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException($"结果文件正在被另一个搜索任务使用：{output}", error); }
    }

    public static void AppendDurable(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
        writer.Write(text);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }
}
