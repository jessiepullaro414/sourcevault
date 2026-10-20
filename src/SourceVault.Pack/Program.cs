using SourceVault.Packaging;
using SourceVault.Pack;

// SourceVault Pack (command line): on the computer where the code lives, encrypts a project folder for one vault PC.
//   SourceVault.Pack.exe init [--out <sender file>]
//   SourceVault.Pack.exe <folder> --to <recipient file> --out <file.svpkg> [--name "<suggested project name>"] [--exclude <folder name>]... [--dry-run] [--yes]
// "SourceVault Pack.exe" is the same, in a window. Prints paths, counts and fingerprints only, never file content.
// The folder is only read; nothing is deleted.
try
{
    if (args.Length >= 1 && args[0] == "init") return Commands.Init(args[1..], Console.Out);
    return Commands.Pack(args, Console.Out, Console.ReadLine);
}
catch (SenderKeyException e) { Console.Error.WriteLine(e.Message); return 3; }
catch (KeyFileException e) { Console.Error.WriteLine("The recipient file is not usable: " + e.Message); return 2; }
catch (PackException e) { Console.Error.WriteLine(e.Message); return 1; }
catch (DirectoryNotFoundException) { Console.Error.WriteLine("Folder not found."); return 2; }

namespace SourceVault.Pack
{
    internal static class Commands
    {
        internal const string Usage =
            "usage: SourceVault.Pack.exe init [--out <sender file>]\n" +
            "       SourceVault.Pack.exe <folder> --to <recipient file> --out <file.svpkg> [--name \"<project name>\"] [--exclude <folder name>]... [--dry-run] [--yes]";

        /// <summary>Creates this computer's TPM sender key (once) and writes its public sender file for the vault PC's administrator.</summary>
        internal static int Init(string[] args, TextWriter output)
        {
            string? outPath = args switch { [] => null, ["--out", var p] => p, _ => throw new PackException(Usage) };
            var (key, created) = SenderKey.OpenOrCreate();
            using (key)
            {
                var sender = PackCore.SenderFile(key);
                var path = Path.GetFullPath(outPath ?? $"SourceVault-sender-{sender.Computer}.json");
                PackCore.WriteNew(path, sender.ToJson());
                output.WriteLine(created ? "Created this computer's signing key in its TPM (it cannot be exported)." : "This computer already has its signing key; wrote its sender file again.");
                output.WriteLine($"Sender file: {path}");
                output.WriteLine($"Fingerprint: {sender.FingerprintText}");
                output.WriteLine("It holds no secrets. Give it to the vault PC's administrator, who registers it with: Setup-SourceVault.ps1 -RegisterSender <file>");
                return 0;
            }
        }

        internal static int Pack(string[] args, TextWriter output, Func<string?> readLine, Func<IPackageSigner>? openSigner = null)
        {
            string? folder = null, to = null, outPath = null, name = null;
            bool dryRun = false, yes = false;
            var excludes = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--to" when i + 1 < args.Length: to = args[++i]; break;
                    case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                    case "--name" when i + 1 < args.Length: name = args[++i]; break;
                    case "--exclude" when i + 1 < args.Length: excludes.Add(args[++i]); break;
                    case "--dry-run": dryRun = true; break;
                    case "--yes": yes = true; break;
                    default:
                        if (folder is null && !args[i].StartsWith("--", StringComparison.Ordinal)) folder = args[i];
                        else throw new PackException(Usage);
                        break;
                }
            }
            if (folder is null || to is null || (!dryRun && outPath is null)) throw new PackException(Usage);

            var recipient = PackCore.ReadRecipient(to);
            var plan = PackCore.Scan(folder, excludes);
            output.WriteLine($"Folder: {plan.Root}");
            output.WriteLine($"{plan.Files.Count} file(s) to pack ({plan.TotalBytes:N0} bytes); {plan.Skipped.Count} skipped:");
            foreach (var skipped in plan.Skipped) output.WriteLine($"  skipped {skipped.Path}  ({skipped.Reason})");
            if (PackCore.Problem(plan) is { } problem) throw new PackException(problem);
            output.WriteLine($"Encrypting for the vault PC \"{recipient.Computer}\", recipient fingerprint:");
            output.WriteLine($"  {recipient.FingerprintText}");
            output.WriteLine("Check that this matches the fingerprint the vault PC showed when its recipient file was exported.");
            if (dryRun) { output.WriteLine("Dry run: no package was written."); return 0; }

            var target = Path.GetFullPath(outPath!);
            if (File.Exists(target)) throw new PackException($"{target} already exists; choose a new file name.");
            using var key = (openSigner ?? SenderKey.Open)();
            var sender = PackCore.SenderFile(key);
            output.WriteLine($"Signing as \"{sender.Computer}\", sender fingerprint {sender.FingerprintText}");
            if (!yes)
            {
                output.Write("Type YES to write the encrypted package: ");
                if (readLine()?.Trim() != "YES") { output.WriteLine("Nothing was written."); return 1; }
            }
            var (id, bytes) = PackCore.Write(plan, recipient, key, target, name);
            output.WriteLine($"Wrote {target} ({bytes:N0} bytes), package id {id:D}.");
            output.WriteLine("It is encrypted for that vault PC only. Carry it over any way you like, then on the vault PC run:");
            output.WriteLine($"  SourceVault.Import.exe --package \"{Path.GetFileName(target)}\" --project \"<new project name>\"");
            output.WriteLine("Your folder was not changed.");
            return 0;
        }
    }
}
