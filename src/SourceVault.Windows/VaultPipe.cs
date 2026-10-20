using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SourceVault.Windows;

/// <summary>
/// A connected client as reported by the OS (never by the message): token user, logon session, PID, the process's
/// creation time (UTC FILETIME, which tells a reused PID apart) and image.
/// </summary>
public sealed record PipePeer(SecurityIdentifier User, uint Session, uint ProcessId, long ProcessCreated, string ImagePath);

/// <summary>
/// Who may use the vault pipe: interactive members of the operators group, running an installed client executable.
/// The executable check is defense in depth: it stops other programs run by the operator, not code injected into
/// an allowed client. Startup hooks are off in every client (StartupHookSupport=false); a debugger or a .NET profiler
/// set through environment variables can still inject code, and that stays outside the claim.
/// </summary>
public sealed class PipePeerPolicy(SecurityIdentifier operatorsGroup, IEnumerable<string> allowedImagePaths, ClientPins? pins = null)
{
    private static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);
    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);
    private static readonly SecurityIdentifier Service = new(WellKnownSidType.ServiceSid, null);
    private readonly HashSet<string> images = allowedImagePaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public SecurityIdentifier OperatorsGroup { get; } = operatorsGroup;

    /// <summary>Checks the impersonated client token. Throws <see cref="UnauthorizedAccessException"/> on any mismatch.</summary>
    internal void ValidateToken(WindowsIdentity client)
    {
        var principal = new WindowsPrincipal(client);
        if (!principal.IsInRole(OperatorsGroup) || !principal.IsInRole(Interactive) || principal.IsInRole(Network) || principal.IsInRole(Service))
            throw new UnauthorizedAccessException("Peer policy rejected.");
    }

    internal void ValidateImage(string imagePath)
    {
        if (!images.Contains(imagePath)) throw new UnauthorizedAccessException("Peer policy rejected.");
    }

    /// <summary>
    /// Once per connection, after <see cref="VaultPipe.ValidateClient"/>: the client's installed files must match
    /// their pins (<see cref="ClientPins"/>). Without pins (in-process tests only) this checks nothing.
    /// </summary>
    public void ValidateInstalledFiles(string imagePath) => pins?.Verify(imagePath);
}

/// <summary>Production named-pipe plumbing for the vault service and its clients. BCL P/Invoke only.</summary>
public static class VaultPipe
{
    public const string ProductionName = "SourceVault.v1";
    public const int MaxInstances = 4;
    // FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | READ_CONTROL | SYNCHRONIZE. Excludes
    // FILE_CREATE_PIPE_INSTANCE (0x4). READ_CONTROL lets the client read the pipe owner to identify the server.
    internal const uint ClientRights = 0x00120083;
    private const uint ProcessQueryLimited = 0x1000;

    /// <summary>
    /// Creates one server instance. Owner is set to the server account, which is how clients identify the server:
    /// users cannot open service processes, and only the account itself (or a trusted administrator) can create an
    /// object it owns. Protected DACL: the server account may create further instances; SYSTEM and the operators
    /// group get exactly <see cref="ClientRights"/>. Remote clients are rejected by the OS. The first instance uses
    /// first-instance protection so a squatter that created the name first makes startup fail.
    /// </summary>
    public static NamedPipeServerStream CreateServerInstance(string name, SecurityIdentifier serverAccount, SecurityIdentifier operatorsGroup, bool first) =>
        CreateInstance(name, $"O:{serverAccount.Value}D:P(A;;FA;;;{serverAccount.Value})(A;;0x{ClientRights:X};;;SY)(A;;0x{ClientRights:X};;;{operatorsGroup.Value})", first);

    /// <summary>
    /// A service-to-service pipe (the build service's): owned by <paramref name="serverAccount"/>, which alone may create
    /// instances; <paramref name="clientAccount"/> alone may connect, with exactly <see cref="ClientRights"/>. Remote
    /// clients are rejected; the first instance uses first-instance protection.
    /// </summary>
    public static NamedPipeServerStream CreatePrivateServerInstance(string name, SecurityIdentifier serverAccount, SecurityIdentifier clientAccount, bool first) =>
        CreateInstance(name, $"O:{serverAccount.Value}D:P(A;;FA;;;{serverAccount.Value})(A;;0x{ClientRights:X};;;{clientAccount.Value})", first);

