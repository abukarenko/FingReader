using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FS80H;

record CaptureInfo(string SessionId, int SampleNumber, int RequestedSamples, string PositionHint,
    string SaveMode, double StableSeconds, string QualityMethod = "contrast-texture-v1 (heuristic, not NFIQ)");

static class PreviewImage
{
    public static Bitmap Create(CaptureData data)
    {
        var bitmap = new Bitmap(data.Width, data.Height, PixelFormat.Format8bppIndexed);
        try
        {
            var palette = bitmap.Palette;
            for (int i = 0; i < 256; i++) palette.Entries[i] = Color.FromArgb(i, i, i);
            bitmap.Palette = palette;
            Update(bitmap, data);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
    public static void Update(Bitmap bitmap, CaptureData data)
    {
        if (bitmap.Width != data.Width || bitmap.Height != data.Height || bitmap.PixelFormat != PixelFormat.Format8bppIndexed || (long)data.Width * data.Height != data.Pixels.Length)
            throw new ArgumentException("Размер или формат кадра не совпадает с изображением.");
        var locked = bitmap.LockBits(new Rectangle(0, 0, data.Width, data.Height), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
        try { for (int y = 0; y < data.Height; y++) Marshal.Copy(data.Pixels, y * data.Width, locked.Scan0 + y * locked.Stride, data.Width); }
        finally { bitmap.UnlockBits(locked); }
    }
}

record FrameQuality(bool Acceptable, double Contrast, double TextureFraction, string Message);

static class QualityCheck
{
    // Conservative experimental image checks; not a fingerprint matcher or certified quality score.
    public static FrameQuality Evaluate(CaptureData data)
    {
        int textured = 0, blocks = 0, count = 0, middle = 0;
        double sum = 0, squares = 0;
        int w = data.Width, h = data.Height;
        for (int by = h / 10; by + 16 < h * 9 / 10; by += 16)
        for (int bx = w / 10; bx + 16 < w * 9 / 10; bx += 16)
        {
            double local = 0, local2 = 0, gradient = 0;
            for (int y = by; y < by + 16; y++)
            for (int x = bx; x < bx + 16; x++)
            {
                int v = data.Pixels[y * w + x];
                local += v; local2 += v * v; sum += v; squares += v * v; count++;
                if (v > 10 && v < 245) middle++;
                if (x > bx) gradient += Math.Abs(v - data.Pixels[y * w + x - 1]);
                if (y > by) gradient += Math.Abs(v - data.Pixels[(y - 1) * w + x]);
            }
            double deviation = Math.Sqrt(Math.Max(0, local2 / 256 - Math.Pow(local / 256, 2)));
            double edge = gradient / 480;
            if (deviation >= 12 && edge >= 3 && edge <= 65) textured++;
            blocks++;
        }
        if (count == 0 || blocks == 0) return new(false, 0, 0, "Слишком маленький кадр");
        double contrast = Math.Sqrt(Math.Max(0, squares / count - Math.Pow(sum / count, 2)));
        double fraction = (double)textured / blocks;
        bool good = contrast >= 18 && fraction >= .18 && (double)middle / count >= .12;
        return new(good, contrast, fraction, good ? "Контраст и текстура достаточны (предварительно)" : "Недостаточно текстуры: измените прижатие или положение пальца");
    }
}

sealed class StabilityTracker
{
    byte[]? anchor, previous;
    double started, last;
    int frames;
    public double Seconds { get; private set; }
    public static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    public void Reset()
    {
        if (anchor != null) Array.Clear(anchor);
        if (previous != null) Array.Clear(previous);
        anchor = previous = null; frames = 0; Seconds = 0;
    }
    public bool Update(byte[] pixels, bool acceptable, double now)
    {
        if (!acceptable) { Reset(); return false; }
        if (anchor == null || anchor.Length != pixels.Length || now - last > 1.0 ||
            Difference(anchor, pixels) > 8 || Difference(previous!, pixels) > 5)
        {
            Reset(); anchor = (byte[])pixels.Clone(); previous = (byte[])pixels.Clone(); started = last = now; frames = 1;
            return false;
        }
        pixels.CopyTo(previous!, 0); last = now; frames++; Seconds = now - started;
        return Seconds >= 2 && frames >= 6;
    }
    static double Difference(byte[] a, byte[] b)
    {
        long total = 0;
        for (int i = 0; i < a.Length; i++) total += Math.Abs(a[i] - b[i]);
        return (double)total / a.Length;
    }
}

sealed class VariantTracker
{
    readonly List<byte[]> signatures = [];
    static byte[] Signature(CaptureData frame)
    {
        var values = new byte[32 * 48];
        for (int y = 0; y < 48; y++) for (int x = 0; x < 32; x++)
            values[y * 32 + x] = frame.Pixels[Math.Min(frame.Height - 1, y * frame.Height / 48) * frame.Width + Math.Min(frame.Width - 1, x * frame.Width / 32)];
        return values;
    }
    public bool IsDistinct(CaptureData frame)
    {
        var signature = Signature(frame);
        try
        {
            foreach (var existing in signatures)
            {
                double offset = signature.Zip(existing, (a, b) => (double)a - b).Average();
                double difference = signature.Zip(existing, (a, b) => Math.Abs(a - b - offset)).Average();
                if (difference < 6) return false;
            }
            return true;
        }
        finally { Array.Clear(signature); }
    }
    public void Add(CaptureData frame) => signatures.Add(Signature(frame));
    public void Clear() { foreach (var signature in signatures) Array.Clear(signature); signatures.Clear(); }
}
