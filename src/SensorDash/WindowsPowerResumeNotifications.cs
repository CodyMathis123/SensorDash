using System.Runtime.InteropServices;

// Receives actual Windows sleep/resume notifications in the elevated collector.
// The native callback never touches LHM, the fan writer, or other hardware;
// it only queues a single resume flag for the serial collection thread.
internal sealed class WindowsPowerResumeNotifications : IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PbtResumeAutomatic = 0x12;
    private const uint PbtResumeCritical = 0x06;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Callback(IntPtr context, uint eventType, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct SubscribeParameters
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterSuspendResumeNotification", SetLastError = true)]
    private static extern IntPtr Register(ref SubscribeParameters recipient, uint flags);

    [DllImport("user32.dll", EntryPoint = "UnregisterSuspendResumeNotification", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Unregister(IntPtr registration);

    private readonly Callback _callback;
    private readonly IntPtr _registration;
    private int _pending;
    private int _closed;

    public WindowsPowerResumeNotifications(ILogger logger)
    {
        _callback = OnPowerNotification; // root the delegate until we unregister
        try
        {
            var args = new SubscribeParameters
            {
                Callback = Marshal.GetFunctionPointerForDelegate(_callback),
                Context = IntPtr.Zero
            };
            _registration = Register(ref args, DeviceNotifyCallback);
            if (_registration == IntPtr.Zero)
                logger.LogWarning("Windows resume notifications unavailable (Win32 error {Error}); polling-gap recovery remains active.", Marshal.GetLastWin32Error());
            else
                logger.LogInformation("Windows resume notifications registered for collector fan rearm.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Windows resume registration failed; polling-gap recovery remains active.");
        }
    }

    private uint OnPowerNotification(IntPtr context, uint eventType, IntPtr setting)
    {
        // PBT_APMRESUMEAUTOMATIC is delivered for both unattended and
        // user-initiated wakes. Do NOT also respond to RESUMESUSPEND (0x7):
        // it follows AUTOMATIC on user wake and would duplicate reinitialization.
        if (eventType is PbtResumeAutomatic or PbtResumeCritical &&
            Volatile.Read(ref _closed) == 0)
            Interlocked.Exchange(ref _pending, 1);
        return 0;
    }

    internal bool ConsumePending() => Interlocked.Exchange(ref _pending, 0) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        if (_registration != IntPtr.Zero)
            try { Unregister(_registration); } catch { /* collector shutdown */ }
        GC.KeepAlive(_callback);
    }
}
