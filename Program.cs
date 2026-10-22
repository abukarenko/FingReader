using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FS80H;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

record CaptureData(int Width, int Height, byte[] Pixels, DateTimeOffset CapturedAt);

static class Scanner
{
    static readonly object DeviceGate = new();
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct ImageSize { public int Width, Height, Size; }
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Winapi)] static extern IntPtr ftrScanOpenDevice();
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Winapi)] static extern void ftrScanCloseDevice(IntPtr handle);
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ftrScanGetImageSize(IntPtr handle, out ImageSize size);
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ftrScanGetFrame(IntPtr handle, [Out] byte[] pixels, IntPtr parameters);
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Winapi)] static extern uint ftrScanGetLastError();
    [DllImport("ftrScanAPI.dll", CallingConvention = CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ftrScanSetDiodesStatus(IntPtr handle, byte green, byte red);
    static Scanner()
    {
        NativeLibrary.SetDllImportResolver(typeof(Scanner).Assembly, (name, assembly, searchPath) =>
        {
            if (name != "ftrScanAPI.dll") return IntPtr.Zero;
            string path = Path.Combine(AppContext.BaseDirectory, "native", "ftrScanAPI.dll");
            if (!File.Exists(path)) throw new DllNotFoundException($"Нет native\\ftrScanAPI.dll рядом с приложением. Нужна DLL {(Environment.Is64BitProcess ? "x64" : "x86")} из официального SDK.");
            return NativeLibrary.Load(path);
        });
    }
    public static void SetIndicator(bool on)
    {
        lock (DeviceGate)
        {
            IntPtr handle = ftrScanOpenDevice();
            if (handle == IntPtr.Zero) throw Error("Не удалось открыть сканер для индикатора");
            try { WriteIndicator(handle, on); }
            finally { ftrScanCloseDevice(handle); }
        }
    }
    static void WriteIndicator(IntPtr handle, bool on)
    {
        if (!ftrScanSetDiodesStatus(handle, on ? (byte)255 : (byte)0, 0)) throw Error("Не удалось переключить индикатор");
    }
    public static void Stream(CancellationToken token, Func<CaptureData?, uint, bool> receive, Func<bool>? isWaiting = null, Action<string>? indicatorWarning = null)
    {
        lock (DeviceGate) StreamCore(token, receive, isWaiting, indicatorWarning);
    }
    static void StreamCore(CancellationToken token, Func<CaptureData?, uint, bool> receive, Func<bool>? isWaiting, Action<string>? indicatorWarning)
    {
        IntPtr handle = IntPtr.Zero;
        byte[]? pixels = null;
        ScannerIndicator? indicator = null;
        try
        {
            token.ThrowIfCancellationRequested();
            handle = ftrScanOpenDevice();
            if (handle == IntPtr.Zero) throw Error("Не удалось открыть сканер");
            indicator = new ScannerIndicator(on => WriteIndicator(handle, on), indicatorWarning);
            if (!ftrScanGetImageSize(handle, out var size)) throw Error("Не удалось получить размер изображения");
            if (size.Width <= 0 || size.Height <= 0 || size.Size <= 0 || size.Size > 32_000_000 || (long)size.Width * size.Height != size.Size)
                throw new InvalidOperationException("SDK вернул неожиданный размер 8-битного изображения.");
            pixels = new byte[size.Size];
            while (true)
            {
                long cycleStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                token.ThrowIfCancellationRequested();
                indicator.Update(isWaiting?.Invoke() ?? false, StabilityTracker.Now);
                if (ftrScanGetFrame(handle, pixels, IntPtr.Zero))
                {
                    token.ThrowIfCancellationRequested();
                    if (!receive(new CaptureData(size.Width, size.Height, pixels, DateTimeOffset.UtcNow), 0)) return;
                    Pace(token, cycleStarted, 33);
                    continue;
                }
                uint error = ftrScanGetLastError();
                // Empty frame, movable finger and no frame: retry until cancellation/deadline.
                if (error != 4306 && error != 0x20000001 && error != 0x20000002)
                    throw new InvalidOperationException($"Ошибка сканирования SDK: {error} (0x{error:X8}). Проверьте USB, драйвер и закройте другие программы сканера.");
                if (!receive(null, error)) return;
                Pace(token, cycleStarted, 20);
            }
        }
        catch (BadImageFormatException) { throw new InvalidOperationException($"Разрядность DLL не совпадает с приложением {(Environment.Is64BitProcess ? "x64" : "x86")}. Нужна DLL и её зависимости той же разрядности."); }
        catch (EntryPointNotFoundException) { throw new InvalidOperationException("DLL не содержит нужных функций ftrScanAPI. Используйте совместимый официальный SDK."); }
        catch (DllNotFoundException ex) { throw new InvalidOperationException("Не загружена native\\ftrScanAPI.dll или её зависимости. " + ex.Message); }
        finally { indicator?.Dispose(); if (handle != IntPtr.Zero) ftrScanCloseDevice(handle); if (pixels != null) Array.Clear(pixels); }
    }
    static void Pace(CancellationToken token, long cycleStarted, int minimumCycleMilliseconds)
    {
        // Hardware/SDK time counts toward the limit; slow captures get no added delay.
        int remaining = minimumCycleMilliseconds - (int)System.Diagnostics.Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
        if (remaining > 0 && token.WaitHandle.WaitOne(remaining)) token.ThrowIfCancellationRequested();
    }
    static Exception Error(string action) { uint code = ftrScanGetLastError(); return new InvalidOperationException($"{action}. Код SDK: {code} (0x{code:X8}). Проверьте подключение и драйвер; закройте другие приложения сканера."); }
}

static class Storage
{
    public static string Save(string directory, string name, CaptureData capture, CaptureInfo? info = null)
    {
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(directory, $"fingerprint_{capture.CapturedAt:yyyyMMdd_HHmmss}_{id}.zip");
        bool created = false;
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            using (var data = zip.CreateEntry("image.raw", CompressionLevel.Optimal).Open()) data.Write(capture.Pixels);
            using (var png = zip.CreateEntry("image.png", CompressionLevel.NoCompression).Open())
            using (var bitmap = PreviewImage.Create(capture)) bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            using (var metadata = zip.CreateEntry("metadata.json").Open())
                JsonSerializer.Serialize(metadata, new { schemaVersion = 3, alias = name,
                    capturedAtUtc = capture.CapturedAt, width = capture.Width, height = capture.Height,
                    pixelFormat = "Gray8", imageFile = "image.raw", previewFile = "image.png", enrollment = info, source = "Futronic ftrScanAPI ftrScanGetFrame" }, new JsonSerializerOptions { WriteIndented = true });
            return path;
        }
        catch { if (created) { try { File.Delete(path); } catch { /* Do not mask the original write failure. */ } } throw; }
    }
}
