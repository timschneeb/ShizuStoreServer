namespace ShizuAppStoreServer.Sync;

/// <summary>
/// Wake-up latch between the admin webhook and the fast loop (singleton).
/// <see cref="Request"/> marks a pending run and wakes a waiting worker; the
/// worker consumes the flag when it starts the run, so a request that lands
/// while a pass is running leaves the flag set and turns into an immediate
/// follow-up pass instead of waiting for the next tick.
/// </summary>
public class SyncSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _next = NewSource();
    private bool _pending;

    /// <summary>Marks a run pending and wakes the worker.</summary>
    public virtual void Request()
    {
        TaskCompletionSource wake;
        lock (_gate)
        {
            _pending = true;
            wake = _next;
            _next = NewSource();
        }

        wake.TrySetResult();
    }

    /// <summary>Returns true exactly once per pending request and clears the flag.</summary>
    public virtual bool ConsumePending()
    {
        lock (_gate)
        {
            var pending = _pending;
            _pending = false;
            return pending;
        }
    }

    /// <summary>
    /// Waits until a request arrives or <paramref name="timeout"/> elapses.
    /// Returns true when a request woke the wait; false on timeout/shutdown.
    /// </summary>
    public virtual async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        TaskCompletionSource wake;
        lock (_gate)
        {
            if (_pending)
            {
                // A request that arrived before the waiter registered must not
                // wait for the next wake-up swap.
                return true;
            }

            wake = _next;
        }

        var completed = await Task.WhenAny(wake.Task, Task.Delay(timeout, ct));
        return completed == wake.Task;
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
