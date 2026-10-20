using System.Security.Cryptography;

namespace SourceVault.Build;

/// <summary>One file of a build's source: its vault path and exact length.</summary>
public sealed record BuildFile(string Name, long Length);

/// <summary>The source could not be read while copying (a record failed its check, or the vault side aborted).</summary>
public sealed class BuildSourceException(string reason) : Exception("Build source unavailable: " + reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// A build's source, read once, streamed (docs/vault-any-file-design.md section 6): the file table first, then the
/// files' bytes in table order. Memory on either side is a few chunks, never the project.
/// </summary>
public interface IBuildSource
{
    IReadOnlyList<BuildFile> Files { get; }

    /// <summary>
    /// The next bytes of the files, in table order. A chunk is at most 512 KiB and never spans two files; an empty
    /// file has no chunks; the end is an empty chunk. A chunk is valid only until the next call. Throws
    /// <see cref="BuildSourceException"/> when the source fails (the copy is abandoned and the build fails).
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> NextAsync(CancellationToken ct);
}

/// <summary>Files already in memory (tests, and small callers). Chunks are slices of the caller's arrays.</summary>
public sealed class InMemoryBuildSource(IReadOnlyList<(string Name, byte[] Content)> files) : IBuildSource
{
    public const int ChunkBytes = 512 * 1024;
    private int file, offset;
    public IReadOnlyList<BuildFile> Files { get; } = files.Select(f => new BuildFile(f.Name, f.Content.LongLength)).ToList();

    public ValueTask<ReadOnlyMemory<byte>> NextAsync(CancellationToken ct)
    {
        while (file < files.Count && offset >= files[file].Content.Length) { file++; offset = 0; }
        if (file >= files.Count) return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        var content = files[file].Content;
        int n = Math.Min(ChunkBytes, content.Length - offset);
        var chunk = new ReadOnlyMemory<byte>(content, offset, n);
        offset += n;
        return ValueTask.FromResult(chunk);
    }
}

/// <summary>
/// One file's bytes pulled from an <see cref="IBuildSource"/>, as the data stream of a tar entry. It reports the file's
/// exact length (so the tar header is written up front) and reads strictly forward.
/// </summary>
internal sealed class SourceFileStream(IBuildSource source, long length, CancellationToken ct) : Stream
{
    private ReadOnlyMemory<byte> pending;
    private long position;
    public override bool CanRead => true;
    public override bool CanSeek => true; // Length is known; only the current position can be "sought"
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => position;
        set { if (value != position) throw new NotSupportedException("Source files are read forward only."); }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (position == length || buffer.IsEmpty) return 0;
        if (pending.IsEmpty)
        {
            pending = await source.NextAsync(ct).ConfigureAwait(false);
            if (pending.IsEmpty || pending.Length > length - position) throw new BuildSourceException("SourceUnavailable"); // short or overlong
        }
        int n = Math.Min(buffer.Length, pending.Length);
        pending[..n].CopyTo(buffer);
        pending = pending[n..];
        position += n;
        return n;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => origin == SeekOrigin.Begin && offset == position ? position : throw new NotSupportedException();
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
