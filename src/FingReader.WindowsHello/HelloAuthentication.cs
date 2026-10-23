using Windows.Security.Credentials.UI;

namespace FingReader.WindowsHello;

/// <summary>Verifies the current Windows user. Does not store credentials or grant application roles.</summary>
public static class HelloAuthentication
{
    private static readonly SemaphoreSlim RequestGate = new(1, 1);

    /// <summary>Checks configuration without displaying an authentication prompt.</summary>
    public static async Task<HelloAvailability> CheckAvailabilityAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return HelloAvailability.UnsupportedSystem;
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync().AsTask().ConfigureAwait(false);
            return availability switch
            {
                UserConsentVerifierAvailability.Available => HelloAvailability.Available,
                UserConsentVerifierAvailability.DeviceNotPresent => HelloAvailability.DeviceNotPresent,
                UserConsentVerifierAvailability.NotConfiguredForUser => HelloAvailability.NotConfiguredForUser,
                UserConsentVerifierAvailability.DisabledByPolicy => HelloAvailability.DisabledByPolicy,
                UserConsentVerifierAvailability.DeviceBusy => HelloAvailability.DeviceBusy,
                _ => HelloAvailability.Error
            };
        }
        catch (Exception) { return HelloAvailability.Error; }
    }

    /// <summary>Shows Windows Hello for the current process's active top-level window.</summary>
    /// <remarks>Call from an interactive desktop app with a live, visible, foreground HWND.
    /// Cancellation is best effort; a canceled request can never return IsVerified=true.
    /// The host must invalidate results if its session or window closes or locks.</remarks>
    public static Task<HelloResult> VerifyAsync(nint ownerWindow,
        string message = "Подтвердите вход в приложение.", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > 512) throw new ArgumentOutOfRangeException(nameof(message), "Use at most 512 characters.");
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(new HelloResult(HelloStatus.Canceled));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return Task.FromResult(new HelloResult(HelloStatus.UnsupportedSystem));
        if (!NativeWindow.IsValidOwner(ownerWindow)) return Task.FromResult(new HelloResult(HelloStatus.InvalidOwnerWindow));
        if (NativeWindow.Foreground != ownerWindow) return Task.FromResult(new HelloResult(HelloStatus.OwnerNotActive));
        return RunExclusiveAsync(() => RequestAsync(ownerWindow, message, cancellationToken), cancellationToken);
    }

    internal static async Task<HelloResult> RunExclusiveAsync(Func<Task<HelloResult>> request, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(HelloStatus.Canceled);
        if (!RequestGate.Wait(0)) return new(HelloStatus.DeviceBusy);
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await request().ConfigureAwait(false);
            return token.IsCancellationRequested ? new(HelloStatus.Canceled) : result;
        }
        catch (OperationCanceledException) { return new(HelloStatus.Canceled); }
        catch (Exception ex) { return new(HelloStatus.Error, ex.HResult); }
        finally { RequestGate.Release(); }
    }

    private static async Task<HelloResult> RequestAsync(nint owner, string message, CancellationToken token)
    {
        var originalWindows = NativeWindow.Windows().ToHashSet();
        using var focusCancellation = new CancellationTokenSource();
        var request = Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            var operation = UserConsentVerifierInterop.RequestVerificationForWindowAsync(owner, message);
            using var cancellation = token.Register(() =>
            {
                try { operation.Cancel(); }
                catch (Exception) { /* Native cancellation is best effort. */ }
            });
            var result = await operation.AsTask().ConfigureAwait(false);
            return MapResult(result);
        });
        var focus = NativeWindow.AssistForegroundAsync(owner, originalWindows, request, focusCancellation.Token);
        try { return await request.ConfigureAwait(false); }
        finally
        {
            focusCancellation.Cancel();
            await focus.ConfigureAwait(false);
        }
    }

    internal static HelloResult MapResult(UserConsentVerificationResult result) => new(result switch
    {
        UserConsentVerificationResult.Verified => HelloStatus.Verified,
        UserConsentVerificationResult.Canceled => HelloStatus.Canceled,
        UserConsentVerificationResult.DeviceNotPresent => HelloStatus.DeviceNotPresent,
        UserConsentVerificationResult.NotConfiguredForUser => HelloStatus.NotConfiguredForUser,
        UserConsentVerificationResult.DisabledByPolicy => HelloStatus.DisabledByPolicy,
        UserConsentVerificationResult.DeviceBusy => HelloStatus.DeviceBusy,
        UserConsentVerificationResult.RetriesExhausted => HelloStatus.RetriesExhausted,
        _ => HelloStatus.Error
    });
}
