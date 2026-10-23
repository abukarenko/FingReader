using Microsoft.Win32;
using System.Runtime.InteropServices;
using FingReader.WindowsHello;

namespace HelloUnlock;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTest();
        ApplicationConfiguration.Initialize();
        Application.Run(new UnlockForm());
        return 0;
    }

    private static int SelfTest()
    {
        var session = new UnlockSession();
        void Check(bool ok) { if (!ok) throw new InvalidOperationException("Session check failed"); }
        Check(!session.IsUnlocked);
        foreach (var result in Enum.GetValues<HelloStatus>())
        {
            var attempt = session.BeginAttempt();
            Check(!session.IsUnlocked);
            Check(session.Complete(attempt, result == HelloStatus.Verified)
                == (result == HelloStatus.Verified));
            Check(!session.Complete(attempt, true)); // No replay of a completed response.
        }
        var stale = session.BeginAttempt();
        session.Lock();
        Check(!session.Complete(stale, true) && !session.IsUnlocked);
        var old = session.BeginAttempt();
        var current = session.BeginAttempt();
        Check(!session.Complete(old, true) && !session.IsUnlocked);
        Check(session.Complete(current, true));
        session.Lock();
        Check(!session.IsUnlocked);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-result.txt"),
            "PASS: all verification results, initial lock, stale/replayed responses, new attempt and explicit lock. No authentication prompt or biometric access.");
        return 0;
    }
}

internal sealed class UnlockForm : Form
{
    private readonly UnlockSession session = new();
    private readonly Label heading = new() { AutoSize = true, Font = new("Segoe UI", 21, FontStyle.Bold) };
    private readonly Label status = new() { AutoSize = false, Height = 54 };
    private readonly Button lockButton = new() { Text = "Заблокировать", AutoSize = true, Height = 42 };
    private readonly Panel content = new() { BackColor = Color.FromArgb(232, 245, 236), Height = 125, Visible = false };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly CancellationTokenSource lifetime = new();
    private bool resourcesDisposed;
    private bool busy;
    private bool shown;
    private bool pendingVerification = true;
    private readonly System.Diagnostics.Stopwatch unlockedFor = new();
    private const int TimeoutSeconds = 60;

