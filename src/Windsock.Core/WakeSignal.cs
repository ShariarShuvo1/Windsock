namespace Windsock.Core;

/// <summary>
/// The wake-up nudge the background services share: a semaphore of at most one,
/// released to make a loop look again now rather than at its next turn.
/// </summary>
public static class WakeSignal
{
    /// <summary>
    /// Releases the semaphore unless it is already released. A second nudge
    /// before the loop wakes adds nothing.
    /// </summary>
    /// <remarks>
    /// Each service used to call <see cref="SemaphoreSlim.Release()"/> and
    /// catch <see cref="SemaphoreFullException"/>, which threw (and was caught)
    /// whenever a nudge was already waiting - at some starts, every time. The
    /// count is looked at first now; two nudges racing past the look can still
    /// meet a full semaphore, so the catch stays for that.
    /// </remarks>
    public static void Signal(this SemaphoreSlim nudge)
    {
        ArgumentNullException.ThrowIfNull(nudge);

        if (nudge.CurrentCount > 0)
        {
            return;
        }

        try
        {
            nudge.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another nudge got there in between; one is all that is needed.
        }
    }
}
