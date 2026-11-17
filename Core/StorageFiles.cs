namespace ListeningSubtitles.Core;
internal static class StorageFiles
{
    internal static void WriteAtomic(string path, byte[] data, Func<string, FileStream>? createFile = null)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("存储目录无效。", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = createFile?.Invoke(temporary) ?? new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory rename provides atomic replacement without a plaintext backup.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
