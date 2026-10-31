using System.Diagnostics;
using PS3HddTool.Core.Crypto;
using PS3HddTool.Core.Disk;
using PS3HddTool.Core.FileSystem;

/// <summary>
/// Performance benchmark on a realistic stack: the plain test image is encrypted the way a
/// PS3 Slim drive is (XTS-128 + 16-bit byte swap), then every workload runs through
/// DecryptedDiskSource over a counting layer, so we report both wall time and the number
/// of physical reads, writes and flushes (the counts are deterministic; CI timings are not).
///
/// Usage: PS3HddTool.WriterHarness --bench &lt;plain-image&gt; &lt;work-dir&gt;
/// </summary>
internal static class Bench
{
    private static readonly byte[] DataKey = Enumerable.Range(0x10, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] TweakKey = Enumerable.Range(0x40, 16).Select(i => (byte)i).ToArray();

    public static int Run(string plainImage, string workDir)
    {
        Directory.CreateDirectory(workDir);
        string enc = Path.Combine(workDir, "bench-encrypted.img");
        var sw = Stopwatch.StartNew();
        EncryptImage(plainImage, enc);
        Report("setup: encrypt test image", sw.Elapsed, null);

        var rows = new List<string>();
        using var raw = new ImageDiskSource(enc, writable: true);
        var counter = new CountingDiskSource(raw);
        using var disk = new DecryptedDiskSource(new NonDisposingDiskSource(counter), DataKey, TweakKey, applyBswap16: true);

        Ufs2FileSystem Mount()
        {
            var fs = new Ufs2FileSystem(disk, 0);
            if (!fs.Mount()) throw new InvalidDataException("encrypted bench image does not mount");
            return fs;
        }

        void Measure(string name, Action<Ufs2FileSystem> work, Ufs2FileSystem? fs = null)
        {
            fs ??= Mount();
            counter.Reset();
            var t = Stopwatch.StartNew();
            work(fs);
            t.Stop();
            Report(name, t.Elapsed, counter);
        }

        Ufs2Writer Writer(Ufs2FileSystem fs) => new(fs, disk, 0, dryRun: false, log: _ => { });

        var rng = new Random(1234);
        byte[] big = new byte[64 * 1024 * 1024];
        rng.NextBytes(big);

        Measure("mkdir /bench (1 op + superblock update)", fs =>
        {
            var w = Writer(fs);
            w.CreateDirectory(2, "bench");
            w.UpdateSuperblock();
        });

        Measure("write 300 small files, one batch", fs =>
        {
            var w = Writer(fs);
            long parent = fs.ResolvePath("/bench")!.InodeNumber;
            for (int i = 0; i < 300; i++)
            {
                byte[] data = new byte[1 + rng.Next(20_000)];
                rng.NextBytes(data);
                w.WriteFile(parent, $"s{i:D3}.bin", new MemoryStream(data), data.Length);
            }
            w.UpdateSuperblock();
        });

        Measure("write one 64 MiB file", fs =>
        {
            var w = Writer(fs);
            w.WriteFile(2, "big.bin", new MemoryStream(big), big.Length);
            w.UpdateSuperblock();
        });

        Measure("browse: list every folder + stat every entry (fresh mount)", fs => Walk(fs));
        var warm = Mount();
        Walk(warm);
        Measure("browse again on the same mount", fs => Walk(fs), warm);

        Measure("extract the 64 MiB file", fs =>
        {
            var inode = fs.ResolvePath("/big.bin")!;
            using var sink = new HashingSink();
            fs.ExtractInodeToStream(inode, sink);
        });

        Measure("rename one file (1 op + superblock update)", fs =>
        {
            var w = Writer(fs);
            w.RenameEntry(fs.ResolvePath("/bench")!.InodeNumber, "s000.bin", "renamed.bin");
            w.UpdateSuperblock();
        });

        Measure("delete 300 small files, one batch", fs =>
        {
            var w = Writer(fs);
            long parent = fs.ResolvePath("/bench")!.InodeNumber;
            foreach (var e in fs.ReadDirectory(fs.ReadInode(parent)).Where(e => e.Name is not ("." or "..")).ToList())
                w.DeleteFile(parent, e.Name, e.InodeNumber);
            w.UpdateSuperblock();
        });

        Measure("delete the 64 MiB file", fs =>
        {
            var w = Writer(fs);
            w.DeleteFile(2, "big.bin", fs.ResolvePath("/big.bin")!.InodeNumber);
            w.UpdateSuperblock();
        });

        // Raw crypto throughput, to separate cipher cost from I/O pattern cost.
        using (var xts = new AesXts128(DataKey, TweakKey))
        {
            byte[] buf = new byte[4 * 1024 * 1024];
            var t = Stopwatch.StartNew();
            for (int i = 0; i < 64; i++) xts.DecryptSectors(buf, i * 8192L);
            Report("crypto: XTS decrypt 256 MiB in 4 MiB calls", t.Elapsed, null);
            byte[] small = new byte[512];
            t.Restart();
            for (int i = 0; i < 65_536; i++) xts.DecryptSectors(small, i);
            Report("crypto: XTS decrypt 32 MiB in 512 B calls", t.Elapsed, null);
        }

        File.Delete(enc);
        Console.WriteLine("BENCH OK");
        return 0;
    }