    /// <summary>For a private pipe: the OS client token's user must be <paramref name="expectedClient"/>. Throws otherwise.</summary>
    public static void ValidatePrivateClient(NamedPipeServerStream pipe, SecurityIdentifier expectedClient)
    {
        SecurityIdentifier? user = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(true) ?? throw new UnauthorizedAccessException();
            user = identity.User;
        });
        if (user is null || !user.Equals(expectedClient)) throw new UnauthorizedAccessException("Peer rejected.");
    }

    private static NamedPipeServerStream CreateInstance(string name, string sddl, bool first)
    {
        ValidateName(name);
        Check(ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _));
        try
        {
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            // PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED (| FILE_FLAG_FIRST_PIPE_INSTANCE); PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS
            uint openMode = 0x00000003 | 0x40000000 | (first ? 0x00080000u : 0u);
            var handle = CreateNamedPipe($@"\\.\pipe\{name}", openMode, 0x00000008, MaxInstances, 65536, 65536, 0, ref sa);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }

    /// <summary>
    /// Call after reading each frame: checks the OS client token (identification-level impersonation, never message
    /// claims), then the client's executable as reported by Windows. The service can open the client process only
    /// because the client granted it limited query access (<see cref="AllowServiceToIdentifyThisProcess"/>); a
    /// client that did not is rejected. PID reuse: the process must still be running and must have been created no
    /// later than <paramref name="connectedUtc"/>, when the server accepted the connection; the open handle keeps the
    /// PID from being reused while it is checked, so a PID that now names a newer process is refused. Residual risk,
    /// documented: a same-user process that hands its pipe handle to
    /// an allowed client that was already running could pass as that client; same-user code is outside the claim anyway.
    /// </summary>
    public static PipePeer ValidateClient(NamedPipeServerStream pipe, PipePeerPolicy policy, DateTime connectedUtc)
    {
        Check(GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid));
        SecurityIdentifier? user = null; uint session = 0;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(true) ?? throw new UnauthorizedAccessException();
            policy.ValidateToken(identity);
            Check(GetTokenInformation(identity.AccessToken, 12, out session, sizeof(uint), out _)); // TokenSessionId
            user = identity.User;
        });
        if (user is null) throw new UnauthorizedAccessException();
        using var process = OpenProcess(ProcessQueryLimited, false, pid);
        if (process.IsInvalid) throw new UnauthorizedAccessException("Client process not identifiable.");
        long created = CheckProcessLifetime(process, connectedUtc);
        var image = ImagePath(process);
        policy.ValidateImage(image);
        return new PipePeer(user, session, pid, created, image);
    }

    /// <summary>The process's creation time; throws unless it is still running and was created by <paramref name="connectedUtc"/>.</summary>
    internal static long CheckProcessLifetime(SafeProcessHandle process, DateTime connectedUtc)
    {
        const uint StillActive = 259;
        if (!GetExitCodeProcess(process, out uint exitCode) || exitCode != StillActive) throw new UnauthorizedAccessException("Client process exited.");
        if (!GetProcessTimes(process, out long created, out _, out _, out _)) throw new UnauthorizedAccessException("Client process not identifiable.");
        if (created > connectedUtc.ToFileTimeUtc()) throw new UnauthorizedAccessException("Client process is newer than the connection.");
        return created;
    }

    /// <summary>
    /// For installed clients, at startup: adds an ACE granting <paramref name="service"/> only
    /// PROCESS_QUERY_LIMITED_INFORMATION on this process, so the service can read this process's executable path.
    /// User processes otherwise grant service accounts nothing.
    /// </summary>
    public static void AllowServiceToIdentifyThisProcess(SecurityIdentifier service)
    {
        var self = GetCurrentProcess();
        uint status = GetSecurityInfo(self, 6, 4, IntPtr.Zero, IntPtr.Zero, out var dacl, IntPtr.Zero, out var descriptor); // SE_KERNEL_OBJECT, DACL
        if (status != 0) throw new Win32Exception((int)status);
        try
        {
            Check(ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1, 4, out var text, IntPtr.Zero));
            string current;
            try { current = Marshal.PtrToStringUni(text)!; } finally { LocalFree(text); }
            string extended = current + $"(A;;0x{ProcessQueryLimited:X};;;{service.Value})";
            Check(ConvertStringSecurityDescriptorToSecurityDescriptor(extended, 1, out var updated, out _));
            try
            {
                Check(GetSecurityDescriptorDacl(updated, out _, out var newDacl, out _));
                status = SetSecurityInfo(self, 6, 4, IntPtr.Zero, IntPtr.Zero, newDacl, IntPtr.Zero);
                if (status != 0) throw new Win32Exception((int)status);
            }
            finally { LocalFree(updated); }
        }
        finally { LocalFree(descriptor); }
    }

    /// <summary>
    /// Client side: opens the pipe with exact rights and identification-level impersonation only, then requires the
    /// pipe to be owned by <paramref name="expectedServer"/> before any data is sent. Waits while all instances are busy.
    /// </summary>
    public static NamedPipeClientStream Connect(string name, SecurityIdentifier expectedServer, TimeSpan busyTimeout)
    {
        ValidateName(name);
        var path = $@"\\.\pipe\{name}";
        var deadline = DateTime.UtcNow + busyTimeout;
        while (true)
        {
            // FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION
            var handle = CreateFile(path, ClientRights, 0, IntPtr.Zero, 3, 0x40000000 | 0x00100000 | 0x00010000, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                var pipe = new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
                try
                {
                    if (!PipeOwner(handle).Equals(expectedServer)) throw new UnauthorizedAccessException("Server identity rejected.");
                    return pipe;
                }
                catch { pipe.Dispose(); throw; }
            }
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            var remaining = deadline - DateTime.UtcNow;
            if (error != 231 || remaining <= TimeSpan.Zero) throw new Win32Exception(error); // ERROR_PIPE_BUSY
            WaitNamedPipe(path, (uint)Math.Clamp(remaining.TotalMilliseconds, 1, 5000));
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException("Invalid pipe name.");
    }

    private static SecurityIdentifier PipeOwner(SafePipeHandle pipe)
    {
        uint status = GetSecurityInfo(pipe, 6, 1, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor); // OWNER
        if (status != 0) throw new UnauthorizedAccessException("Server identity rejected.");
        try { return new SecurityIdentifier(owner); }
        finally { LocalFree(descriptor); }
    }

    private static string ImagePath(SafeProcessHandle process)
    {
        var buffer = new StringBuilder(1024);
        uint size = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(process, 0, buffer, ref size)) throw new UnauthorizedAccessException();
        return buffer.ToString(0, (int)size);
    }

    private static void Check(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint outputSize, uint inputSize, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WaitNamedPipe(string name, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out uint value, uint size, out uint returned);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafePipeHandle handle, int type, uint info, out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(IntPtr handle, int type, uint info, IntPtr owner, IntPtr group, out IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(IntPtr handle, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, out bool present, out IntPtr dacl, out bool defaulted);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(IntPtr descriptor, uint revision, uint info, out IntPtr text, IntPtr length);
}
