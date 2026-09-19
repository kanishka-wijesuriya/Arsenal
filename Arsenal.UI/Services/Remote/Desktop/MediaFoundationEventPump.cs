using Arsenal.Helpers;
using System.Runtime.InteropServices;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// Turns an asynchronous transform's callbacks into something a loop can wait on.
/// </summary>
/// <remarks>
/// A hardware encoder does not encode when asked. It raises METransformNeedInput when
/// it has room for a frame and METransformHaveOutput when it has coded one, and it
/// raises them on Media Foundation's own work queue threads rather than on the caller's.
///
/// <para>The obvious way to read those is <c>GetEvent</c>, which the interface offers
/// and which never returns anything: every hardware encoder tried here answers
/// MF_E_NO_EVENTS_AVAILABLE forever, because the driver's transform only begins
/// queueing once a callback has been registered. So a callback is registered, and this
/// class converts what arrives on it into two counting semaphores the capture thread
/// can wait on with a timeout.</para>
///
/// <para>The callback re-arms itself: <c>BeginGetEvent</c> delivers exactly one event,
/// so the last thing Invoke does is ask for the next one. Stopping therefore means
/// refusing to re-arm rather than cancelling anything, which is why <see cref="Stop"/>
/// is safe to call from the thread that is also waiting on the semaphores.</para>
/// </remarks>
internal sealed class MediaFoundationEventPump : MF.IMFAsyncCallback, IDisposable
{
    private readonly MF.IMFMediaEventGenerator _events;
    private readonly SemaphoreSlim _needInput = new(0);
    private readonly SemaphoreSlim _haveOutput = new(0);
    private readonly object _gate = new();

    /// <summary>Held for the whole of a callback, so teardown can wait one out.</summary>
    private readonly object _callbackGate = new();
    private bool _running;
    private bool _disposed;

    internal MediaFoundationEventPump(MF.IMFMediaEventGenerator events) => _events = events;

    /// <summary>Set when the transform reports a failure through its event queue.</summary>
    internal int FaultStatus { get; private set; }

    internal bool Start()
    {
        lock (_gate)
        {
            if (_running) return true;
            _running = true;
        }
        return Arm();
    }

    /// <summary>
    /// Stops the pump, and does not return until no callback is running.
    /// </summary>
    /// <remarks>
    /// The waiting is the point. Invoke runs on a Media Foundation work queue thread
    /// and touches both the transform and the semaphores; the caller's next act is to
    /// release the transform. Without this the two race, and the mild version of losing
    /// that race is the two exceptions it used to log ("COM object separated from its
    /// underlying RCW", "cannot access a disposed object"). The severe version is the
    /// callback reaching through a released interface pointer, which is not an
    /// exception at all.
    /// </remarks>
    internal void Stop()
    {
        // Taking the same gate Invoke holds means this blocks until any callback in
        // flight has finished, and _running being false stops the next one from
        // starting or re-arming.
        lock (_callbackGate)
        {
            lock (_gate) _running = false;
        }

        // Anything already waiting is released so the capture thread can finish rather
        // than sit out its timeout on an encoder that is going away.
        _needInput.Release();
        _haveOutput.Release();
    }

    internal bool WaitForInput(TimeSpan timeout) => Wait(_needInput, timeout);

    internal bool WaitForOutput(TimeSpan timeout) => Wait(_haveOutput, timeout);

    /// <summary>Takes an output token if one is already waiting, without blocking.</summary>
    internal bool TryTakeOutput() => _haveOutput.Wait(0) && Running;

    private bool Running
    {
        get { lock (_gate) return _running; }
    }

    private bool Wait(SemaphoreSlim semaphore, TimeSpan timeout)
    {
        if (!semaphore.Wait(timeout)) return false;

        // Stop releases both semaphores to wake whoever is waiting. That release is a
        // signal to give up, not a token to act on.
        return Running;
    }

    private bool Arm()
    {
        try
        {
            int hr = _events.BeginGetEvent(this, null);
            if (hr == MF.S_OK) return true;
            Logger.WriteLine($"Remote encoder event pump: BeginGetEvent 0x{hr:X8}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote encoder event pump: " + ex.Message);
            return false;
        }
    }

    // ---- IMFAsyncCallback --------------------------------------------------------

    /// <summary>
    /// No flags, default queue.
    /// </summary>
    /// <remarks>
    /// Returning E_NOTIMPL is also legal and is what most samples do, but it makes
    /// Media Foundation log a failure per event. Answering the question costs nothing.
    /// </remarks>
    public int GetParameters(out uint flags, out uint queue)
    {
        flags = 0;
        queue = 0;
        return MF.S_OK;
    }

    public int Invoke(MF.IMFAsyncResult result)
    {
        // Held for the whole callback so Stop cannot release the transform underneath
        // it. Re-entrant for this thread, so the Arm below is safe inside it.
        lock (_callbackGate)
        {
            if (!Running) return MF.S_OK;

            try
            {
                int hr = _events.EndGetEvent(result, out MF.IMFMediaEvent? mediaEvent);
                if (hr != MF.S_OK || mediaEvent is null) return MF.S_OK;

                try
                {
                    if (mediaEvent.GetStatus(out int status) == MF.S_OK && status != MF.S_OK)
                    {
                        FaultStatus = status;
                    }

                    if (mediaEvent.GetType(out uint eventType) == MF.S_OK)
                    {
                        if (eventType == MF.METransformNeedInput) _needInput.Release();
                        else if (eventType == MF.METransformHaveOutput) _haveOutput.Release();
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(mediaEvent);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Remote encoder event: " + ex.Message);
            }

            // One BeginGetEvent yields one event, so the next one has to be asked for.
            if (Running) Arm();
            return MF.S_OK;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();

        // The semaphores are deliberately not disposed. A capture thread can still be
        // inside a timed Wait on one of them when the session ends, and disposing it
        // underneath that thread throws where releasing it simply wakes it. Neither
        // holds an OS handle unless AvailableWaitHandle is asked for, which it never
        // is here, so there is nothing to leak.
    }
}