    private static int Walk(Ufs2FileSystem fs)
    {
        int count = 0;
        var stack = new Stack<long>();
        stack.Push(2);
        while (stack.Count > 0)
        {
            var dir = fs.ReadInode(stack.Pop());
            foreach (var e in fs.ReadDirectory(dir))
            {
                if (e.Name is "." or "..") continue;
                var inode = fs.ReadInode(e.InodeNumber);
                count++;
                if (inode.FileType == Ufs2FileType.Directory) stack.Push(e.InodeNumber);
            }
        }
        return count;
    }

    private static void Report(string name, TimeSpan elapsed, CountingDiskSource? c)
    {
        string io = c == null ? "" :
            $" | reads {c.Reads} ({c.ReadBytes / 1048576.0:F1} MiB) | writes {c.Writes} ({c.WriteBytes / 1048576.0:F1} MiB) | flushes {c.Flushes}";
        Console.WriteLine($"::notice title=bench::{name}: {elapsed.TotalMilliseconds:F0} ms{io}");
    }

    private static void EncryptImage(string plain, string enc)
    {
        using var input = File.OpenRead(plain);
        using var output = new FileStream(enc, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var xts = new AesXts128(DataKey, TweakKey);
        byte[] buf = new byte[8 * 1024 * 1024];
        long sector = 0;
        int n;
        while ((n = input.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false)) > 0)
        {
            byte[] chunk = n == buf.Length ? buf : buf[..n];
            byte[] c = xts.EncryptSectors(chunk, sector);
            Bswap16.SwapInPlace(c);
            output.Write(c, 0, c.Length);
            sector += n / 512;
        }
    }

    private sealed class HashingSink : Stream
    {
        private readonly System.Security.Cryptography.IncrementalHash _h =
            System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _h.AppendData(buffer, offset, count);
        protected override void Dispose(bool disposing) { if (disposing) _h.Dispose(); base.Dispose(disposing); }
    }
}

/// <summary>Counts physical I/O issued to the wrapped source.</summary>
internal sealed class CountingDiskSource(IDiskSource inner) : IDiskSource
{
    public long Reads, ReadBytes, Writes, WriteBytes, Flushes;
    public void Reset() { Reads = ReadBytes = Writes = WriteBytes = Flushes = 0; }

    public long TotalSize => inner.TotalSize;
    public int SectorSize => inner.SectorSize;
    public long SectorCount => inner.SectorCount;
    public string Description => inner.Description;
    public bool CanWrite => inner.CanWrite;

    public byte[] ReadSectors(long startSector, int count)
    {
        Interlocked.Increment(ref Reads); Interlocked.Add(ref ReadBytes, (long)count * 512);
        return inner.ReadSectors(startSector, count);
    }
    public byte[] ReadBytes(long offset, int count)
    {
        Interlocked.Increment(ref Reads); Interlocked.Add(ref ReadBytes, count);
        return inner.ReadBytes(offset, count);
    }
    public void WriteSectors(long startSector, byte[] data) => WriteBytes(startSector * 512, (ReadOnlySpan<byte>)data);
    public void WriteBytes(long offset, byte[] data) => WriteBytes(offset, (ReadOnlySpan<byte>)data);
    public void WriteBytes(long offset, ReadOnlySpan<byte> data)
    {
        Interlocked.Increment(ref Writes); Interlocked.Add(ref WriteBytes, data.Length);
        inner.WriteBytes(offset, data);
    }
    public void Flush() { Interlocked.Increment(ref Flushes); inner.Flush(); }
    public void Dispose() { }
}
