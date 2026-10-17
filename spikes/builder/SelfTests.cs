using System.Formats.Tar;
using System.Text;

internal static class SelfTests
{
    internal static void Run(string workspace)
    {
        if (!Path.IsPathFullyQualified(workspace)) throw new ArgumentException();
        // An explicit unique directory prevents clobbering prior developer files.
        string root = Path.Combine(workspace, "builder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0;
        void Check(string name, Action action)
        {
            action();
            Console.WriteLine("PASS " + name);
            passed++;
        }
        string Archive(string name, byte[] payload, TarEntryType type = TarEntryType.RegularFile)
        {
            string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tar");
            using var file = File.Create(path);
            Program.WriteArchive(file, name, payload, type);
            return path;
        }
        void Reject(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or DecoderFallbackException or OverflowException)
            { return; }
            throw new InvalidOperationException("Expected rejection");
        }
        byte[] valid = "ADD 19 23"u8.ToArray();
        string context = Archive("main.sv", valid);
        Check("valid-context", () => {
            if (!Program.ReadArchive(context, "main.sv").SequenceEqual(valid)) throw new InvalidOperationException();
        });
        string output = Path.Combine(root, "artifact.tar");
        Check("synthetic-compile-and-artifact-validate", () => {
            Program.Compile(context, output);
            if (Encoding.UTF8.GetString(Program.ReadArchive(output, "app.js")) != "console.log(42);\n")
                throw new InvalidOperationException();
        });
        Check("existing-output-preserved", () => {
            byte[] before = File.ReadAllBytes(output);
            Reject(() => Program.Compile(context, output));
            if (!before.SequenceEqual(File.ReadAllBytes(output))) throw new InvalidOperationException();
        });
        int pathCase = 0;
        foreach (string name in new[] { "../main.sv", "/main.sv", "C:/main.sv", "main.sv:secret", "MAIN.SV", "sub/main.sv", "main.sv\\x" })
            Check("reject-path-" + (++pathCase).ToString(), () => Reject(() => Program.ReadArchive(Archive(name, valid), "main.sv")));
        foreach (var type in new[] { TarEntryType.SymbolicLink, TarEntryType.HardLink, TarEntryType.Directory })
            Check("reject-" + type, () => Reject(() => Program.ReadArchive(Archive("main.sv", valid, type), "main.sv")));
        Check("reject-entry-over-limit", () => Reject(() => Program.ReadArchive(Archive("main.sv", new byte[Program.EntryLimit + 1]), "main.sv")));
        Check("reject-empty-entry", () => Reject(() => Program.ReadArchive(Archive("main.sv", []), "main.sv")));
        Check("reject-invalid-utf8", () => Reject(() => Program.ReadArchive(Archive("main.sv", [0xC0, 0xAF]), "main.sv")));
        Check("reject-archive-over-limit", () => {
            string path = Path.Combine(root, "large.tar");
            using (var file = File.Create(path)) file.SetLength(Program.ArchiveLimit + 512);
            Reject(() => Program.ReadArchive(path, "main.sv"));
        });
        Check("reject-truncated-archive", () => {
            byte[] bytes = File.ReadAllBytes(context);
            string path = Path.Combine(root, "truncated.tar");
            File.WriteAllBytes(path, bytes[..^1]);
            Reject(() => Program.ReadArchive(path, "main.sv"));
        });
        Check("reject-trailing-data", () => {
            byte[] bytes = File.ReadAllBytes(context);
            bytes[^1] = 42;
            string path = Path.Combine(root, "trailer.tar");
            File.WriteAllBytes(path, bytes);
            Reject(() => Program.ReadArchive(path, "main.sv"));
        });
        Check("reject-duplicate-entry", () => {
            string path = Path.Combine(root, "duplicate.tar");
            using (var file = File.Create(path))
            using (var writer = new TarWriter(file, TarEntryFormat.Ustar))
            {
                for (int i = 0; i < 2; i++)
                {
                    using var data = new MemoryStream(valid);
                    writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "main.sv") { DataStream = data });
                }
            }
            Reject(() => Program.ReadArchive(path, "main.sv"));
        });
        Check("reject-pax-format", () => {
            string path = Path.Combine(root, "pax.tar");
            using (var file = File.Create(path))
            using (var writer = new TarWriter(file, TarEntryFormat.Pax))
            using (var data = new MemoryStream(valid))
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "main.sv") { DataStream = data });
            Reject(() => Program.ReadArchive(path, "main.sv"));
        });
        Check("reject-bad-checksum", () => {
            byte[] bytes = File.ReadAllBytes(context);
            bytes[0] ^= 1;
            string path = Path.Combine(root, "checksum.tar");
            File.WriteAllBytes(path, bytes);
            Reject(() => Program.ReadArchive(path, "main.sv"));
        });
        Check("reject-compiler-injection", () => Reject(() => Program.Compile(Archive("main.sv", "ADD 1 2;evil()"u8.ToArray()), Path.Combine(root, "inject.tar"))));
        Check("reject-compiler-overflow", () => Reject(() => Program.Compile(Archive("main.sv", "ADD 2147483647 1"u8.ToArray()), Path.Combine(root, "overflow.tar"))));
        Check("reject-source-in-artifact", () => Reject(() => Program.ReadArchive(context, "app.js")));
        Console.WriteLine($"Builder self-tests passed: {passed}");
    }
}
