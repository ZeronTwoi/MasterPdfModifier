using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PdfModifier.Core;

/// <summary>Settings of the process that decide how fast its work gets done.</summary>
public static class Performance
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeed = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(nint process, int informationClass, ref PowerThrottlingState information, uint size);

    /// <summary>
    /// Asks Windows not to slow this process down to save power. Windows does that to programs that are not in the
    /// foreground, and on processors with separate fast and frugal cores it moves their work to the frugal ones:
    /// recognising the text of a page then takes five times as long (measured: 7 s instead of 1.3 s). Someone who
    /// starts a long job here and turns to another window while it runs wants it done, not saved on.
    /// Does nothing on other systems, or where Windows does not know the setting.
    /// </summary>
    public static void RunAtFullSpeed()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // The control mask names what is being decided; a zero state says "do not throttle".
            var state = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed, StateMask = 0 };
            SetProcessInformation(Process.GetCurrentProcess().Handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            // An older Windows: nothing to switch off there.
        }
    }
}
