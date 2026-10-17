namespace SourceVault.Storage;

public interface IKeyProtector
{
    byte[] WrapKey(ReadOnlySpan<byte> key);
    byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey);
}

public enum RecordKind : byte { Document = 1, Manifest = 2 }
public readonly record struct RecordContext(Guid ProjectId, Guid FileId, Guid RevisionId, RecordKind Kind);
