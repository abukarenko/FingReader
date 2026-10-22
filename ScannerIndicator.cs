namespace FS80H;

// Called only by the worker owning the scanner handle; no concurrent SDK timer calls.
sealed class ScannerIndicator : IDisposable
{
    readonly Action<bool> write;
    readonly Action<string>? warning;
    bool? lastOn;
    bool waiting, disabled;
    double phase;
    public ScannerIndicator(Action<bool> write, Action<string>? warning = null)
    {
        this.write = write; this.warning = warning; Set(true);
    }
    public void Update(bool isWaiting, double now)
    {
        if (waiting != isWaiting) { waiting = isWaiting; phase = now; }
        Set(!waiting || ((long)((now - phase) / .5) % 2 == 0));
    }
    void Set(bool on)
    {
        if (disabled || lastOn == on) return;
        try { write(on); lastOn = on; }
        catch (Exception ex) { disabled = true; Warn(ex.Message); }
    }
    public void Dispose()
    {
        // Always attempt OFF, including after an earlier write failure.
        try { write(false); lastOn = false; }
        catch (Exception ex) { if (!disabled) Warn(ex.Message); }
    }
    void Warn(string message) { try { warning?.Invoke(message); } catch { /* Optional UI notification must not interrupt capture. */ } }
}
