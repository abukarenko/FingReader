using FingReader.WindowsHello;
using Windows.Security.Credentials.UI;

var assertions = 0;
void Check(bool ok, string label)
{
    assertions++;
    if (!ok) throw new InvalidOperationException(label);
}

Check(!new HelloResult().IsVerified, "default fails closed");
foreach (var status in Enum.GetValues<HelloStatus>())
    Check(new HelloResult(status).IsVerified == (status == HelloStatus.Verified), "only verified grants access");
foreach (var value in Enum.GetValues<UserConsentVerificationResult>())
    Check(HelloAuthentication.MapResult(value).IsVerified == (value == UserConsentVerificationResult.Verified), "native mapping");
Check(!HelloAuthentication.MapResult((UserConsentVerificationResult)123456).IsVerified, "unknown native result");
Check((await HelloAuthentication.VerifyAsync(0)).Status == HelloStatus.InvalidOwnerWindow, "invalid HWND never prompts");
using var canceled = new CancellationTokenSource();
canceled.Cancel();
Check((await HelloAuthentication.VerifyAsync(0, cancellationToken: canceled.Token)).Status == HelloStatus.Canceled, "pre-canceled request never prompts");
var rejectedBlank = false;
try { await HelloAuthentication.VerifyAsync(0, " "); } catch (ArgumentException) { rejectedBlank = true; }
Check(rejectedBlank, "blank message rejected");
var rejectedLong = false;
try { await HelloAuthentication.VerifyAsync(0, new string('a', 513)); } catch (ArgumentOutOfRangeException) { rejectedLong = true; }
Check(rejectedLong, "long message rejected");

var completion = new TaskCompletionSource<HelloResult>(TaskCreationOptions.RunContinuationsAsynchronously);
var first = HelloAuthentication.RunExclusiveAsync(() => completion.Task, default);
var secondInvoked = false;
var second = await HelloAuthentication.RunExclusiveAsync(() => { secondInvoked = true; return Task.FromResult(new HelloResult(HelloStatus.Verified)); }, default);
Check(second.Status == HelloStatus.DeviceBusy && !secondInvoked, "overlap refused without opening another prompt");
completion.SetResult(new(HelloStatus.Verified));
Check((await first).IsVerified, "original request completes");

using var late = new CancellationTokenSource();
var lateCompletion = new TaskCompletionSource<HelloResult>(TaskCreationOptions.RunContinuationsAsynchronously);
var lateResult = HelloAuthentication.RunExclusiveAsync(() => lateCompletion.Task, late.Token);
late.Cancel();
lateCompletion.SetResult(new(HelloStatus.Verified));
Check(!(await lateResult).IsVerified && (await lateResult).Status == HelloStatus.Canceled, "cancellation beats late success");
var error = await HelloAuthentication.RunExclusiveAsync(() => throw new InvalidOperationException("simulated"), default);
Check(error.Status == HelloStatus.Error && !error.IsVerified && error.HResult != 0, "exceptions fail closed");
Check((await HelloAuthentication.RunExclusiveAsync(() => Task.FromResult(new HelloResult(HelloStatus.Verified)), default)).IsVerified, "gate released after exception");
Check((await HelloAuthentication.RunExclusiveAsync(() => throw new OperationCanceledException(), default)).Status == HelloStatus.Canceled, "native cancellation mapped");

Check(NativeWindow.SingleCandidate([]) == 0, "no target means no activation");
Check(NativeWindow.SingleCandidate([1, 2]) == 0, "ambiguous targets never guessed");
Check(NativeWindow.SingleCandidate([42]) == 42, "unique candidate");
Check(NativeWindow.MayTransfer(1, 1), "owner has foreground");
Check(NativeWindow.MayTransfer(1, 0), "no foreground");
Check(!NativeWindow.MayTransfer(1, 2), "switch to another app respected");

Console.WriteLine($"PASS: {assertions} checks. No authentication dialog, fingerprint or PIN was used.");
