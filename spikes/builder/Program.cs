using System.Formats.Tar;
using System.Globalization;
using System.Text;

// Offline feasibility tooling only. Paths here belong to a trusted lab CLI,
// never an operator protocol request. No archive entry is extracted to disk.
internal static class Program
{
    internal const int ArchiveLimit = 1_048_576;
    internal const int EntryLimit = 262_144;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static int Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["self-test", var workspace]:
                    SelfTests.Run(workspace);
                    return 0;
                case ["validate-context", var input]:
                    _ = ReadArchive(input, "main.sv");
                    break;
                case ["validate-artifacts", var input]:
                    _ = ReadArchive(input, "app.js");
                    break;
                case ["synthetic-compile", var input, var output]:
                    Compile(input, output);
                    break;
                default:
                    Console.Error.WriteLine("Usage: self-test ABSOLUTE_WORKSPACE | validate-context TAR | validate-artifacts TAR | synthetic-compile INPUT_TAR NEW_OUTPUT_TAR");
                    return 2;
            }
            Console.WriteLine("OK");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or DecoderFallbackException or OverflowException or UnauthorizedAccessException)
        {
            // Never return exception messages: they can contain source or paths.
            Console.Error.WriteLine("Rejected");
            return 1;
        }
    }

    // Entire bounded archive is buffered before parsing. Uncompressed USTAR only:
    // no PAX extensions, compression, links, directories, duplicate entries, or
    // user-chosen names. This tiny exact-name profile is deliberately narrow.
    internal static byte[] ReadArchive(string path, string expectedName)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1536 or > ArchiveLimit || input.Length % 512 != 0)
            throw new InvalidDataException();
        byte[] archive = new byte[(int)input.Length];
        input.ReadExactly(archive);
        if (input.ReadByte() != -1) throw new InvalidDataException();
        // Validate physical structure first; TarReader may otherwise consume
        // extension headers or stop at the first zero header and ignore trailers.
        int offset = 0;
        int count = 0;
        while (offset < archive.Length && !IsZero(archive.AsSpan(offset, 512)))
        {
            var header = archive.AsSpan(offset, 512);
            if (++count > 1 || header[156] != (byte)'0' ||
                !header.Slice(257, 6).SequenceEqual("ustar\0"u8) ||
                !header.Slice(263, 2).SequenceEqual("00"u8) ||
                !IsZero(header.Slice(345, 155))) throw new InvalidDataException();
            int size = ParseOctal(header.Slice(124, 12));
            if (size is < 1 or > EntryLimit) throw new InvalidDataException();
            int dataEnd = checked(offset + 512 + size);
            int next = checked(offset + 512 + ((size + 511) / 512) * 512);
            if (next > archive.Length || !IsZero(archive.AsSpan(dataEnd, next - dataEnd)))
                throw new InvalidDataException();
            offset = next;
        }
        if (count != 1 || archive.Length - offset < 1024 || !IsZero(archive.AsSpan(offset)))
            throw new InvalidDataException();
        using var memory = new MemoryStream(archive, writable: false);
        using var reader = new TarReader(memory);
        TarEntry entry = reader.GetNextEntry() ?? throw new InvalidDataException();
        if (entry.Format != TarEntryFormat.Ustar || entry.EntryType != TarEntryType.RegularFile ||
            !string.Equals(entry.Name, expectedName, StringComparison.Ordinal) ||
            entry.LinkName.Length != 0 || entry.Length is < 1 or > EntryLimit || entry.DataStream is null)
            throw new InvalidDataException();
        byte[] payload = new byte[(int)entry.Length];
        entry.DataStream.ReadExactly(payload);
        if (reader.GetNextEntry() is not null) throw new InvalidDataException();
        _ = StrictUtf8.GetString(payload);
        return payload;
    }

    private static bool IsZero(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) if (b != 0) return false;
        return true;
    }

    private static int ParseOctal(ReadOnlySpan<byte> data)
    {
        int value = 0;
        bool ended = false, digit = false;
        foreach (byte b in data)
        {
            if (b is 0 or 32) { ended = digit; continue; }
            if (ended || b < '0' || b > '7') throw new InvalidDataException();
            digit = true;
            value = checked(value * 8 + b - '0');
        }
        return digit ? value : throw new InvalidDataException();
    }

    internal static void Compile(string input, string output)
    {
        // Toy compiler for synthetic data: exact grammar "ADD integer integer".
        // This tests a bounded context -> artifact path, not TypeScript support.
        string text = StrictUtf8.GetString(ReadArchive(input, "main.sv"));
        string[] parts = text.Split(' ');
        if (parts.Length != 3 || parts[0] != "ADD" ||
            !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int left) ||
            !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int right))
            throw new InvalidDataException();
        int result = checked(left + right);
        byte[] artifact = StrictUtf8.GetBytes("console.log(" + result.ToString(CultureInfo.InvariantCulture) + ");\n");
        // Exclusive creation: do not overwrite an existing path or follow an
        // existing file symlink. The containing directory must be trusted.
        using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        WriteArchive(target, "app.js", artifact);
    }

    internal static void WriteArchive(Stream output, string name, byte[] bytes, TarEntryType type = TarEntryType.RegularFile)
    {
        using var writer = new TarWriter(output, TarEntryFormat.Ustar, leaveOpen: true);
        var entry = new UstarTarEntry(type, name);
        if (type == TarEntryType.RegularFile) entry.DataStream = new MemoryStream(bytes, writable: false);
        if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = "outside";
        writer.WriteEntry(entry);
        entry.DataStream?.Dispose();
    }
}
