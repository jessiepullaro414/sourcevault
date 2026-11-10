using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ComStream = System.Runtime.InteropServices.ComTypes.IStream;
using ComStatStg = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace SourceVault.Service;

/// <summary>
/// Writes a finalized disc with Windows' own IMAPI2 (docs/disc-export-design.md, section 2), through COM late binding
/// from the base class library: no third-party package. The image is ISO 9660 + Joliet + UDF; each file is an IStream
/// that decrypts from the vault snapshot as IMAPI reads it, so plaintext is not staged as files on this PC's disk.
/// Writing closes the disc (ForceMediaToBeClosed), and only write-once media are accepted.
/// LAB-ONLY BEHAVIOUR: IMAPI2 from the service's virtual account, its spooling and its read-back timing are unverified
/// until the lab run; development checks never touch a disc drive.
/// </summary>
internal sealed class ImapiDiscWriter : IDiscWriter
{
    // IMAPI_MEDIA_PHYSICAL_TYPE: write-once types SourceVault accepts, and rewritable ones it names when refusing them.
    private static readonly Dictionary<int, string> WriteOnce = new() { [2] = "CD-R", [6] = "DVD+R", [8] = "DVD+R DL", [9] = "DVD-R", [11] = "DVD-R DL", [18] = "BD-R" };
    private static readonly Dictionary<int, string> Rewritable = new() { [3] = "CD-RW", [5] = "DVD-RAM", [7] = "DVD+RW", [10] = "DVD-RW", [13] = "DVD+RW DL", [19] = "BD-RE" };
    private const int FsIso9660 = 1, FsJoliet = 2, FsUdf = 4;

    public IReadOnlyList<DiscDrive> Drives() => OnSta(() =>
    {
        dynamic master = Create("IMAPI2.MsftDiscMaster2");
        if (!(bool)master.IsSupportedEnvironment) throw new DiscWriteException("DiscUnavailable");
        var drives = new List<DiscDrive>();
        int count = master.Count;
        for (int i = 0; i < count; i++)
        {
            string id = master[i];
            dynamic recorder = Create("IMAPI2.MsftDiscRecorder2");
            recorder.InitializeDiscRecorder(id);
            dynamic format = Create("IMAPI2.MsftDiscFormat2Data");
            if (!(bool)format.IsRecorderSupported(recorder)) continue; // reads only
            drives.Add(new DiscDrive(id, Name(recorder), Media(format, recorder)));
        }
        return (IReadOnlyList<DiscDrive>)drives;
    });

