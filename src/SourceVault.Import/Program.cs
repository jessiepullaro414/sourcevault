using System.ComponentModel;
using SourceVault.Client;
using SourceVault.Import;

// Imports a folder of source, or an encrypted package from another computer, into a new SourceVault project. Run as
// an operator, from the installed copy:
//   "C:\Program Files\SourceVault\client\SourceVault.Import.exe" <folder> --project "<new project name>" [--exclude <folder name>]... [--dry-run] [--delete-source]
//   "C:\Program Files\SourceVault\client\SourceVault.Import.exe" --package <file.svpkg> --project "<new project name>"
//   "C:\Program Files\SourceVault\client\SourceVault.Import.exe" --export-recipient <file.json>
// Prints paths and counts only, never file content. Your original folder is not changed unless --delete-source is
// given, and then only after every file verified and you type DELETE.
string? folder = null, project = null, package = null, recipientOut = null;
bool dryRun = false, deleteSource = false;
var excludes = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--project" when i + 1 < args.Length: project = args[++i]; break;
        case "--exclude" when i + 1 < args.Length: excludes.Add(args[++i]); break;
        case "--dry-run": dryRun = true; break;
        case "--delete-source": deleteSource = true; break;
        case "--package" when i + 1 < args.Length: package = args[++i]; break;
        case "--export-recipient" when i + 1 < args.Length: recipientOut = args[++i]; break;
        default:
            if (folder is null && !args[i].StartsWith("--", StringComparison.Ordinal)) folder = args[i];
            else return Usage();
            break;
    }
}
bool folderMode = package is null && recipientOut is null;
if (folderMode && (folder is null || (!dryRun && string.IsNullOrWhiteSpace(project)))) return Usage();
if (package is not null && (folder is not null || recipientOut is not null || dryRun || deleteSource || excludes.Count > 0 || string.IsNullOrWhiteSpace(project))) return Usage();
if (recipientOut is not null && (folder is not null || package is not null || project is not null || dryRun || deleteSource || excludes.Count > 0)) return Usage();

try
{
    if (recipientOut is not null) return PackageImport.ExportRecipient(recipientOut, VaultClient.ConnectInstalled, Console.Out);
    if (package is not null) return PackageImport.Run(package, project!.Trim(), VaultClient.ConnectInstalled, Console.Out);
    var plan = ImportPlan.Scan(folder!, excludes);
    Importer.PrintPlan(plan, Console.Out);
    if (dryRun)
    {
        Console.WriteLine("Dry run: nothing was imported.");
        if (deleteSource) Console.WriteLine($"With --delete-source, up to {plan.Files.Count} imported file(s) would be deleted after verification (you will be asked to type DELETE).");
        return 0;
    }
    if (deleteSource && Console.IsInputRedirected) { Console.Error.WriteLine("--delete-source needs an interactive console to type the confirmation."); return 2; }
    int result = Importer.Run(plan, project!.Trim(), VaultClient.ConnectInstalled, Console.Out);
    if (result == 0 && deleteSource) SourceCleanup.Run(plan, project!.Trim(), Console.ReadLine, Console.Out);
    else if (deleteSource) Console.WriteLine("--delete-source: nothing was deleted because the import did not fully verify.");
    return result;
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
    Console.Error.WriteLine("usage: SourceVault.Import.exe <folder> --project \"<new project name>\" [--exclude <folder name>]... [--dry-run] [--delete-source]");
    Console.Error.WriteLine("       SourceVault.Import.exe --package <file.svpkg> --project \"<new project name>\"");
    Console.Error.WriteLine("       SourceVault.Import.exe --export-recipient <file.json>");
    return 2;
}
