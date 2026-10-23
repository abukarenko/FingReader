namespace HelloUnlock;

// A result from an old prompt must not unlock a session that was locked meanwhile.
internal sealed class UnlockSession
{
    private long generation;
    public bool IsUnlocked { get; private set; }

    public long BeginAttempt()
    {
        Lock();
        return generation;
    }

    public bool Complete(long attempt, bool verified)
    {
        if (attempt != generation) return false;
        generation++;
        IsUnlocked = verified;
        return IsUnlocked;
    }

    public void Lock()
    {
        generation++;
        IsUnlocked = false;
    }
}
