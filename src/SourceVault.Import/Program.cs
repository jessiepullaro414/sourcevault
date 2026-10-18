using System.ComponentModel;
using SourceVault.Client;
using SourceVault.Import;

// Imports a folder of source into a new SourceVault project. Run as an operator, from the installed copy:
//   "C:\Program Files\SourceVault\client\SourceVault.Import.exe" <folder> --project "<new project name>" [--exclude <folder name>]... [--dry-run]
// Prints paths and counts only, never file content. Your original folder is not changed.
string? folder = null, project = null;
bool dryRun = false;
var excludes = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--project" when i + 1 < args.Length: project = args[++i]; break;
        case "--exclude" when i + 1 < args.Length: excludes.Add(args[++i]); break;
        case "--dry-run": dryRun = true; break;
        default:
            if (folder is null && !args[i].StartsWith("--", StringComparison.Ordinal)) folder = args[i];
            else return Usage();
            break;
    }
}
if (folder is null || (!dryRun && string.IsNullOrWhiteSpace(project))) return Usage();

try
{
    var plan = ImportPlan.Scan(folder, excludes);
    Importer.PrintPlan(plan, Console.Out);
    if (dryRun) { Console.WriteLine("Dry run: nothing was imported."); return 0; }
    return Importer.Run(plan, project!.Trim(), VaultClient.ConnectInstalled, Console.Out);
}
catch (DirectoryNotFoundException) { Console.Error.WriteLine("Folder not found."); return 2; }
catch (ImportException e) { Console.Error.WriteLine(e.Message); return 1; }
catch (Win32Exception e) when (e.NativeErrorCode == 2) { Console.Error.WriteLine("The SourceVault service is not running."); return 3; }
catch (Win32Exception e) when (e.NativeErrorCode == 5) { Console.Error.WriteLine("Access denied: this sign-in is not in 'SourceVault Operators' (sign out and in after joining)."); return 3; }
catch (Win32Exception e) when (e.NativeErrorCode == 231) { Console.Error.WriteLine("All service connections are busy; try again."); return 3; }
catch (UnauthorizedAccessException) { Console.Error.WriteLine("Refused: the pipe is not owned by NT SERVICE\\SourceVault."); return 3; }
catch (Exception e) when (e is IOException or EndOfStreamException) { Console.Error.WriteLine("The service closed the connection. Run the installed SourceVault.Import.exe from Program Files."); return 3; }

static int Usage()
{
    Console.Error.WriteLine("usage: SourceVault.Import.exe <folder> --project \"<new project name>\" [--exclude <folder name>]... [--dry-run]");
    return 2;
}
