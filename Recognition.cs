using System.IO.Compression;
using System.Text.Json;
using SourceAFIS;

namespace FS80H;

sealed class RecognitionTouchGate
{
    public bool WaitingForLift { get; private set; }
    double absentSince = -1;
    public void Reset(bool requireLift = false) { WaitingForLift = requireLift; absentSince = -1; }
    public void RequireLift() => Reset(true);
    public bool AcceptFrame(bool hasFrame, uint error, double now)
    {
        if (!WaitingForLift) return hasFrame;
        if (!hasFrame && error == 4306)
        {
            if (absentSince < 0) absentSince = now;
            if (now - absentSince >= .4) Reset();
        }
        else absentSince = -1;
        return false;
    }
}

record RecognitionResult(string? Alias, double Score, bool Ambiguous = false, double Threshold = FingerprintDatabase.MatchThreshold)
{
    public bool Found => Alias != null && !Ambiguous;
    public string Display => Found ? Alias! : "Не найден";
}

sealed class FingerprintDatabase
{
    public const double MatchThreshold = 40;
    public const int MinimumThreshold = 30;
    public const int MaximumThreshold = 60;
    public static double ClampThreshold(double value) => double.IsFinite(value) ? Math.Clamp(value, MinimumThreshold, MaximumThreshold) : MatchThreshold;
    public const double AliasMargin = 8;
    internal record Entry(string Alias, string File, FingerprintTemplate Template);
    readonly List<Entry> entries;
    public int Count => entries.Count;
    public int Skipped { get; }
    public int AliasCount => entries.Select(e => e.Alias).Distinct(StringComparer.Ordinal).Count();
    // Templates stay in process memory only. Re-check archive bytes on every load.
    static readonly Dictionary<string, (string Digest, Entry Entry)> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object CacheGate = new();
    internal FingerprintDatabase(IEnumerable<Entry> entries, int skipped = 0) { this.entries = entries.ToList(); Skipped = skipped; }

    public static FingerprintDatabase Load(string directory, CancellationToken token, Action<int, int>? progress = null)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Папка базы не найдена. Выберите её в Setup.");
        string[] files = Directory.GetFiles(directory, "fingerprint_*.zip", SearchOption.TopDirectoryOnly);
        var entries = new List<Entry>(); int skipped = 0;
        var active = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        lock (CacheGate) foreach (var stale in Cache.Keys.Where(k => !active.Contains(k)).ToArray()) Cache.Remove(stale);
        for (int i = 0; i < files.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            byte[]? raw = null;
            try
            {
                using var archive = ZipFile.OpenRead(files[i]);
                var metadata = archive.GetEntry("metadata.json") ?? throw new InvalidDataException();
                if (metadata.Length > 65_536) throw new InvalidDataException();
                using var metadataStream = metadata.Open(); using var doc = JsonDocument.Parse(metadataStream);
                var root = doc.RootElement;
                int schema = root.GetProperty("schemaVersion").GetInt32();
                if (schema < 1 || schema > 3) throw new InvalidDataException();
                string alias = (root.GetProperty(schema >= 3 ? "alias" : "user").GetString() ?? "").Trim();
                if (alias.Length is 0 or > 80 || alias.Any(char.IsControl)) throw new InvalidDataException();
                int width = root.GetProperty("width").GetInt32(), height = root.GetProperty("height").GetInt32();
                if (width < 64 || height < 64 || width > 4096 || height > 4096 || (long)width * height > 4_000_000 || root.GetProperty("pixelFormat").GetString() != "Gray8") throw new InvalidDataException();
                var image = archive.GetEntry("image.raw") ?? throw new InvalidDataException();
                if (image.Length != (long)width * height) throw new InvalidDataException();
                raw = new byte[width * height]; using (var stream = image.Open()) stream.ReadExactly(raw);
                string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)) + $":{width}:{height}:{alias}";
                Entry? entry = null;
                lock (CacheGate) if (Cache.TryGetValue(files[i], out var cached) && cached.Digest == digest) entry = cached.Entry;
                if (entry == null)
                {
                    var template = Extract(new CaptureData(width, height, raw, DateTimeOffset.UtcNow));
                    token.ThrowIfCancellationRequested();
                    entry = new Entry(alias, files[i], template);
                    lock (CacheGate) Cache[files[i]] = (digest, entry);
                }
                entries.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException or InvalidDataException or FormatException)
            { skipped++; }
            finally { if (raw != null) Array.Clear(raw); }
            progress?.Invoke(i + 1, files.Length);
        }
        return new FingerprintDatabase(entries, skipped);
    }

    internal static FingerprintTemplate Extract(CaptureData capture)
    {
        if (capture.Width <= 0 || capture.Height <= 0 || (long)capture.Width * capture.Height != capture.Pixels.Length) throw new ArgumentException("Неверный размер изображения.");
        // FS80H buffer has light ridges on dark background; SourceAFIS expects the inverse.
        var inverted = new byte[capture.Pixels.Length];
        try
        {
            for (int i = 0; i < inverted.Length; i++) inverted[i] = (byte)(255 - capture.Pixels[i]);
            return new FingerprintTemplate(new FingerprintImage(capture.Width, capture.Height, inverted, new FingerprintImageOptions { Dpi = 500 }));
        }
        finally { Array.Clear(inverted); }
    }

    public RecognitionResult Identify(CaptureData capture, CancellationToken token, string? excludeFile = null, double threshold = MatchThreshold)
    {
        token.ThrowIfCancellationRequested();
        var matcher = new FingerprintMatcher(Extract(capture));
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var candidate in entries)
        {
            token.ThrowIfCancellationRequested();
            if (excludeFile != null && string.Equals(excludeFile, candidate.File, StringComparison.OrdinalIgnoreCase)) continue;
            double score = matcher.Match(candidate.Template);
            if (double.IsFinite(score) && (!scores.TryGetValue(candidate.Alias, out double previous) || score > previous)) scores[candidate.Alias] = score;
        }
        return Decide(scores, threshold);
    }
    internal static RecognitionResult Decide(IReadOnlyDictionary<string, double> scores, double threshold = MatchThreshold)
    {
        threshold = ClampThreshold(threshold);
        var ordered = scores.Where(s => double.IsFinite(s.Value)).OrderByDescending(s => s.Value).ToArray();
        if (ordered.Length == 0 || ordered[0].Value < threshold) return new(null, ordered.Length == 0 ? 0 : ordered[0].Value, Threshold: threshold);
        if (ordered.Length > 1 && ordered[0].Value - ordered[1].Value < AliasMargin) return new(null, ordered[0].Value, true, threshold);
        return new(ordered[0].Key, ordered[0].Value, Threshold: threshold);
    }
}