    public void WriteAndClose(string driveId, string volumeLabel, IReadOnlyList<DiscImageFile> files, Action<string, int> progress, CancellationToken ct) => OnSta(() =>
    {
        dynamic recorder = Recorder(driveId);
        dynamic format = Create("IMAPI2.MsftDiscFormat2Data");
        var media = Media(format, recorder) ?? throw new DiscWriteException("NoMedia");
        if (!media.WriteOnce) throw new DiscWriteException("MediaNotWriteOnce");
        if (!media.Blank) throw new DiscWriteException("MediaNotBlank");
        format.Recorder = recorder;
        format.ClientName = "SourceVault";
        format.ForceMediaToBeClosed = true; // finalize: no further sessions

        dynamic image = Create("IMAPI2FS.MsftFileSystemImage");
        image.ChooseImageDefaults(recorder);
        image.FileSystemsToCreate = FsIso9660 | FsJoliet | FsUdf;
        image.VolumeName = volumeLabel;
        image.FreeMediaBlocks = format.FreeSectorsOnMedia; // adding more than fits fails here, before anything is written
        dynamic root = image.Root;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in files)
            {
                var path = file.Path.Replace('/', '\\');
                for (int at = path.IndexOf('\\'); at > 0; at = path.IndexOf('\\', at + 1))
                    if (folders.Add(path[..at])) root.AddDirectory(path[..at]);
                root.AddFile(path, new SegmentStream(file));
            }
        }
        catch (COMException) { throw new DiscWriteException("MediaTooSmall"); }
        dynamic result = image.CreateResultImage();
        progress("writing", 0);
        try { format.Write(result.ImageStream); }
        catch (COMException) { throw new DiscWriteException("BurnFailed"); }
        progress("closing", 100);
        return true;
    });

    public string? MountedRoot(string driveId) => OnSta(() =>
    {
        dynamic recorder = Recorder(driveId);
        foreach (string volume in (object[])recorder.VolumePathNames)
            if (File.Exists(Path.Combine(volume, SourceVault.Packaging.DiscRecords.RecordFile))) return volume;
        return (string?)null;
    });

    private static dynamic Recorder(string driveId)
    {
        dynamic recorder = Create("IMAPI2.MsftDiscRecorder2");
        try { recorder.InitializeDiscRecorder(driveId); }
        catch (COMException) { throw new DiscWriteException("NoMedia"); }
        return recorder;
    }

    private static DiscMedia? Media(dynamic format, dynamic recorder)
    {
        try
        {
            if (!(bool)format.IsCurrentMediaSupported(recorder)) return null;
            format.Recorder = recorder;
            int type = (int)format.CurrentPhysicalMediaType;
            string name = WriteOnce.TryGetValue(type, out var w) ? w : Rewritable.TryGetValue(type, out var r) ? r : $"type {type}";
            return new DiscMedia(name, WriteOnce.ContainsKey(type), (bool)format.MediaHeuristicallyBlank, (long)(int)format.FreeSectorsOnMedia * 2048);
        }
        catch (COMException) { return null; } // no disc, or not readable
    }

    private static string Name(dynamic recorder)
    {
        string vendor = ((string)recorder.VendorId ?? "").Trim(), product = ((string)recorder.ProductId ?? "").Trim();
        var volumes = string.Join(", ", (object[])recorder.VolumePathNames);
        return $"{vendor} {product}".Trim() + (volumes.Length > 0 ? $" ({volumes})" : "");
    }

    private static dynamic Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId) ?? throw new DiscWriteException("DiscUnavailable");
        return Activator.CreateInstance(type) ?? throw new DiscWriteException("DiscUnavailable");
    }

    // IMAPI2's objects are apartment-threaded: every call runs on one short-lived STA thread.
    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e is COMException ? new DiscWriteException("DiscUnavailable") : e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    /// <summary>
    /// A file as a COM IStream: plaintext segments from the vault snapshot, read forward. IMAPI may ask to start again
    /// (seek to 0) or skip ahead; anything else is refused. Each segment buffer belongs to the reader, which zeroes it.
    /// </summary>
    [ComVisible(true)]
    private sealed class SegmentStream(DiscImageFile file) : ComStream
    {
        private IEnumerator<ArraySegment<byte>>? segments;
        private ArraySegment<byte> pending;
        private long position;

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int copied = 0;
            segments ??= file.Read().GetEnumerator();
            while (copied < cb)
            {
                if (pending.Count == 0)
                {
                    if (!segments.MoveNext()) break;
                    pending = segments.Current;
                }
                int n = Math.Min(cb - copied, pending.Count);
                pending.AsSpan(0, n).CopyTo(pv.AsSpan(copied, n));
                pending = pending[n..];
                copied += n;
            }
            position += copied;
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, copied);
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            long target = dwOrigin switch { 0 => dlibMove, 1 => position + dlibMove, 2 => file.Length + dlibMove, _ => -1 };
            if (target < 0 || target > file.Length) throw new COMException("Seek out of range.", unchecked((int)0x80030019)); // STG_E_INVALIDFUNCTION
            if (target < position) { segments?.Dispose(); segments = null; pending = default; position = 0; }
            var skip = new byte[Math.Min(1 << 16, Math.Max(1, target - position))];
            while (position < target) Read(skip, (int)Math.Min(skip.Length, target - position), IntPtr.Zero);
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, position);
        }

        public void Stat(out ComStatStg pstatstg, int grfStatFlag) => pstatstg = new ComStatStg { type = 2, cbSize = file.Length }; // STGTY_STREAM

        public void CopyTo(ComStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
        {
            var buffer = new byte[1 << 16];
            long total = 0;
            var count = Marshal.AllocHGlobal(4);
            try
            {
                while (total < cb)
                {
                    Read(buffer, (int)Math.Min(buffer.Length, cb - total), count);
                    int n = Marshal.ReadInt32(count);
                    if (n == 0) break;
                    pstm.Write(buffer, n, IntPtr.Zero);
                    total += n;
                }
            }
            finally { Marshal.FreeHGlobal(count); Array.Clear(buffer); }
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, total);
            if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, total);
        }

        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();
        public void SetSize(long libNewSize) => throw new NotSupportedException();
        public void Commit(int grfCommitFlags) { }
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
        public void Clone(out ComStream ppstm) => throw new NotSupportedException();
    }
}
