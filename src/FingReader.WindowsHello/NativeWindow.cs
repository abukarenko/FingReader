using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FingReader.WindowsHello;

internal static class NativeWindow
{
    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string[] BrokerPaths =
    [
        Path.Combine(WindowsDirectory, "System32", "CredentialUIBroker.exe"),
        Path.Combine(WindowsDirectory, "SysWOW64", "CredentialUIBroker.exe")
    ];
    internal static nint Foreground => GetForegroundWindow();

    internal static bool IsValidOwner(nint window)
    {
        if (window == 0 || !IsWindow(window) || !IsWindowVisible(window) || IsIconic(window)
            || GetAncestor(window, 2) != window) return false;
        GetWindowThreadProcessId(window, out var pid);
        return pid == Environment.ProcessId;
    }

    internal static async Task AssistForegroundAsync(nint owner, HashSet<nint> originalWindows, Task request, CancellationToken token)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var session = process.SessionId;
            var clock = Stopwatch.StartNew();
            var attempts = 0;
            while (!request.IsCompleted && clock.ElapsedMilliseconds < 5000 && IsWindow(owner))
            {
                await Task.Delay(100, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || request.IsCompleted) return;
                var candidates = Windows().Where(w => !originalWindows.Contains(w)
                    && IsWindowVisible(w) && IsCredentialBroker(w, session)).ToArray();
                var popup = SingleCandidate(candidates);
                if (popup == 0) continue;
                var foreground = Foreground;
                if (foreground == popup) return;
                if (!MayTransfer(owner, foreground)) return;
                GetWindowThreadProcessId(popup, out var pid);
                AllowSetForegroundWindow(pid);
                if (SetForegroundWindow(popup) || ++attempts >= 3) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Focus assistance must never grant or deny verification. */ }
    }

    internal static nint SingleCandidate(nint[] candidates) => candidates.Length == 1 ? candidates[0] : 0;
    internal static bool MayTransfer(nint owner, nint foreground) => foreground == owner || foreground == 0;

    internal static List<nint> Windows()
    {
        var result = new List<nint>();
        EnumWindows((hwnd, _) => { result.Add(hwnd); return true; }, 0);
        return result;
    }

    private static bool IsCredentialBroker(nint window, int session)
    {
        GetWindowThreadProcessId(window, out var pid);
        if (!ProcessIdToSessionId(pid, out var targetSession) || targetSession != session) return false;
        var process = OpenProcess(0x1000, false, pid);
        if (process == 0) return false;
        try
        {
            var path = new StringBuilder(1024);
            var length = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length)
                && BrokerPaths.Any(p => string.Equals(path.ToString(), p, StringComparison.OrdinalIgnoreCase));
        }
        finally { CloseHandle(process); }
    }

    private delegate bool EnumWindowProc(nint hwnd, nint state);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowProc callback, nint state);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint pid, out int session);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint process);
}
