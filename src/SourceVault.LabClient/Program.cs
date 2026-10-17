using System.ComponentModel;
using SourceVault.LabClient;

// LAB ONLY client for the SourceVault service pipe. Must run from <install root>\client\ as the installed .exe:
// the service accepts only installed client executables. Prints no document content.
try
{
    return args switch
    {
        ["list"] => LabCommands.List(VaultClient.ConnectInstalled, Console.Out),
        ["check"] => LabCommands.Check(VaultClient.ConnectInstalled, Console.Out) == 0 ? 0 : 1,
        _ => Usage(),
    };
}
catch (Win32Exception e) when (e.NativeErrorCode == 2) { Console.Error.WriteLine("The SourceVault service is not running (no pipe)."); return 3; }
catch (Win32Exception e) when (e.NativeErrorCode == 5) { Console.Error.WriteLine("Access denied by the pipe: this sign-in is not in 'SourceVault Operators' (sign out and in after joining)."); return 3; }
catch (Win32Exception e) when (e.NativeErrorCode == 231) { Console.Error.WriteLine("All service connections are busy; try again."); return 3; }
catch (UnauthorizedAccessException) { Console.Error.WriteLine("Refused: the pipe is not owned by NT SERVICE\\SourceVault."); return 3; }
catch (Exception e) when (e is IOException or EndOfStreamException) { Console.Error.WriteLine("The service closed the connection (is this the installed client under Program Files?)."); return 3; }
catch (LabClientException e) { Console.Error.WriteLine("Service error: " + e.Message); return 3; }

static int Usage()
{
    Console.Error.WriteLine("usage: SourceVault.LabClient list | check");
    return 2;
}
