using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace ListeningSubtitles.Core;

public sealed record SavedCaptionRecord(string FileName, DateTimeOffset StartedAt)
{
    public override string ToString() => StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed class CaptionStore(string directory)
{
    private const int MaximumBytes = 64 * 1024 * 1024;
    public string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(directory), "history");
    public IReadOnlyList<SavedCaptionRecord> List()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        return Directory.EnumerateFiles(DirectoryPath, "*.bin").Select(Path.GetFileName)
            .Where(name => name is not null && name.Length == 52 &&
                DateTimeOffset.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out _) && Guid.TryParseExact(name.Substring(16, 32), "N", out _))
            .Select(name => new SavedCaptionRecord(name!, DateTimeOffset.ParseExact(name![..15], "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)))
            .OrderByDescending(x => x.StartedAt).ToArray();
    }
    public string Save(CaptionHistory history)
    {
        var record = history.Snapshot();
        var name = record.StartedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + record.Id.ToString("N") + ".bin";
        var plain = JsonSerializer.SerializeToUtf8Bytes(record);
        try
        {
            if (plain.Length > MaximumBytes - 4096) throw new IOException(AppText.T("本场记录过大，请先导出字幕，再开始新记录。", "This session is too large. Export the subtitles, then start a new session."));
            var encrypted = SecretStore.Transform(plain, true, "Listening Subtitles history", MaximumBytes);
            StorageFiles.WriteAtomic(Path.Combine(DirectoryPath, name), encrypted, SecretStore.CreatePrivateFile);
            return Path.Combine(DirectoryPath, name);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public CaptionRecord Read(SavedCaptionRecord saved)
    {
        if (saved.FileName != Path.GetFileName(saved.FileName)) throw new InvalidDataException(AppText.T("历史文件名无效。", "The history file name is invalid."));
        var path = Path.Combine(DirectoryPath, saved.FileName);
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException(AppText.T("历史文件过大。", "The history file is too large."));
        var plain = SecretStore.Transform(File.ReadAllBytes(path), false, maximumBytes: MaximumBytes);
        try
        {
            var record = JsonSerializer.Deserialize<CaptionRecord>(plain);
            if (record is not { Version: 1 } || record.Id == Guid.Empty || record.Captions is null ||
                record.Captions.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || c.Original is null ||
                    c.Translation is null || c.TranslationStatus is null || c.SourceLanguages is null || !TranslationLanguages.IsSupported(c.TranslationLanguage)) ||
                record.Captions.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != record.Captions.Length)
                throw new InvalidDataException(AppText.T("历史文件内容无效。", "The history file content is invalid."));
            return record;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