    public UnlockForm()
    {
        Text = "Hello Unlock — тест Windows Hello";
        Font = new("Segoe UI", 10);
        BackColor = Color.White;
        ClientSize = new(610, 435);
        MinimumSize = new(626, 474);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(26), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 53));
        layout.RowStyles.Add(new(SizeType.Absolute, 58));
        layout.RowStyles.Add(new(SizeType.Absolute, 58));
        layout.RowStyles.Add(new(SizeType.Absolute, 135));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 30));
        heading.Text = "Доступ закрыт";
        heading.ForeColor = Color.FromArgb(70, 77, 87);
        status.Text = "Подготовка Windows Hello…";
        status.Dock = DockStyle.Fill;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.Add(lockButton);
        content.Dock = DockStyle.Fill;
        content.Padding = new(15);
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        layout.Controls.Add(content, 0, 3);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Отпечаток или PIN проверяет Windows.\nАвтоблокировка через 60 секунд и при блокировке Windows.", ForeColor = Color.DimGray }, 0, 4);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Учебный пример • отпечатки и PIN приложение не получает", ForeColor = Color.DimGray, Font = new("Segoe UI", 9) }, 0, 5);
        Controls.Add(layout);
        lockButton.Enabled = false;
        lockButton.Click += (_, _) => Lock("Приложение заблокировано.");
        Shown += (_, _) => { shown = true; QueueVerification(); };
        Activated += (_, _) => QueueVerification();
        Deactivate += (_, _) =>
        {
            // After cancellation/errors, leaving and returning to the form is
            // an explicit retry. Closing Hello must not immediately reopen it.
            if (!busy && !session.IsUnlocked) pendingVerification = true;
        };
        timer.Tick += (_, _) =>
        {
            QueueVerification();
            if (!session.IsUnlocked) return;
            var remaining = TimeoutSeconds - (int)unlockedFor.Elapsed.TotalSeconds;
            if (remaining <= 0) Lock("Время доступа истекло. Подтвердите вход снова.");
            else status.Text = $"Windows подтвердила пользователя. Блокировка через {remaining} с.";
        };
        SystemEvents.SessionSwitch += SessionSwitch;
        timer.Start();
    }

    private void QueueVerification()
    {
        if (!shown || IsDisposed || !IsHandleCreated || busy || !pendingVerification
            || session.IsUnlocked || WindowState == FormWindowState.Minimized
            || GetForegroundWindow() != Handle) return;
        // VerifyAsync marks the attempt busy synchronously before the first await.
        _ = VerifyAsync();
    }

    private async Task VerifyAsync()
    {
        if (busy) return;
        busy = true;
        pendingVerification = false;
        var attempt = session.BeginAttempt();
        PaintLocked();
        status.Text = "Ожидаем подтверждение в системном окне…";
        try
        {
            // Open immediately while the form has foreground activation.
            // The request itself returns NotConfigured/Disabled/Busy as needed.
            if (!PreparePromptOwner())
            {
                session.Complete(attempt, false);
                pendingVerification = true;
                status.Text = "Откройте окно приложения для подтверждения входа.";
                return;
            }
            // Desktop apps must bind the system prompt to a real window HWND.
            var result = await HelloAuthentication.VerifyAsync(
                Handle, "Подтвердите разблокировку тестового приложения Hello Unlock.", lifetime.Token);
            if (IsDisposed) return;
            if (session.Complete(attempt, result.IsVerified))
            {
                unlockedFor.Restart();
                heading.Text = "Доступ открыт";
                heading.ForeColor = Color.FromArgb(25, 115, 60);
                content.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Добро пожаловать!\n\nЭтот раздел доступен после подтверждения Windows Hello.", ForeColor = Color.FromArgb(25, 90, 45) });
                content.Visible = true;
                lockButton.Enabled = true;
                status.Text = "Windows подтвердила пользователя. Доступ открыт на 60 секунд.";
            }
            else status.Text = result.IsVerified
                ? "Сеанс уже заблокирован. Подтвердите вход повторно." : ResultMessage(result.Status);
        }
        catch (Exception ex)
        {
            session.Complete(attempt, false);
            if (!IsDisposed) { PaintLocked(); status.Text = ErrorMessage(ex); }
        }
        finally
        {
            busy = false;
            if (!IsDisposed)
            {
                QueueVerification();
            }
        }
    }

    private bool PreparePromptOwner()
    {
        if (IsDisposed || !Visible) return false;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        // Windows owns the foreground policy. If activation was denied, don't
        // create a hidden prompt or bypass the foreground lock.
        return GetForegroundWindow() == Handle;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private void Lock(string message)
    {
        session.Lock();
        pendingVerification = true;
        unlockedFor.Reset();
        PaintLocked();
        status.Text = message;
        QueueVerification();
    }

    private void PaintLocked()
    {
        heading.Text = "Доступ закрыт";
        heading.ForeColor = Color.FromArgb(70, 77, 87);
        content.Visible = false;
        foreach (Control control in content.Controls.Cast<Control>().ToArray()) control.Dispose();
        content.Controls.Clear();
        lockButton.Enabled = false;
    }

    private void SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)) return;
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => Lock("Сеанс Windows заблокирован. Подтвердите вход повторно.")); }
        catch (InvalidOperationException) { /* Window was closed meanwhile. */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            lifetime.Cancel();
            lifetime.Dispose();
            SystemEvents.SessionSwitch -= SessionSwitch;
            timer.Dispose();
            session.Lock();
        }
        base.Dispose(disposing);
    }

    private static string ResultMessage(HelloStatus result) => result switch
    {
        HelloStatus.Canceled => "Подтверждение отменено. Доступ закрыт. Для повтора сверните и откройте окно снова.",
        HelloStatus.RetriesExhausted => "Попытки подтверждения исчерпаны. Доступ закрыт.",
        HelloStatus.DeviceBusy => "Средство подтверждения занято. Доступ закрыт.",
        HelloStatus.DeviceNotPresent => "Средство подтверждения не найдено. Доступ закрыт.",
        HelloStatus.NotConfiguredForUser => "Windows Hello не настроена для пользователя. Доступ закрыт.",
        HelloStatus.DisabledByPolicy => "Подтверждение запрещено политикой Windows. Доступ закрыт.",
        _ => "Windows не подтвердила пользователя. Доступ закрыт."
    };

    private static string ErrorMessage(Exception ex) => $"Не удалось выполнить проверку Windows Hello (0x{ex.HResult:X8}). Доступ закрыт.";
}
