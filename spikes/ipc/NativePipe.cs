using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SourceVault.IpcSpike;

internal sealed record Peer(string Sid, uint Session, uint ProcessId);
internal sealed record PeerPolicy(string Sid, uint Session, uint? ProcessId = null)
{
    public void Validate(Peer peer)
    {
        if (peer.Sid != Sid || peer.Session != Session || (ProcessId.HasValue && peer.ProcessId != ProcessId))
            throw new UnauthorizedAccessException("Peer policy rejected.");
    }
}

internal static class NativePipe
{
    // FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE; excludes
    // FILE_CREATE_PIPE_INSTANCE (0x4), unlike FILE_GENERIC_WRITE.
    internal const uint ClientRights = 0x00100083;
    internal static Peer Current
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return new Peer(identity.User!.Value, unchecked((uint)process.SessionId), unchecked((uint)Environment.ProcessId));
        }
    }

    internal static NamedPipeServerStream Create(string name, string clientSid)
    {
        if (!new SecurityIdentifier(clientSid).IsAccountSid()) throw new ArgumentException("Account SID required.");
        // Protected DACL: SYSTEM and the configured principal receive exact data rights.
        // This interactive spike has no service identity. Production provisioning must
        // add the distinct service SID's explicit server rights, with no operator create-instance grant.
        string sddl = $"D:P(A;;0x{ClientRights:X};;;SY)(A;;0x{ClientRights:X};;;{clientSid})";
        Check(ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _));
        try
        {
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe($@"\\.\pipe\{name}", 0x00000003 | 0x00080000 | 0x40000000,
                0x00000008, 1, 65536, 65536, 0, ref sa); // FIRST_INSTANCE, OVERLAPPED, REJECT_REMOTE_CLIENTS
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }

    internal static NamedPipeClientStream Connect(string name, PeerPolicy expectedServer)
    {
        var handle = CreateFile($@"\\.\pipe\{name}", ClientRights, 0, IntPtr.Zero, 3,
            0x40000000 | 0x00100000 | 0x00010000, IntPtr.Zero); // OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        var pipe = new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
        try
        {
            Check(GetNamedPipeServerProcessId(handle, out uint pid));
            expectedServer.Validate(ProcessPeer(pid));
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    internal static Peer ClientPeer(NamedPipeServerStream pipe)
    {
        Check(GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid));
        Peer? peer = null;
        // Call after the first frame was read: the OS pipe token reflects the client
        // security context of that write, including thread impersonation, not JSON claims.
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(true) ?? throw new UnauthorizedAccessException();
            Check(GetTokenInformation(identity.AccessToken, 12, out uint session, sizeof(uint), out _));
            peer = new Peer(identity.User!.Value, session, pid);
        });
        return peer ?? throw new UnauthorizedAccessException();
    }

    private static Peer ProcessPeer(uint pid)
    {
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        Check(OpenProcessToken(process, 0x0008, out var token));
        using (token)
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            Check(GetTokenInformation(token, 12, out uint session, sizeof(uint), out _));
            return new Peer(identity.User!.Value, session, pid);
        }
    }

    private static void Check(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint outputSize, uint inputSize, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out uint value, uint size, out uint returned);
}
