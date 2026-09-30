using AirSend.Core;
using AirSend.Core.Logging;

namespace AirSend.Services;

/// <summary>
/// Keeps one AirSend process per user session. Two instances would each open their
/// own mDNS sockets and their own HomePod session, and the receiver would keep
/// switching between them, which looks like "connections getting mixed up".
/// </summary>
/// <remarks>
/// The second instance signals the first one to show its window and then exits, so
/// launching the app again behaves like every other Windows utility.
/// </remarks>
public static class SingleInstance
{
    private const string MutexName = @"Local\AirSend.SingleInstance";
    private const string ShowEventName = @"Local\AirSend.ShowWindow";

    private static Mutex? _mutex;

    public static bool IsPrimary { get; private set; }

    /// <summary>Returns true when this process owns the session (and should run).</summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsPrimary = createdNew;
        if (!createdNew)
        {
        AppLog.Info("log.instance.already_running");
        }

        return createdNew;
    }

    /// <summary>Asks the running instance to bring its window back.</summary>
    public static void SignalPrimaryInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ShowEventName);
            handle.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            // The other instance is shutting down: nothing to do.
        }
    }

    /// <summary>Starts listening for "show your window" requests from later launches.</summary>
    public static void StartListening(Action showWindow)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                handle.WaitOne();
                try
                {
                    showWindow();
                }
                catch (Exception ex)
                {
                    // Never let a wake-up request take the process down.
            AppLog.Warn("log.instance.wake_failed", new { err = AirSendError.Describe(ex) });
                }
            }
        })
        {
            IsBackground = true,
            Name = "airsend-single-instance",
        };
        thread.Start();
    }
}
