using System.Runtime.InteropServices;
using SourceVault.Windows;

namespace SourceVault.Service;

internal sealed class ScmHost
{
    internal const string ServiceName = "SourceVault";
    private readonly Native.ServiceMain main;
    private readonly Native.Handler handler;
    private IntPtr statusHandle;
    private volatile ServiceLifecycle? lifecycle;
    private int failure;
    internal ScmHost() { main = ServiceMain; handler = HandleControl; }
    internal int Run()
    {
        if (!OperatingSystem.IsWindows()) return 3;
        var table = new[] { new Native.TableEntry { Name = ServiceName, Main = main }, new Native.TableEntry() };
        if (!Native.StartServiceCtrlDispatcherW(table)) return 3;
        GC.KeepAlive(main);
        GC.KeepAlive(handler);
        return failure;
    }
    private void ServiceMain(uint count, IntPtr arguments)
    {
        // No exception may cross an unmanaged callback boundary.
        try
        {
            statusHandle = Native.RegisterServiceCtrlHandlerExW(ServiceName, handler, IntPtr.Zero);
            if (statusHandle == IntPtr.Zero) { failure = 3; return; }
            using var instance = new ServiceLifecycle(Report);
            lifecycle = instance;
            instance.Run(() =>
            {
                TpmKeyProtector.RequireServiceIdentity();
                var config = ServiceConfiguration.LoadFromApplicationDirectory();
                // Validates existing key only. No vault, pipe, document, or build endpoint exists here.
                return TpmKeyProtector.OpenExisting(config.KeyName);
            });
        }
        catch { failure = 3; }
        finally { lifecycle = null; }
    }
    private uint HandleControl(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        try
        {
            if (control is 1 or 5) { lifecycle?.RequestStop(); return 0; }
            if (control == 4) return 0; // Interrogate: status unchanged; no redundant SetServiceStatus call.
            return 120; // ERROR_CALL_NOT_IMPLEMENTED.
        }
        catch { failure = 3; return 1064; }
    }
    private void Report(Status value)
    {
        var status = new Native.ServiceStatus
        {
            Type = 0x10, State = (uint)value.State, Accepted = value.AcceptedControls,
            Win32ExitCode = value.ExitCode, ServiceSpecificExitCode = 0,
            Checkpoint = value.Checkpoint, WaitHint = value.WaitHint
        };
        if (!Native.SetServiceStatus(statusHandle, ref status)) throw new InvalidOperationException();
        if (value.ExitCode != 0) failure = 3;
    }
    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void ServiceMain(uint count, IntPtr arguments);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct TableEntry
        {
            [MarshalAs(UnmanagedType.LPWStr)] internal string? Name;
            internal ServiceMain? Main;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceStatus
        {
            internal uint Type, State, Accepted, Win32ExitCode, ServiceSpecificExitCode, Checkpoint, WaitHint;
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool StartServiceCtrlDispatcherW([In] TableEntry[] table);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern IntPtr RegisterServiceCtrlHandlerExW(string name, Handler handler, IntPtr context);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
    }
}
