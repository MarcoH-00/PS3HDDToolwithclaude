using System.Security.Cryptography;
using PS3HddTool.Core.Disk;
using PS3HddTool.Core.FileSystem;

// Writer harness: drives Ufs2Writer against a plain (unencrypted) big-endian UFS2 image
// and checks every result by reading it back. The CI job then runs NetBSD's fsck_ffs on
// the snapshots this program leaves behind, which is the independent check that the
// on-disk structures (bitmaps, counters, link counts, block counts) are consistent.
//
// Usage: PS3HddTool.WriterHarness <image.img> <snapshot-dir>

internal static class Program
{
    private static readonly Dictionary<string, byte[]?> Expected = new(StringComparer.Ordinal); // null = directory
    private static string _image = "";
    private static int _checks;

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: PS3HddTool.WriterHarness <image.img> <snapshot-dir>");
            return 2;
        }
        _image = Path.GetFullPath(args[0]);
        string snapshots = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(snapshots);

        try
        {
            Phase("superblock probe", ProbeSuperblock);
            Phase("baseline mount", () => WithFs(fs => Log($"ncg={fs.Superblock!.CylinderGroups} ipg={fs.Superblock.InodesPerGroup} bsize={fs.Superblock.BlockSize} fsize={fs.Superblock.FragmentSize}")));

            Phase("create directories", () =>
            {
                Mkdir("/t");
                Mkdir("/t/sub");
                Mkdir("/t/many");
                Mkdir("/t/move");
                Mkdir("/t/sub/deep");
            });

            long[] sizes =
            {
                0, 1, 511, 4095, 4096, 4097, 16383, 16384, 16385, 100_000,
                12 * 16384, 12 * 16384 + 1,           // last direct-only file, first single-indirect file
                5 * 1024 * 1024 + 123,                // single indirect
                34 * 1024 * 1024 + 4567,              // double indirect (> 12 + 2048 blocks of 16 KiB)
            };
            Phase("write files of every size class", () =>
            {
                foreach (long size in sizes) WriteFile($"/t/f_{size}", size);
                WriteFile("/t/sub/deep/nested.bin", 70_000);
                WriteFile("/t/ñandú – unicode.txt", 3_000);
            });

            Phase("issue #7: more files than one cylinder group has inodes", () =>
            {
                int ipg = (int)WithFs(fs => fs.Superblock!.InodesPerGroup);
                int count = ipg + 40;
                Log($"writing {count} small files into /t/many (ipg={ipg})");
                Batch(w =>
                {
                    long parent = Inode("/t/many");
                    for (int i = 0; i < count; i++)
                    {
                        string path = $"/t/many/m{i:D4}";
                        byte[] data = Data(path, 1 + (i * 37) % 9000);
                        w.WriteFile(parent, Path.GetFileName(path), new MemoryStream(data), data.Length);
                        Expected[path] = Sha(data);
                    }
                });
            });

            Phase("verify after writes", VerifyAll);
            Snapshot(snapshots, "01-after-writes.img");

            Phase("input validation", () =>
            {
                ExpectThrow<ArgumentException>("name with '/'", () => Batch(w => w.WriteFile(Inode("/t"), "a/b", new MemoryStream(new byte[1]), 1)));
                ExpectThrow<ArgumentException>("name of 256 bytes", () => Batch(w => w.CreateDirectory(Inode("/t"), new string('x', 256))));
                ExpectThrow<IOException>("duplicate name", () => Batch(w => w.WriteFile(Inode("/t"), "f_1", new MemoryStream(new byte[1]), 1)));
                ExpectThrow<EndOfStreamException>("source shorter than declared size", () =>
                    Batch(w => w.WriteFile(Inode("/t"), "short.bin", new MemoryStream(new byte[50_000]), 100_000)));
                Check(!Exists("/t/short.bin"), "a failed write must not leave a directory entry");
                ExpectThrow<EndOfStreamException>("short source, indirect-sized file", () =>
                    Batch(w => w.WriteFile(Inode("/t"), "short-big.bin", new MemoryStream(new byte[300_000]), 3_000_000)));
                Check(!Exists("/t/short-big.bin"), "a failed large write must not leave a directory entry");
            });

            Phase("rename and move", () =>
            {
                Batch(w => w.RenameEntry(Inode("/t"), "f_4097", "renamed.bin"));
                MoveExpected("/t/f_4097", "/t/renamed.bin");

                Batch(w => w.RenameEntry(Inode("/t"), "sub", "sub2"));
                MoveExpected("/t/sub", "/t/sub2");

                Batch(w => w.MoveEntry(Inode("/t"), Inode("/t/move"), "f_100000", Inode("/t/f_100000"), false));
                MoveExpected("/t/f_100000", "/t/move/f_100000");

                Batch(w => w.MoveEntry(Inode("/t"), Inode("/t/move"), "sub2", Inode("/t/sub2"), true));
                MoveExpected("/t/sub2", "/t/move/sub2");
                Check(WithFs(fs => ParentOf(fs, "/t/move/sub2")) == Inode("/t/move"), "'..' of a moved folder must point at its new parent");

                ExpectThrow<IOException>("moving a folder inside itself", () =>
                    Batch(w => w.MoveEntry(Inode("/t"), Inode("/t/move/sub2/deep"), "move", Inode("/t/move"), true)));
                ExpectThrow<IOException>("rename onto an existing name", () =>
                    Batch(w => w.RenameEntry(Inode("/t"), "f_1", "f_511")));
            });

            Phase("verify after rename/move", VerifyAll);
            Snapshot(snapshots, "02-after-moves.img");

            Phase("delete files and folders", () =>
            {
                foreach (string path in new[] { "/t/f_1", "/t/f_16385", "/t/f_4095", $"/t/f_{12 * 16384 + 1}", $"/t/f_{34 * 1024 * 1024 + 4567}", "/t/f_0" })
                {
                    long parent = Inode(Path.GetDirectoryName(path)!.Replace('\\', '/'));
                    long ino = Inode(path);
                    Batch(w => w.DeleteFile(parent, Path.GetFileName(path), ino));
                    Expected.Remove(path);
                }
                long manyIno = Inode("/t/many");
                Batch(w => w.DeleteDirectory(Inode("/t"), "many", manyIno));
                foreach (var key in Expected.Keys.Where(k => k == "/t/many" || k.StartsWith("/t/many/", StringComparison.Ordinal)).ToList())
                    Expected.Remove(key);
            });

            Phase("re-use freed space", () =>
            {
                for (int i = 0; i < 20; i++) WriteFile($"/t/reuse_{i}", 1 + i * 5_000);
                WriteFile("/t/reuse_big", 3 * 1024 * 1024 + 77);
            });

            Phase("verify final state", VerifyAll);
            Snapshot(snapshots, "03-final.img");

            Console.WriteLine($"HARNESS OK — {_checks} checks passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"::error title=Writer harness failed::{ex.GetType().Name}: {ex.Message}".Replace("\r", " ").Replace("\n", " "));
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // ── operations ──────────────────────────────────────────────────────────────

    private static void Mkdir(string path)
    {
        string parent = ParentPath(path);
        Batch(w => w.CreateDirectory(Inode(parent), Path.GetFileName(path)));
        Expected[path] = null;
    }

    private static void WriteFile(string path, long size)
    {
        byte[] data = Data(path, size);
        string parent = ParentPath(path);
        Batch(w => w.WriteFile(Inode(parent), Path.GetFileName(path), new MemoryStream(data), data.Length));
        Expected[path] = Sha(data);
    }

    private static void MoveExpected(string from, string to)
    {
        foreach (var key in Expected.Keys.Where(k => k == from || k.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
        {
            var value = Expected[key];
            Expected.Remove(key);
            Expected[to + key[from.Length..]] = value;
        }
    }

    /// <summary>One writer per operation, finished with UpdateSuperblock — the same pattern the GUI uses.</summary>
    private static ImageDiskSource? _openDisk; // set while a Batch runs, so lookups reuse its handle

    private static void Batch(Action<Ufs2Writer> action)
    {
        using var disk = new ImageDiskSource(_image, writable: true);
        _openDisk = disk;
        try { RunBatch(disk, action); }
        finally { _openDisk = null; }
    }

    private static void RunBatch(ImageDiskSource disk, Action<Ufs2Writer> action)
    {
        var fs = new Ufs2FileSystem(disk, 0);
        if (!fs.Mount()) throw new InvalidDataException("Image does not contain a UFS2 filesystem at offset 0.");
        var writer = new Ufs2Writer(fs, disk, 0, dryRun: false, log: msg => { if (msg.Contains("ERROR") || msg.Contains("NOTE") || msg.Contains("MISMATCH") || msg.Contains("Rolled back") || msg.Contains("no free inodes")) Log(msg.Trim()); });
        action(writer);
        writer.UpdateSuperblock();
        writer.VerifyCgBitmaps();
    }

    private static T WithFs<T>(Func<Ufs2FileSystem, T> func)
    {
        if (_openDisk != null)
        {
            var shared = new Ufs2FileSystem(_openDisk, 0);
            if (!shared.Mount()) throw new InvalidDataException("Image does not contain a UFS2 filesystem at offset 0.");
            return func(shared);
        }
        using var disk = new ImageDiskSource(_image, writable: false);
        var fs = new Ufs2FileSystem(disk, 0);
        if (!fs.Mount()) throw new InvalidDataException("Image does not contain a UFS2 filesystem at offset 0.");
        return func(fs);
    }

    private static void WithFs(Action<Ufs2FileSystem> action) => WithFs<int>(fs => { action(fs); return 0; });

    private static long Inode(string path) => WithFs(fs =>
        fs.ResolvePath(path)?.InodeNumber ?? throw new FileNotFoundException($"Path not found on image: {path}"));

    private static bool Exists(string path) => WithFs(fs => fs.ResolvePath(path) != null);

    private static long ParentOf(Ufs2FileSystem fs, string dirPath)
    {
        var dir = fs.ResolvePath(dirPath) ?? throw new FileNotFoundException(dirPath);
        return fs.ReadDirectory(dir).Single(e => e.Name == "..").InodeNumber;
    }

    private static void VerifyAll() => WithFs(fs =>
    {
        foreach (var (path, sha) in Expected.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var inode = fs.ResolvePath(path);
            Check(inode != null, $"{path} is missing");
            if (sha == null)
            {
                Check(inode!.FileType == Ufs2FileType.Directory, $"{path} should be a directory");
                var names = fs.ReadDirectory(inode).Select(e => e.Name).ToList();
                Check(names.Count(n => n == ".") == 1 && names.Count(n => n == "..") == 1, $"{path} must have exactly one '.' and '..'");
                Check(names.Count == names.Distinct(StringComparer.Ordinal).Count(), $"{path} has duplicate names");
                // Every child we expect is listed, and nothing unexpected is.
                var expectedChildren = Expected.Keys.Where(k => ParentPath(k) == path).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
                var actualChildren = names.Where(n => n != "." && n != "..").ToHashSet(StringComparer.Ordinal);
                Check(expectedChildren.SetEquals(actualChildren!),
                    $"{path} lists [{string.Join(", ", actualChildren.Order())}] but expected [{string.Join(", ", expectedChildren.Order())}]");
            }
            else
            {
                Check(inode!.FileType == Ufs2FileType.RegularFile, $"{path} should be a regular file");
                using var ms = new MemoryStream();
                fs.ExtractInodeToStream(inode, ms);
                Check(ms.Length == inode.Size, $"{path}: extracted {ms.Length} bytes, inode says {inode.Size}");
                Check(Sha(ms.ToArray()).AsSpan().SequenceEqual(sha), $"{path}: content differs after read-back");
            }
        }
        Log($"verified {Expected.Count} paths");
    });

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static void ProbeSuperblock()
    {
        using var f = File.OpenRead(_image);
        Log($"image size {f.Length} bytes");
        foreach (long sbOff in new long[] { 0, 8192, 65536, 262144 })
        {
            var buf = new byte[0x560];
            f.Position = sbOff;
            f.ReadExactly(buf);
            uint be = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0x55C));
            uint le = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0x55C));
            Console.WriteLine($"::warning title=SB probe {sbOff}::magic BE=0x{be:X8} LE=0x{le:X8} size={f.Length}");
            Log($"offset {sbOff}: magic BE=0x{be:X8} LE=0x{le:X8} bsize(BE)={System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(0x30))}");
        }
    }

    private static string ParentPath(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private static byte[] Data(string seed, long size)
    {
        var data = new byte[size];
        new Random(StringComparer.Ordinal.GetHashCode(seed) ^ (int)size ^ 0x5EED).NextBytes(data);
        return data;
    }

    private static byte[] Sha(byte[] data) => SHA256.HashData(data);

    private static void Snapshot(string dir, string name)
    {
        string dest = Path.Combine(dir, name);
        File.Copy(_image, dest, overwrite: true);
        Log($"snapshot written: {dest}");
    }

    private static void ExpectThrow<T>(string what, Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { Log($"rejected as expected ({what}): {ex.Message}"); _checks++; return; }
        throw new Exception($"Expected {typeof(T).Name} for: {what}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }

    private static void Phase(string name, Action action)
    {
        Console.WriteLine($"── {name}");
        action();
    }

    private static void Log(string message) => Console.WriteLine($"   {message}");
}
