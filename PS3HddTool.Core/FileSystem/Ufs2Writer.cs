using System.Buffers.Binary;
using PS3HddTool.Core.Disk;

namespace PS3HddTool.Core.FileSystem;

/// <summary>
/// UFS2 filesystem write operations for PS3 HDD.
/// Supports dry-run mode that logs all changes without writing.
/// 
/// Write operations:
///   - CreateDirectory: create a new empty directory
///   - WriteFile: copy a file from host into the filesystem
///   - AddDirectoryEntry: add an entry to an existing directory
///   - AllocateInode: find and allocate a free inode in a CG
///   - AllocateBlocks: find and allocate free data blocks in a CG
/// </summary>
public class Ufs2Writer
{
    private readonly Ufs2FileSystem _fs;
    private readonly IDiskSource _disk;
    private readonly PS3HddTool.Core.Disk.IPipelinedDiskSource? _pipelinedDisk;
    private readonly IDiskSource _writeTarget;   // RawSource if pipelined, else use _disk
    private readonly long _partitionOffset;
    private readonly bool _dryRun;
    private readonly Action<string> _log;

    // Pending writes for dry-run mode
    private readonly List<PendingWrite> _pendingWrites = new();
    private long _currentL1Block; // tracks current L1 indirect block during double-indirect writes

    // Protected fragments — directory blocks that must not be overwritten even if bitmap says free
    private readonly HashSet<long> _protectedFragments = new();

    // CG cache — keeps modified CGs in memory so re-reads get the updated version
    private readonly Dictionary<int, CylinderGroupInfo> _cgCache = new();
    
    // Dirty CG tracking — when DeferCgWrites is true, CGs are marked dirty instead of written immediately
    private readonly HashSet<int> _dirtyCgs = new();

    public IReadOnlyList<PendingWrite> PendingWrites => _pendingWrites;
    
    /// <summary>
    /// When true, emits detailed hex dumps of inodes, directory blocks, and indirect chains.
    /// </summary>
    public bool VerboseLog { get; set; } = false;
    
    /// <summary>
    /// When true, WriteCylinderGroup defers disk writes. Call FlushDirtyCgs() to write all at once.
    /// </summary>
    public bool DeferCgWrites { get; set; } = true;

    // Persistent data write batch state — survives across WriteFile calls for small file batching
    private const int MAX_BATCH = 1024; // 1024 * 16KB = 16MB batches
    private byte[][]? _pingPong;
    private int _activeBuf = 0;
    private long _batchStartFrag = -1;
    private int _batchBlockCount = 0;
    private int _batchCg = -1;
    private Task? _pendingWrite;

    // three stage pipeline (used when _pipelinedDisk != null): main thread only reads source data;
    // each batch's encrypt+write runs as a chained worker task, so read(N+1) || encrypt(N) || write(N-1)
    // two 4k aligned native ciphertext buffers ping pong between encrypt and write so encrypt(N+1) overlaps write(N) w/o contending for the same memory - sage
    private IntPtr[]? _encPingPongPtr;
    private int _encPingPongCapacity = 0;
    private int _encActiveBuf = 0;
    private readonly Task?[] _writeTaskPerEncSlot = new Task?[2];
    private readonly Task?[] _ptEncDone = new Task?[2];

    // Batch block allocation — avoids per-block bitmap scanning for large files
    private long _batchAllocNextFrag = -1;
    private int _batchAllocRemaining = 0;
    private int _batchAllocCg = -1;

    private void EnsureBatchBuffers(int blockSize)
    {
        if (_pingPong == null || _pingPong[0].Length != blockSize * MAX_BATCH)
        {
            _pingPong = new byte[2][];
            _pingPong[0] = new byte[blockSize * MAX_BATCH];
            _pingPong[1] = new byte[blockSize * MAX_BATCH];
        }
    }

    private unsafe void EnsureEncBuffers(int blockSize)
    {
        int needed = blockSize * MAX_BATCH;
        if (_encPingPongPtr == null || _encPingPongCapacity < needed)
        {
            if (_encPingPongPtr != null)
            {
                for (int i = 0; i < 2; i++)
                    if (_encPingPongPtr[i] != IntPtr.Zero)
                        System.Runtime.InteropServices.NativeMemory.AlignedFree((void*)_encPingPongPtr[i]);
            }
            _encPingPongPtr = new IntPtr[2];
            _encPingPongPtr[0] = (IntPtr)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)needed, 4096);
            _encPingPongPtr[1] = (IntPtr)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)needed, 4096);
            _encPingPongCapacity = needed;
        }
    }

    private unsafe Span<byte> EncSpan(int slot, int length)
        => new Span<byte>((void*)_encPingPongPtr![slot], length);

    private void InternalFlushBatch(int blockSize)
    {
        if (_batchBlockCount <= 0) return;
        long offset = _partitionOffset + (_batchStartFrag * _fs.Superblock!.FragmentSize);
        int totalBytes = _batchBlockCount * blockSize;
        if (_dryRun)
        {
            _batchStartFrag = -1;
            _batchBlockCount = 0;
            return;
        }

        if (_pipelinedDisk != null)
        {
            EnsureEncBuffers(blockSize);
            int ptSlot = _activeBuf;
            int encSlot = _encActiveBuf;

            var encSlotBusy = _writeTaskPerEncSlot[encSlot]; // write that last used this ciphertext buffer
            var prevChain = _pendingWrite; // previous batch's encrypt+write chain

            long capturedOffset = offset;
            int capturedBytes = totalBytes;
            var ptFree = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var chain = Task.Run(() =>
            {
                try
                {
                    encSlotBusy?.Wait();
                    _pipelinedDisk.EncryptForWrite(_pingPong![ptSlot], capturedBytes, capturedOffset, EncSpan(encSlot, capturedBytes));
                    ptFree.SetResult();
                }
                catch (Exception ex)
                {
                    ptFree.TrySetException(ex);
                    throw;
                }
                prevChain?.Wait();
                _writeTarget.WriteBytes(capturedOffset, EncSpan(encSlot, capturedBytes));
            });

            _pendingWrite = chain;
            _writeTaskPerEncSlot[encSlot] = chain;
            _ptEncDone[ptSlot] = ptFree.Task;

            _activeBuf = 1 - _activeBuf;
            _encActiveBuf = 1 - _encActiveBuf;

            _ptEncDone[_activeBuf]?.Wait();
        }
        else
        {
            _pendingWrite?.Wait();
            
            byte[] writeData;
            if (totalBytes == _pingPong![_activeBuf].Length)
                writeData = _pingPong[_activeBuf];
            else
            {
                writeData = new byte[totalBytes];
                Buffer.BlockCopy(_pingPong![_activeBuf], 0, writeData, 0, totalBytes);
            }
            
            long capturedOffset = offset;
            _pendingWrite = Task.Run(() => _disk.WriteBytes(capturedOffset, writeData));
            _activeBuf = 1 - _activeBuf;
        }
        _batchStartFrag = -1;
        _batchBlockCount = 0;
    }
    
    /// <summary>
    /// Flush any pending data write batch and wait for completion.
    /// Call this after a batch of small file writes, or before UpdateSuperblock.
    /// </summary>
    public void FlushDataBatch()
    {
        if (_pingPong != null)
        {
            int blockSize = (int)_fs.Superblock!.BlockSize;
            InternalFlushBatch(blockSize);
            _pendingWrite?.Wait();
            _writeTaskPerEncSlot[0]?.Wait();
            _writeTaskPerEncSlot[1]?.Wait();
            _pendingWrite = null;
            _writeTaskPerEncSlot[0] = null;
            _writeTaskPerEncSlot[1] = null;
            _ptEncDone[0] = null;
            _ptEncDone[1] = null;
        }
    }

    /// <summary>
    /// Collect all data block fragments used by an inode and add them to the protected set.
    /// </summary>
    private void CollectProtectedFragments(long inodeNumber)
    {
        try
        {
            var inode = _fs.ReadInode(inodeNumber);
            for (int i = 0; i < 12; i++)
            {
                long frag = inode.DirectBlocks[i];
                if (frag == 0) break;
                _protectedFragments.Add(frag);
            }
        }
        catch (Exception ex) { _log($"  NOTE could not read inode {inodeNumber} to protect its blocks: {ex.Message}"); }
    }

    public Ufs2Writer(Ufs2FileSystem fs, IDiskSource disk, long partitionOffsetBytes, bool dryRun, Action<string> log)
    {
        if (fs.Superblock?.IsLittleEndian == true || disk is Ps4PartitionSource)
            throw new NotSupportedException("PS4 filesystem support is read-only.");
        _fs = fs;
        _disk = disk;
        _partitionOffset = partitionOffsetBytes;
        _dryRun = dryRun;
        _log = log;
        _pipelinedDisk = disk as PS3HddTool.Core.Disk.IPipelinedDiskSource;
        _writeTarget = _pipelinedDisk?.RawSource ?? disk;
    }

    ~Ufs2Writer()
    {
        unsafe
        {
            if (_encPingPongPtr != null)
            {
                for (int i = 0; i < 2; i++)
                    if (_encPingPongPtr[i] != IntPtr.Zero)
                        System.Runtime.InteropServices.NativeMemory.AlignedFree((void*)_encPingPongPtr[i]);
                _encPingPongPtr = null;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Safety helpers (fork hardening, phase 1)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When true (default), allocation bitmaps and file data are forced to disk before
    /// any inode or directory entry that points at them is written, and directory
    /// entries are removed before the inode and blocks they name are released.
    /// A crash then leaves at worst leaked space (which fsck reclaims), never a live
    /// file pointing at blocks the allocator considers free.
    /// </summary>
    public bool SafeWriteOrdering { get; set; } = true;

    /// <summary>Maximum UFS2 name length in bytes (MAXNAMLEN).</summary>
    public const int MaxNameBytes = 255;

    /// <summary>
    /// Reject names the on-disk format or the PS3 cannot represent.
    /// </summary>
    public static byte[] ValidateEntryName(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Name cannot be empty.", nameof(name));
        if (name == "." || name == "..")
            throw new ArgumentException($"'{name}' is reserved.", nameof(name));
        if (name.IndexOf('/') >= 0 || name.IndexOf('\0') >= 0)
            throw new ArgumentException("Name cannot contain '/' or NUL characters.", nameof(name));
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
        if (bytes.Length > MaxNameBytes)
            throw new ArgumentException($"Name is {bytes.Length} bytes in UTF-8; the limit is {MaxNameBytes}.", nameof(name));
        return bytes;
    }

    private static bool NameEquals(byte[] block, int offset, int nameLen, byte[] nameBytes)
    {
        if (nameLen != nameBytes.Length || offset + nameLen > block.Length) return false;
        return block.AsSpan(offset, nameLen).SequenceEqual(nameBytes);
    }

    /// <summary>
    /// Push pending file data and dirty cylinder groups to the disk and wait until the
    /// disk reports them stable. Call before writing metadata that references them.
    /// </summary>
    public void DurabilityBarrier()
    {
        if (_dryRun) return;
        FlushDataBatch();
        FlushDirtyCgs();
        _disk.Flush();
    }

    private long InodeDiskOffset(long inodeNumber)
    {
        var sb = _fs.Superblock!;
        return _partitionOffset + (inodeNumber / sb.InodesPerGroup) * sb.FragsPerGroup * sb.FragmentSize
            + sb.InodeBlockOffset * sb.FragmentSize + (inodeNumber % sb.InodesPerGroup) * sb.InodeSize;
    }

    private byte[] ReadRawInode(long inodeNumber)
        => _disk.ReadBytes(InodeDiskOffset(inodeNumber), (int)_fs.Superblock!.InodeSize);

    private void CheckFragmentAddress(long absFrag, int count, string what)
    {
        var sb = _fs.Superblock!;
        long total = sb.TotalFragments > 0 ? sb.TotalFragments : (long)sb.CylinderGroups * sb.FragsPerGroup;
        int fpb = (int)(sb.BlockSize / sb.FragmentSize);
        if (absFrag <= 0 || count <= 0 || count > fpb || absFrag + count > total)
            throw new InvalidDataException($"{what} points outside the filesystem (fragment 0x{absFrag:X}, count {count}).");
        if ((absFrag % sb.FragsPerGroup) + count > sb.FragsPerGroup)
            throw new InvalidDataException($"{what} crosses a cylinder group boundary (fragment 0x{absFrag:X}).");
        if ((absFrag % fpb) + count > fpb)
            throw new InvalidDataException($"{what} crosses a block boundary (fragment 0x{absFrag:X}, count {count}).");
    }

    private bool IsFragmentFree(CylinderGroupInfo cg, int localFrag)
        => (cg.RawData[cg.FreeBlocksOffset + (localFrag / 8)] & (1 << (localFrag % 8))) != 0;

    /// <summary>Mark an allocated run (all inside one block) free again.</summary>
    private void FreeFragmentRun(long absFrag, int count)
    {
        var sb = _fs.Superblock!;
        int cgNum = (int)(absFrag / sb.FragsPerGroup);
        int local = (int)(absFrag % sb.FragsPerGroup);
        var cg = ReadCylinderGroup(cgNum);
        MarkFragmentsFree(cg, local, count);
        WriteCylinderGroup(cg);
    }

    /// <summary>
    /// Allocate an inode, preferring <paramref name="preferredCg"/> and falling back to
    /// every other cylinder group (FFS ffs_hashalloc behaviour). Fixes upstream issue #7
    /// ("No free inodes in CG") where installs failed once the parent's group filled up.
    /// </summary>
    private int FindFreeInodeAnyCg(int preferredCg, out int inodeCg, out CylinderGroupInfo inodeCgInfo)
    {
        var sb = _fs.Superblock!;
        for (int i = 0; i < sb.CylinderGroups; i++)
        {
            int tryCg = (preferredCg + i) % sb.CylinderGroups;
            var info = ReadCylinderGroup(tryCg);
            if (info.Magic != 0x00090255) continue;
            if (info.FreeInodes <= 0 && i > 0) continue; // header says full: skip the bitmap scan
            int idx = FindFreeInode(info);
            if (idx < 0) continue;
            if (tryCg == 0 && idx < 3) continue; // inodes 0-2 are reserved (root is 2)
            inodeCg = tryCg;
            inodeCgInfo = info;
            if (i > 0) _log($"  CG {preferredCg} has no free inodes; using CG {tryCg}");
            return idx;
        }
        throw new IOException("No free inodes left on the filesystem.");
    }

    /// <summary>
    /// Give back blocks pre-reserved by the batch allocator but never used. Before this,
    /// they stayed marked "in use" with no owner (space leak, fsck "BLK(S) MISSING").
    /// </summary>
    private void ReleaseBatchReservation()
    {
        if (_batchAllocRemaining > 0 && _batchAllocCg >= 0)
        {
            var sb = _fs.Superblock!;
            int fpb = (int)(sb.BlockSize / sb.FragmentSize);
            var cg = ReadCylinderGroup(_batchAllocCg);
            for (int i = 0; i < _batchAllocRemaining; i++)
                MarkFragmentsFree(cg, (int)(_batchAllocNextFrag + (long)i * fpb), fpb);
            WriteCylinderGroup(cg);
            _log($"  Released {_batchAllocRemaining} unused reserved block(s) in CG {_batchAllocCg}");
        }
        _batchAllocRemaining = 0;
        _batchAllocNextFrag = -1;
        _batchAllocCg = -1;
    }

    /// <summary>
    /// Rebuild a cylinder group's derived summary fields from its bitmaps, the way
    /// fsck_ffs pass 5 does: cs_nbfree, cs_nffree, cs_nifree, cg_frsum, the cluster
    /// bitmap and cg_clustersum. The incremental updates elsewhere had gaps (freeing a
    /// block never updated the cluster summary, for example); recomputing before every
    /// CG write makes those counters correct by construction.
    /// cs_ndir is not derivable from the bitmaps and is still maintained incrementally.
    /// </summary>
    public void RecomputeCgSummary(CylinderGroupInfo cg)
    {
        if (cg.Magic != 0x00090255) return;
        var sb = _fs.Superblock!;
        int fpb = (int)(sb.BlockSize / sb.FragmentSize);
        byte[] d = cg.RawData;
        int ndblk = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(0x14));
        int freeoff = cg.FreeBlocksOffset;
        if (ndblk <= 0 || freeoff <= 0 || freeoff + (ndblk + 7) / 8 > d.Length) return;

        int clusteroff = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(0x6C));
        int clustersumoff = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(0x68));
        int nclusterblks = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(0x70));
        bool clusters = clusteroff > 0 && clustersumoff > 0 && nclusterblks > 0
            && clusteroff > clustersumoff && clusteroff + (nclusterblks + 7) / 8 <= d.Length;

        int nbfree = 0, nffree = 0;
        var frsum = new int[8];
        int blk = 0;
        for (int start = 0; start < ndblk; start += fpb, blk++)
        {
            int frags = Math.Min(fpb, ndblk - start);
            int free = 0, run = 0;
            for (int f = start; f < start + frags; f++)
            {
                if ((d[freeoff + (f / 8)] & (1 << (f % 8))) != 0) { free++; run++; }
                else { if (run > 0 && run < fpb) frsum[run]++; run = 0; }
            }
            bool wholeBlockFree = free == fpb;
            if (wholeBlockFree) nbfree++;
            else
            {
                if (run > 0 && run < fpb) frsum[run]++;
                nffree += free;
            }
            if (clusters && blk < nclusterblks)
            {
                int cb = clusteroff + (blk / 8);
                if (wholeBlockFree) d[cb] |= (byte)(1 << (blk % 8));
                else d[cb] &= (byte)~(1 << (blk % 8));
            }
        }

        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0x1C), nbfree);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0x24), nffree);
        cg.FreeBlocks = nbfree;
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0x34 + i * 4), i < fpb ? frsum[i] : 0);

        if (clusters)
        {
            int contigsumsize = (clusteroff - clustersumoff) / 4 - 1;
            if (contigsumsize > 0)
            {
                var sums = new int[contigsumsize + 1];
                int run = 0;
                for (int b = 0; b < nclusterblks; b++)
                {
                    if ((d[clusteroff + (b / 8)] & (1 << (b % 8))) != 0) run++;
                    else if (run > 0) { sums[Math.Min(run, contigsumsize)]++; run = 0; }
                }
                if (run > 0) sums[Math.Min(run, contigsumsize)]++;
                // Index 0 is never written: newfs places cg_clustersumoff 4 bytes early so
                // that unused slot overlaps the tail of the free-fragment bitmap.
                for (int i = 1; i <= contigsumsize; i++)
                    BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(clustersumoff + i * 4), sums[i]);
            }
        }

        int ipg = (int)sb.InodesPerGroup;
        int iusedoff = cg.InodesUsedOffset;
        if (iusedoff > 0 && iusedoff + (ipg + 7) / 8 <= d.Length)
        {
            int nifree = 0;
            for (int i = 0; i < ipg; i++)
                if ((d[iusedoff + (i / 8)] & (1 << (i % 8))) == 0) nifree++;
            BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0x20), nifree);
            cg.FreeInodes = nifree;
        }
    }

    /// <summary>Location of a named entry inside a directory's data.</summary>
    private sealed class EntryLocation
    {
        public int BlockIndex;
        public long BlockDiskOffset;
        public byte[] Block = Array.Empty<byte>();
        public int Offset;
        public int PrevOffset;   // previous live entry in the same 512-byte section, or -1
        public uint Inode;
        public byte Type;
    }

    private EntryLocation? LocateEntry(long dirInodeNumber, byte[] nameBytes)
    {
        var sb = _fs.Superblock!;
        var dir = _fs.ReadInode(dirInodeNumber);
        if (dir.FileType != Ufs2FileType.Directory)
            throw new IOException($"Inode {dirInodeNumber} is not a directory.");
        long bytesLeft = dir.Size;
        long allocBytes = dir.Blocks * 512;

        for (int i = 0; i < 12 && bytesLeft > 0; i++)
        {
            long blockAddr = dir.DirectBlocks[i];
            if (blockAddr == 0) break;
            int blockCapacity = (int)sb.BlockSize; // reads are bounded by di_size below
            int thisBlockSize = (int)Math.Min(blockCapacity, bytesLeft);
            long blockDiskOffset = _partitionOffset + blockAddr * sb.FragmentSize;
            byte[] block = _disk.ReadBytes(blockDiskOffset, thisBlockSize);

            for (int sectionStart = 0; sectionStart + 512 <= thisBlockSize; sectionStart += 512)
            {
                int sectionEnd = sectionStart + 512;
                int offset = sectionStart, prev = -1;
                while (offset + 8 <= sectionEnd)
                {
                    uint ino = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(offset));
                    ushort recLen = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(offset + 4));
                    if (recLen < 8 || offset + recLen > sectionEnd)
                        throw new InvalidDataException($"Corrupt directory record in inode {dirInodeNumber} (block {i}, offset {offset}).");
                    byte nameLen = block[offset + 7];
                    if (ino != 0 && NameEquals(block, offset + 8, nameLen, nameBytes))
                    {
                        return new EntryLocation
                        {
                            BlockIndex = i, BlockDiskOffset = blockDiskOffset, Block = block,
                            Offset = offset, PrevOffset = prev, Inode = ino, Type = block[offset + 6]
                        };
                    }
                    if (ino != 0) prev = offset;
                    offset += recLen;
                }
            }
            bytesLeft -= thisBlockSize;
        }
        return null;
    }

    /// <summary>Inode number of ".." inside a directory, read from its first record.</summary>
    private long ReadParentOf(long dirInodeNumber)
    {
        var dotdot = LocateEntry(dirInodeNumber, new[] { (byte)'.', (byte)'.' });
        if (dotdot == null) throw new InvalidDataException($"Directory inode {dirInodeNumber} has no '..' entry.");
        return dotdot.Inode;
    }

    /// <summary>
    /// Read the cylinder group descriptor for the given CG number.
    /// </summary>
    public CylinderGroupInfo ReadCylinderGroup(int cgNumber)
    {
        // Return cached version if available (critical for deferred CG writes)
        if (_cgCache.TryGetValue(cgNumber, out var cached))
            return cached;
        
        var sb = _fs.Superblock!;
        long cgOffset = _partitionOffset + (long)cgNumber * sb.FragsPerGroup * sb.FragmentSize;
        long cgHeaderOffset = cgOffset + sb.InodeBlockOffset * sb.FragmentSize - 4 * sb.FragmentSize;
        // fs_cblkno = fs_iblkno - 4 typically. Read from superblock.
        int cblkno = BinaryPrimitives.ReadInt32BigEndian(_fs.RawSuperblockData.AsSpan(0x0C));
        cgHeaderOffset = cgOffset + (long)cblkno * sb.FragmentSize;

        // CG header is one block (fs_cgsize bytes, typically up to fs_bsize)
        int cgSize = BinaryPrimitives.ReadInt32BigEndian(_fs.RawSuperblockData.AsSpan(0xA0)); // fs_cgsize
        byte[] cgData = _disk.ReadBytes(cgHeaderOffset, cgSize);

        var cg = new CylinderGroupInfo();
        cg.CgNumber = cgNumber;
        cg.DiskOffset = cgHeaderOffset;
        cg.RawData = cgData;

        // Parse CG header (big-endian)
        // struct cg offsets:
        // 0x00: cg_firstfield (unused)
        // 0x04: cg_magic (0x00090255)
        // 0x08: cg_old_time
        // 0x0C: cg_cgx (CG number)
        // 0x10: cg_old_ncyl
        // 0x12: cg_old_niblk
        // 0x14: cg_ndblk (number of data blocks in this CG)
        // 0x18: cg_cs.cs_ndir
        // 0x1C: cg_cs.cs_nbfree (free blocks)
        // 0x20: cg_cs.cs_nifree (free inodes)
        // 0x24: cg_cs.cs_nffree (free frags)
        // 0x28: cg_rotor
        // 0x2C: cg_frotor
        // 0x30: cg_irotor (next free inode)
        // 0x34: cg_frsum[8] (32 bytes)
        // 0x54: cg_old_btotoff
        // 0x58: cg_old_boff
        // 0x5C: cg_iusedoff (offset to used inode bitmap)
        // 0x60: cg_freeoff (offset to free block bitmap)
        // ...

        cg.Magic = BinaryPrimitives.ReadUInt32BigEndian(cgData.AsSpan(0x04));
        cg.CgxFromDisk = BinaryPrimitives.ReadInt32BigEndian(cgData.AsSpan(0x0C));
        cg.NumDataBlocks = BinaryPrimitives.ReadInt32BigEndian(cgData.AsSpan(0x14));
        cg.FreeBlocks = BinaryPrimitives.ReadInt32BigEndian(cgData.AsSpan(0x1C));
        cg.FreeInodes = BinaryPrimitives.ReadInt32BigEndian(cgData.AsSpan(0x20));
        cg.InodesUsedOffset = BinaryPrimitives.ReadInt32BigEndian(cgData.AsSpan(0x5C));
        cg.FreeBlocksOffset = BinaryPrimitives.ReadInt32BigEndian(cgData.AsSpan(0x60));
        cg.InodeRotor = BinaryPrimitives.ReadInt32BigEndian(cgData.AsSpan(0x30));
        cg.InitedIblk = BinaryPrimitives.ReadInt32BigEndian(cgData.AsSpan(0x78)); // cg_initediblk

        _cgCache[cgNumber] = cg;
        return cg;
    }

    /// <summary>
    /// Find a free inode in the given CG's inode bitmap.
    /// Returns the inode index within the CG, or -1 if none free.
    /// </summary>
    public int FindFreeInode(CylinderGroupInfo cg)
    {
        var sb = _fs.Superblock!;
        int ipg = (int)sb.InodesPerGroup;
        int bitmapOffset = cg.InodesUsedOffset;

        for (int i = 0; i < ipg; i++)
        {
            int byteIdx = bitmapOffset + (i / 8);
            int bitIdx = i % 8;
            if (byteIdx < cg.RawData.Length && (cg.RawData[byteIdx] & (1 << bitIdx)) == 0)
                return i; // This inode is free
        }
        return -1;
    }

    /// <summary>
    /// Find N contiguous free fragments in the given CG's block bitmap.
    /// Returns the fragment offset within the CG, or -1 if not enough free.
    /// </summary>
    private readonly Dictionary<int, int> _cgSearchCursor = new();
    private int _cachedDblkno = -1;

    /// <summary>
    /// Find a free fragment at a block-aligned address (frag%fragsPerBlock == 0).
    /// Used for directory block 0 allocation — FreeBSD/PS3 requires this because
    /// the kernel reads di_db[0] as a full block when di_size > fragmentSize.
    /// Allocates a full block (all fragsPerBlock fragments).
    /// </summary>
    public long FindFreeBlockAlignedFragment(CylinderGroupInfo cg, int fragsPerBlock)
    {
        // Just delegate to FindFreeFragments with fragsPerBlock count — 
        // that function already enforces block alignment for count >= fragsPerBlock
        return FindFreeFragments(cg, fragsPerBlock);
    }

    /// <summary>
    /// Find a contiguous run of free blocks starting at a block-aligned address.
    /// Returns (startFrag, blockCount) where blockCount is up to maxBlocks.
    /// Returns startFrag=-1 if no free blocks found.
    /// Much faster than calling FindFreeFragments once per block.
    /// </summary>
    public (long startFrag, int blockCount) FindFreeBlockRun(CylinderGroupInfo cg, int fragsPerBlock, int maxBlocks)
    {
        var sb = _fs.Superblock!;
        int fpg = (int)sb.FragsPerGroup;
        int bitmapOffset = cg.FreeBlocksOffset;

        if (_cachedDblkno < 0)
            _cachedDblkno = BinaryPrimitives.ReadInt32BigEndian(_fs.RawSuperblockData.AsSpan(0x14));

        int startFrom = _cachedDblkno;
        if (_cgSearchCursor.TryGetValue(cg.CgNumber, out int cursor) && cursor > startFrom)
            startFrom = cursor;

        // Round up to block boundary
        if ((startFrom % fragsPerBlock) != 0)
            startFrom = ((startFrom / fragsPerBlock) + 1) * fragsPerBlock;

        int runStartFrag = -1;
        int runBlocks = 0;

        for (int f = startFrom; f + fragsPerBlock <= fpg; f += fragsPerBlock)
        {
            // Check if entire block (fragsPerBlock fragments) is free
            bool blockFree = true;
            int baseByteIdx = bitmapOffset + (f / 8);
            
            // Fast path: if f is byte-aligned and fragsPerBlock=4, check half-byte
            if ((f % 8) == 0 && fragsPerBlock == 4)
            {
                if (baseByteIdx >= cg.RawData.Length) break;
                byte b = cg.RawData[baseByteIdx];
                blockFree = (b & 0x0F) == 0x0F; // bits 0-3 all set = all free
            }
            else if ((f % 8) == 4 && fragsPerBlock == 4)
            {
                if (baseByteIdx >= cg.RawData.Length) break;
                byte b = cg.RawData[baseByteIdx];
                blockFree = (b & 0xF0) == 0xF0; // bits 4-7 all set = all free
            }
            else
            {
                // General case
                for (int fi = 0; fi < fragsPerBlock; fi++)
                {
                    int bi = bitmapOffset + ((f + fi) / 8);
                    int bit = (f + fi) % 8;
                    if (bi >= cg.RawData.Length || (cg.RawData[bi] & (1 << bit)) == 0)
                    {
                        blockFree = false;
                        break;
                    }
                }
            }

            // Check protected fragments
            if (blockFree && _protectedFragments.Count > 0)
            {
                long globalBase = (long)cg.CgNumber * sb.FragsPerGroup + f;
                for (int fi = 0; fi < fragsPerBlock; fi++)
                {
                    if (_protectedFragments.Contains(globalBase + fi))
                    {
                        blockFree = false;
                        break;
                    }
                }
            }

            if (blockFree)
            {
                if (runStartFrag < 0) runStartFrag = f;
                runBlocks++;
                if (runBlocks >= maxBlocks)
                {
                    _cgSearchCursor[cg.CgNumber] = runStartFrag + runBlocks * fragsPerBlock;
                    return (runStartFrag, runBlocks);
                }
            }
            else
            {
                if (runBlocks > 0)
                {
                    // Return what we have so far
                    _cgSearchCursor[cg.CgNumber] = runStartFrag + runBlocks * fragsPerBlock;
                    return (runStartFrag, runBlocks);
                }
                runStartFrag = -1;
                runBlocks = 0;
            }
        }

        if (runBlocks > 0)
        {
            _cgSearchCursor[cg.CgNumber] = runStartFrag + runBlocks * fragsPerBlock;
            return (runStartFrag, runBlocks);
        }
        // Nothing after the cursor: wrap once so space freed earlier in the group is reused.
        if (startFrom > _cachedDblkno + fragsPerBlock && _cgSearchCursor.Remove(cg.CgNumber))
            return FindFreeBlockRun(cg, fragsPerBlock, maxBlocks);
        return (-1, 0);
    }

    public long FindFreeFragments(CylinderGroupInfo cg, int count)
    {
        // FFS rule: a run of fragments must lie inside one block, and a full-block
        // allocation must be block-aligned. Upstream's linear scan could return a run
        // that straddled two blocks (found by fsck in CI), which the kernel treats as
        // two half-owned blocks. Partial blocks are preferred for fragment runs so
        // whole blocks stay available for large files, as ffs_alloccg does.
        var sb = _fs.Superblock!;
        int fpb = (int)(sb.BlockSize / sb.FragmentSize);
        if (count <= 0 || count > fpb)
            throw new ArgumentOutOfRangeException(nameof(count), $"Fragment runs must be 1..{fpb} fragments.");

        int ndblk = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x14));
        if (ndblk <= 0 || ndblk > sb.FragsPerGroup) ndblk = (int)sb.FragsPerGroup;
        int nblocks = ndblk / fpb;
        if (nblocks <= 0) return -1;
        int bitmapOffset = cg.FreeBlocksOffset;
        if (bitmapOffset + (ndblk + 7) / 8 > cg.RawData.Length) return -1;
        long cgBase = (long)cg.CgNumber * sb.FragsPerGroup;

        int startBlock = 0;
        if (_cgSearchCursor.TryGetValue(cg.CgNumber, out int cursor) && cursor > 0)
            startBlock = (cursor / fpb) % nblocks;

        bool Usable(int localFrag)
            => (cg.RawData[bitmapOffset + (localFrag / 8)] & (1 << (localFrag % 8))) != 0
               && !_protectedFragments.Contains(cgBase + localFrag);

        int firstWholeBlock = -1;
        for (int k = 0; k < nblocks; k++)
        {
            int b = (startBlock + k) % nblocks;
            int blockStart = b * fpb;
            if (cg.RawData[bitmapOffset + (blockStart / 8)] == 0 && fpb <= 8 && (blockStart % 8) + fpb <= 8)
                continue; // whole bitmap byte used: nothing free in this block

            int free = 0;
            for (int j = 0; j < fpb; j++) if (Usable(blockStart + j)) free++;
            if (free == 0) continue;

            if (free == fpb)
            {
                if (count == fpb)
                {
                    _cgSearchCursor[cg.CgNumber] = blockStart + fpb;
                    return blockStart;
                }
                if (firstWholeBlock < 0) firstWholeBlock = blockStart;
                continue;
            }
            if (count == fpb) continue;

            // Partially used block: look for `count` free fragments in a row inside it.
            int run = 0;
            for (int j = 0; j < fpb; j++)
            {
                if (Usable(blockStart + j))
                {
                    if (++run >= count)
                    {
                        int found = blockStart + j - count + 1;
                        _cgSearchCursor[cg.CgNumber] = blockStart;
                        return found;
                    }
                }
                else run = 0;
            }
        }

        if (count < fpb && firstWholeBlock >= 0)
        {
            _cgSearchCursor[cg.CgNumber] = firstWholeBlock;
            return firstWholeBlock;
        }
        return -1;
    }

    /// <summary>
    /// Mark an inode as used in the CG bitmap.
    /// If the inode is beyond cg_initediblk, extends it by initializing inode blocks.
    /// </summary>
    public void MarkInodeUsed(CylinderGroupInfo cg, int inodeIdx)
    {
        var sb = _fs.Superblock!;
        
        // Check if we need to extend cg_initediblk
        // FreeBSD's lazy inode initialization: only inodes < initediblk are considered
        // initialized on disk. The PS3 kernel checks this!
        if (inodeIdx >= cg.InitedIblk)
        {
            int inopb = (int)(sb.BlockSize / sb.InodeSize); // inodes per block (typically 64)
            if (inopb <= 0) inopb = 64; // safety
            // Extend to cover this inode, rounded up to the next block boundary
            int newInitedIblk = ((inodeIdx / inopb) + 1) * inopb;
            // Cap at ipg
            if (newInitedIblk > (int)sb.InodesPerGroup)
                newInitedIblk = (int)sb.InodesPerGroup;
            
            if (!_dryRun)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int oldBlocks = cg.InitedIblk / inopb;
                int newBlocks = newInitedIblk / inopb;
                int blocksZeroed = newBlocks - oldBlocks;
                
                if (blocksZeroed > 0 && blocksZeroed < 1000) // sanity check
                {
                    byte[] zeros = new byte[(int)sb.BlockSize];
                    long cgStart = (long)cg.CgNumber * sb.FragsPerGroup;
                    
                    for (int blk = oldBlocks; blk < newBlocks; blk++)
                    {
                        long inodeBlockFrag = cgStart + sb.InodeBlockOffset + (long)blk * (sb.BlockSize / sb.FragmentSize);
                        long inodeBlockDiskOffset = _partitionOffset + inodeBlockFrag * sb.FragmentSize;
                        _disk.WriteBytes(inodeBlockDiskOffset, zeros);
                    }
                }
                sw.Stop();
                _log($"  Extended cg_initediblk: {cg.InitedIblk} -> {newInitedIblk} (inode {inodeIdx}, zeroed {blocksZeroed} blocks in {sw.ElapsedMilliseconds}ms)");
            }
            
            cg.InitedIblk = newInitedIblk;
            BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x78), cg.InitedIblk);
        }
        
        int byteIdx = cg.InodesUsedOffset + (inodeIdx / 8);
        int bitIdx = inodeIdx % 8;
        cg.RawData[byteIdx] |= (byte)(1 << bitIdx);
        cg.FreeInodes--;
        BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x20), cg.FreeInodes);
    }

    /// <summary>
    /// Mark fragments as used in the CG free block bitmap.
    /// </summary>
    public void MarkFragmentsUsed(CylinderGroupInfo cg, int startFrag, int count)
    {
        var sb = _fs.Superblock!;
        int fragsPerBlock = (int)(sb.BlockSize / sb.FragmentSize);
        int blockStart = (startFrag / fragsPerBlock) * fragsPerBlock;
        
        // Capture old free run sizes for this block BEFORE clearing bits
        int[] oldRuns = GetBlockFreeRuns(cg, blockStart, fragsPerBlock);
        
        for (int f = startFrag; f < startFrag + count; f++)
        {
            int byteIdx = cg.FreeBlocksOffset + (f / 8);
            int bitIdx = f % 8;
            cg.RawData[byteIdx] &= (byte)~(1 << bitIdx); // Clear bit = used
        }
        
        // Capture new free run sizes AFTER clearing bits
        int[] newRuns = GetBlockFreeRuns(cg, blockStart, fragsPerBlock);
        
        // Update frsum: remove old runs, add new runs
        UpdateFrsum(cg, oldRuns, newRuns, fragsPerBlock);
        
        // Update cs_nbfree / cs_nffree
        int oldFreeCount = 0, newFreeCount = 0;
        foreach (int r in oldRuns) oldFreeCount += r;
        foreach (int r in newRuns) newFreeCount += r;
        bool wasFullBlock = (oldFreeCount == fragsPerBlock);
        bool isFullBlock = (newFreeCount == fragsPerBlock);
        
        if (wasFullBlock && !isFullBlock)
        {
            int nbfree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x1C));
            BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x1C), nbfree - 1);
            cg.FreeBlocks = nbfree - 1;
            int nffree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x24));
            BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x24), nffree + newFreeCount);
        }
        else if (!wasFullBlock)
        {
            int nffree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x24));
            BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x24), nffree - (oldFreeCount - newFreeCount));
        }
        
        // Update cluster bitmap and cluster summary
        UpdateClusterBitmap(cg, startFrag / fragsPerBlock, fragsPerBlock);
    }

    /// <summary>
    /// Mark a single fragment as used. Properly handles cs_nbfree, cs_nffree, cg_frsum, and clusters.
    /// </summary>
    public void MarkFragmentUsed(CylinderGroupInfo cg, int fragIdx)
    {
        var sb = _fs.Superblock!;
        int fragsPerBlock = (int)(sb.BlockSize / sb.FragmentSize);
        int blockStart = (fragIdx / fragsPerBlock) * fragsPerBlock;
        
        // Capture old free run sizes BEFORE clearing bit
        int[] oldRuns = GetBlockFreeRuns(cg, blockStart, fragsPerBlock);
        
        // Clear the bit in the bitmap
        int byteIdx = cg.FreeBlocksOffset + (fragIdx / 8);
        int bitIdx = fragIdx % 8;
        cg.RawData[byteIdx] &= (byte)~(1 << bitIdx);
        
        // Capture new free run sizes AFTER clearing bit
        int[] newRuns = GetBlockFreeRuns(cg, blockStart, fragsPerBlock);
        
        // Update frsum
        UpdateFrsum(cg, oldRuns, newRuns, fragsPerBlock);
        
        // Update cs_nbfree / cs_nffree
        int oldFreeCount = 0, newFreeCount = 0;
        foreach (int r in oldRuns) oldFreeCount += r;
        foreach (int r in newRuns) newFreeCount += r;
        bool wasFullBlock = (oldFreeCount == fragsPerBlock);
        
        if (wasFullBlock)
        {
            int nbfree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x1C));
            BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x1C), nbfree - 1);
            cg.FreeBlocks = nbfree - 1;
            int nffree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x24));
            BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x24), nffree + newFreeCount);
        }
        else
        {
            int nffree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x24));
            BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x24), nffree - 1);
        }
        
        // Update cluster bitmap and cluster summary
        UpdateClusterBitmap(cg, fragIdx / fragsPerBlock, fragsPerBlock);
    }

    /// <summary>
    /// Get the free fragment run sizes within a single block.
    /// </summary>
    private int[] GetBlockFreeRuns(CylinderGroupInfo cg, int blockStart, int fragsPerBlock)
    {
        var runs = new System.Collections.Generic.List<int>();
        int runLen = 0;
        for (int f = blockStart; f < blockStart + fragsPerBlock; f++)
        {
            int bi = cg.FreeBlocksOffset + (f / 8);
            int bit = f % 8;
            bool isFree = (cg.RawData[bi] & (1 << bit)) != 0;
            if (isFree)
                runLen++;
            else
            {
                if (runLen > 0) runs.Add(runLen);
                runLen = 0;
            }
        }
        if (runLen > 0) runs.Add(runLen);
        return runs.ToArray();
    }

    /// <summary>
    /// Update cg_frsum array (at offset 0x34, fragsPerBlock x int32).
    /// Only tracks runs smaller than fragsPerBlock (full blocks are in cs_nbfree).
    /// </summary>
    private void UpdateFrsum(CylinderGroupInfo cg, int[] oldRuns, int[] newRuns, int fragsPerBlock)
    {
        foreach (int r in oldRuns)
        {
            if (r > 0 && r < fragsPerBlock)
            {
                int val = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x34 + r * 4));
                if (val > 0)
                    BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x34 + r * 4), val - 1);
            }
        }
        foreach (int r in newRuns)
        {
            if (r > 0 && r < fragsPerBlock)
            {
                int val = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x34 + r * 4));
                BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x34 + r * 4), val + 1);
            }
        }
    }

    /// <summary>
    /// Update the cluster bitmap (1 bit per block) and cluster summary after a block allocation.
    /// The cluster bitmap is at cg_clusteroff, cluster summary at cg_clustersumoff.
    /// </summary>
    private void UpdateClusterBitmap(CylinderGroupInfo cg, int blockIdx, int fragsPerBlock)
    {
        int clusteroff = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x6C));
        int clustersumoff = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x68));
        int nclusterblks = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x70));
        
        if (clusteroff == 0 || clustersumoff == 0 || nclusterblks == 0) return;
        if (blockIdx >= nclusterblks) return;
        
        // Check if ALL fragments in this block are free (determines cluster bit)
        int fragStart = blockIdx * fragsPerBlock;
        bool allFree = true;
        for (int f = fragStart; f < fragStart + fragsPerBlock; f++)
        {
            int bi = cg.FreeBlocksOffset + (f / 8);
            int bit = f % 8;
            if ((cg.RawData[bi] & (1 << bit)) == 0)
            {
                allFree = false;
                break;
            }
        }
        
        // Read old cluster bit
        int cByteIdx = clusteroff + (blockIdx / 8);
        int cBitIdx = blockIdx % 8;
        bool wasSet = (cg.RawData[cByteIdx] & (1 << cBitIdx)) != 0;
        
        // Determine if we need to change the bit
        bool shouldBeSet = allFree;
        if (wasSet == shouldBeSet) return; // No change needed
        
        // Find old cluster run containing this block
        int oldRunStart = blockIdx, oldRunEnd = blockIdx;
        if (wasSet)
        {
            // Expanding search for the old run this block was part of
            while (oldRunStart > 0)
            {
                int prevBi = clusteroff + ((oldRunStart - 1) / 8);
                int prevBit = (oldRunStart - 1) % 8;
                if ((cg.RawData[prevBi] & (1 << prevBit)) == 0) break;
                oldRunStart--;
            }
            while (oldRunEnd < nclusterblks - 1)
            {
                int nextBi = clusteroff + ((oldRunEnd + 1) / 8);
                int nextBit = (oldRunEnd + 1) % 8;
                if ((cg.RawData[nextBi] & (1 << nextBit)) == 0) break;
                oldRunEnd++;
            }
        }
        int oldRunLen = wasSet ? (oldRunEnd - oldRunStart + 1) : 0;
        
        // Update the cluster bit
        if (shouldBeSet)
            cg.RawData[cByteIdx] |= (byte)(1 << cBitIdx);
        else
            cg.RawData[cByteIdx] &= (byte)~(1 << cBitIdx);
        
        // Find new cluster runs after the change
        // The old run splits into 0, 1, or 2 new runs
        int maxSumIdx = (clustersumoff > 0) ? 
            (clusteroff - clustersumoff) / 4 - 1 : fragsPerBlock;
        
        if (wasSet && !shouldBeSet)
        {
            // Block went from free to used — old run is split
            int leftLen = blockIdx - oldRunStart;
            int rightLen = oldRunEnd - blockIdx;
            
            // Decrement old run size in cluster summary
            int oldIdx = Math.Min(oldRunLen, maxSumIdx);
            if (oldIdx > 0 && clustersumoff + oldIdx * 4 < clusteroff)
            {
                int val = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(clustersumoff + oldIdx * 4));
                if (val > 0)
                    BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(clustersumoff + oldIdx * 4), val - 1);
            }
            
            // Increment new run sizes
            if (leftLen > 0)
            {
                int lIdx = Math.Min(leftLen, maxSumIdx);
                if (lIdx > 0 && clustersumoff + lIdx * 4 < clusteroff)
                {
                    int val = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(clustersumoff + lIdx * 4));
                    BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(clustersumoff + lIdx * 4), val + 1);
                }
            }
            if (rightLen > 0)
            {
                int rIdx = Math.Min(rightLen, maxSumIdx);
                if (rIdx > 0 && clustersumoff + rIdx * 4 < clusteroff)
                {
                    int val = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(clustersumoff + rIdx * 4));
                    BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(clustersumoff + rIdx * 4), val + 1);
                }
            }
        }
    }

    /// <summary>
    /// Write the CG header back to disk (or defer if DeferCgWrites is true).
    /// </summary>
    public void WriteCylinderGroup(CylinderGroupInfo cg)
    {
        // Update timestamps in memory regardless of defer mode
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x08), (int)now);
        if (cg.RawData.Length > 0x98)
            BinaryPrimitives.WriteInt64BigEndian(cg.RawData.AsSpan(0x90), now);
        
        if (DeferCgWrites)
        {
            _dirtyCgs.Add(cg.CgNumber);
        }
        else
        {
            RecomputeCgSummary(cg);
            QueueWrite(cg.DiskOffset, cg.RawData, $"CG {cg.CgNumber} header ({cg.RawData.Length} bytes)");
            _dirtyCgs.Remove(cg.CgNumber);
        }
    }

    /// <summary>
    /// Flush all deferred CG writes to disk at once.
    /// </summary>
    public void FlushDirtyCgs()
    {
        if (_dirtyCgs.Count == 0) return;
        foreach (int cgNum in _dirtyCgs)
        {
            if (_cgCache.TryGetValue(cgNum, out var cg))
            {
                RecomputeCgSummary(cg);
                QueueWrite(cg.DiskOffset, cg.RawData, $"CG {cg.CgNumber} header (deferred flush)");
            }
        }
        _log($"  Flushed {_dirtyCgs.Count} deferred CG writes");
        _dirtyCgs.Clear();
    }

    /// <summary>
    /// Write an inode to disk.
    /// </summary>
    public void WriteInode(long inodeNumber, byte[] inodeData)
    {
        var sb = _fs.Superblock!;
        long group = inodeNumber / sb.InodesPerGroup;
        long indexInGroup = inodeNumber % sb.InodesPerGroup;
        long cgOffset = _partitionOffset + (group * sb.FragsPerGroup * sb.FragmentSize);
        long inodeTableOffset = cgOffset + (sb.InodeBlockOffset * sb.FragmentSize);
        long inodeOffset = inodeTableOffset + (indexInGroup * (int)sb.InodeSize);

        QueueWrite(inodeOffset, inodeData, $"Inode {inodeNumber} (CG {group}, idx {indexInGroup})");
    }

    /// <summary>
    /// Build a UFS2 dinode2 structure for a new directory.
    /// </summary>
    public byte[] BuildDirectoryInode(long dataBlockFrag, int nlink, ushort mode = 0x41FF)
    {
        byte[] inode = new byte[256];
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sb = _fs.Superblock!;
        uint gen = (uint)Random.Shared.Next();

        // di_mode: directory permissions (0x41FF=777 for game dirs, 0x41ED=755 for system dirs)
        BinaryPrimitives.WriteUInt16BigEndian(inode.AsSpan(0x00), mode);
        // di_nlink
        BinaryPrimitives.WriteInt16BigEndian(inode.AsSpan(0x02), (short)nlink);
        // di_blksize = 0 (PS3 installer leaves this at 0 for all inodes)
        // (do not write — already zero from array init)
        // di_size = 512
        BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x10), 512);
        // di_blocks: 1 fragment (FragmentSize / 512 = 8 sectors), matching PS3 native
        BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x18), (sb.FragmentSize / 512));
        // di_atime
        BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x20), now);
        // di_mtime
        BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x28), now);
        // di_ctime
        BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x30), now);
        // di_birthtime
        BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x38), now);
        // 0x40-0x4F: nanosecond fields (leave as 0)
        // di_gen at 0x50
        BinaryPrimitives.WriteUInt32BigEndian(inode.AsSpan(0x50), gen);
        // di_db[0] = data block fragment address
        BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x70), dataBlockFrag);

        return inode;
    }

    /// <summary>
    /// Build a UFS2 dinode2 structure for a new regular file.
    /// </summary>
    public byte[] BuildFileInode(long fileSize, long[] directBlocks, long indirectBlock = 0, 
                                  long doubleIndirect = 0, long tripleIndirect = 0)
    {
        byte[] inode = new byte[256];
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sb = _fs.Superblock!;
        uint gen = (uint)Random.Shared.Next();

        // di_mode: regular file + rwxrwxrwx = 0x81FF (matches PS3 native)
        BinaryPrimitives.WriteUInt16BigEndian(inode.AsSpan(0x00), 0x81FF);
        // di_nlink = 1
        BinaryPrimitives.WriteInt16BigEndian(inode.AsSpan(0x02), 1);
        // di_blksize = 0 for files (PS3 leaves this at 0)
        // di_size
        BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x10), fileSize);
        // di_blocks: count allocated space in 512-byte units.
        // PS3 native rule (verified from hex dumps):
        //   - Files with indirect blocks: ALL data counted at BLOCK granularity
        //   - Files without indirect (direct-only): data counted at FRAGMENT granularity  
        bool hasIndirect = (indirectBlock != 0 || doubleIndirect != 0 || tripleIndirect != 0);
        long dataSectors;
        if (hasIndirect)
        {
            // Block-granularity: ceil(size / blockSize) * (blockSize / 512)
            long dataBlocks = (fileSize + sb.BlockSize - 1) / sb.BlockSize;
            dataSectors = dataBlocks * (sb.BlockSize / 512);
        }
        else
        {
            // Fragment-granularity: ceil(size / fragSize) * (fragSize / 512)
            long dataFragments = (fileSize + sb.FragmentSize - 1) / sb.FragmentSize;
            dataSectors = dataFragments * (sb.FragmentSize / 512);
        }
        // Indirect/metadata blocks are always full blocks
        long indirectBlockCount = (indirectBlock != 0 ? 1 : 0) + (doubleIndirect != 0 ? 1 : 0);
        if (doubleIndirect != 0)
        {
            long ptrsPerBlock = sb.BlockSize / 8;
            long fsBlocks = (fileSize + sb.BlockSize - 1) / sb.BlockSize;
            long dataInDirect = 12;
            long dataInSingle = ptrsPerBlock;
            long dataInDouble = fsBlocks - dataInDirect - dataInSingle;
            if (dataInDouble > 0)
                indirectBlockCount += (dataInDouble + ptrsPerBlock - 1) / ptrsPerBlock; // L1 blocks
        }
        long totalAllocated = dataSectors + indirectBlockCount * (sb.BlockSize / 512);
        BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x18), totalAllocated);
        // timestamps
        BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x20), now);
        BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x28), now);
        BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x30), now);
        BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x38), now);
        // di_gen at 0x50
        BinaryPrimitives.WriteUInt32BigEndian(inode.AsSpan(0x50), gen);
        // di_db[0..11] = direct block pointers
        for (int i = 0; i < Math.Min(12, directBlocks.Length); i++)
            BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0x70 + i * 8), directBlocks[i]);
        // di_ib[0] = single indirect
        if (indirectBlock != 0)
            BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0xD0), indirectBlock);
        // di_ib[1] = double indirect
        if (doubleIndirect != 0)
            BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0xD8), doubleIndirect);
        // di_ib[2] = triple indirect
        if (tripleIndirect != 0)
            BinaryPrimitives.WriteInt64BigEndian(inode.AsSpan(0xE0), tripleIndirect);

        return inode;
    }

    /// <summary>
    /// Build a minimal directory block with "." and ".." entries.
    /// </summary>
    public byte[] BuildEmptyDirectoryBlock(long selfInode, long parentInode)
    {
        var sb = _fs.Superblock!;
        byte[] block = new byte[(int)sb.FragmentSize]; // 1 fragment, matching di_blocks=8

        int offset = 0;
        // "." entry
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(offset), (uint)selfInode);
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(offset + 4), 12); // reclen
        block[offset + 6] = 4; // DT_DIR
        block[offset + 7] = 1; // namelen
        block[offset + 8] = (byte)'.';
        offset += 12;

        // ".." entry — takes remaining space in the DIRECTORY (512 bytes), not the full block
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(offset), (uint)parentInode);
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(offset + 4), (ushort)(512 - 12)); // reclen = 500
        block[offset + 6] = 4; // DT_DIR
        block[offset + 7] = 2; // namelen
        block[offset + 8] = (byte)'.';
        block[offset + 9] = (byte)'.';

        // Initialize remaining DIRBLKSIZ sections with free-space entries.
        // Native PS3 writes ino=0, reclen=512 at the start of each unused section.
        // Without this, scanners that read beyond di_size hit reclen=0 and loop forever.
        for (int sec = 512; sec < block.Length; sec += 512)
        {
            // ino = 0 (already zero from array init)
            BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(sec + 4), 512); // reclen = full section
            // type = 0, namelen = 0 (already zero)
        }

        return block;
    }

    /// <summary>
    /// Add a directory entry to an existing directory's data block.
    /// Finds the last entry and splits its reclen to make room.
    /// </summary>
    public byte[] AddEntryToDirectoryBlock(byte[] dirBlock, long inode, string name, byte dirEntryType)
    {
        byte[] result = (byte[])dirBlock.Clone();
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        int newEntrySize = ((8 + nameBytes.Length + 1 + 3) / 4) * 4; // +1 for null terminator, 4-byte aligned
        const int DIRBLKSIZ = 512;
        
        // Scan each DIRBLKSIZ section within the block
        int numSections = result.Length / DIRBLKSIZ;
        for (int section = 0; section < numSections; section++)
        {
            int sectionStart = section * DIRBLKSIZ;
            int sectionEnd = sectionStart + DIRBLKSIZ;
            int offset = sectionStart;
            
            // Walk entries within this section
            while (offset < sectionEnd)
            {
                uint entInode = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(offset));
                ushort recLen = BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(offset + 4));
                if (recLen == 0) break; // Shouldn't happen in valid dir
                
                // Calculate this entry's actual size
                int actualSize;
                if (entInode == 0)
                {
                    // Deleted/empty entry — entire reclen is free
                    actualSize = 0;
                }
                else
                {
                    byte entNameLen = result[offset + 7];
                    actualSize = ((8 + entNameLen + 1 + 3) / 4) * 4;
                }
                
                int freeSpace = recLen - actualSize;
                if (freeSpace >= newEntrySize)
                {
                    // Found space — split this entry
                    if (actualSize > 0)
                    {
                        // Shrink existing entry to its actual size
                        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset + 4), (ushort)actualSize);
                    }
                    
                    // Write new entry after the existing one
                    int newOffset = offset + actualSize;
                    int newRecLen = recLen - actualSize;
                    
                    // Bounds check
                    if (newOffset + 8 + nameBytes.Length > result.Length)
                        throw new IOException($"Directory block overflow for '{name}'");
                    
                    // CRITICAL: Zero the entire new entry region to prevent stale data
                    // from being read as part of the name. Pre-existing directory blocks
                    // may contain old entry data or deleted entry names in this space.
                    Array.Clear(result, newOffset, newRecLen);
                    
                    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(newOffset), (uint)inode);
                    BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(newOffset + 4), (ushort)newRecLen);
                    result[newOffset + 6] = dirEntryType;
                    result[newOffset + 7] = (byte)nameBytes.Length;
                    Array.Copy(nameBytes, 0, result, newOffset + 8, nameBytes.Length);
                    
                    return result;
                }
                
                offset += recLen;
            }
        }

        throw new IOException($"Not enough space in directory block for '{name}' (need {newEntrySize})");
    }

    /// <summary>
    /// Write a data block to disk at the given fragment address.
    /// Uses fast path — no logging or pending list for bulk data.
    /// </summary>
    public void WriteDataBlock(long fragmentAddress, byte[] data)
    {
        long offset = _partitionOffset + (fragmentAddress * _fs.Superblock!.FragmentSize);
        if (_dryRun)
            return; // dry run just skips data blocks silently
        _disk.WriteBytes(offset, data);
    }

    /// <summary>
    /// High-level: Create a new empty directory inside a parent directory.
    /// </summary>
    /// <summary>
    /// Check if a directory already contains an entry with the given name.
    /// </summary>
    public bool DirectoryContainsEntry(long dirInodeNumber, string name)
    {
        // Read errors propagate: treating an unreadable directory as "no such name"
        // (upstream behaviour) could create a duplicate entry.
        var inode = _fs.ReadInode(dirInodeNumber);
        var entries = _fs.ReadDirectory(inode);
        foreach (var entry in entries)
        {
            if (entry.Name == name)
                return true;
        }
        return false;
    }

    public long CreateDirectory(long parentInodeNumber, string name)
    {
        var sb = _fs.Superblock!;
        ValidateEntryName(name);
        _log($"[WRITE] Creating directory '{name}' in inode {parentInodeNumber}");

        // Check for duplicate entry
        if (DirectoryContainsEntry(parentInodeNumber, name))
            throw new IOException($"Directory already contains an entry named '{name}'");

        // Protect directory fragments from PS3 bitmap inconsistencies
        _protectedFragments.Clear();
        CollectProtectedFragments(2);
        if (parentInodeNumber != 2)
            CollectProtectedFragments(parentInodeNumber);

        // 1. Choose a CG for the new directory
        // For root-level dirs: use ffs_dirpref algorithm (spread across disk)
        // For subdirectories: use parent's CG (locality)
        int parentCg = (int)(parentInodeNumber / sb.InodesPerGroup);
        int targetCg = parentCg;
        
        if (parentInodeNumber == 2) // root directory
        {
            // ffs_dirpref: pick a CG with fewest dirs and above-average free space
            // This matches FreeBSD/PS3 behavior — spreads top-level dirs across disk
            int cblkno = BinaryPrimitives.ReadInt32BigEndian(_fs.RawSuperblockData.AsSpan(0x0C));
            
            long totalDirs = 0, totalFreeBlocks = 0, totalFreeInodes = 0;
            int validCgs = 0;
            for (int i = 0; i < sb.CylinderGroups; i++)
            {
                long cgOff = _partitionOffset + (long)i * sb.FragsPerGroup * sb.FragmentSize;
                long cgHdrOff = cgOff + (long)cblkno * sb.FragmentSize;
                byte[] hdr = _disk.ReadBytes(cgHdrOff, 40);
                if (BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(0x04)) != 0x00090255) continue;
                totalDirs += BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(0x18));
                totalFreeBlocks += BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(0x1C));
                totalFreeInodes += BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(0x20));
                validCgs++;
            }
            
            if (validCgs > 0)
            {
                int avgFreeBlocks = (int)(totalFreeBlocks / validCgs);
                int avgFreeInodes = (int)(totalFreeInodes / validCgs);
                
                // Reserve margin: avoid early CGs (system data) and late CGs (wrap-around risk)
                int margin = Math.Min(256, sb.CylinderGroups / 4);
                int rangeStart = Math.Max(1, margin / 2);
                int rangeEnd = sb.CylinderGroups - margin;
                if (rangeEnd <= rangeStart) rangeEnd = sb.CylinderGroups;
                
                int startCg = rangeStart + Random.Shared.Next(rangeEnd - rangeStart);
                int bestCg = startCg;
                int bestDirs = int.MaxValue;
                
                for (int i = 0; i < sb.CylinderGroups; i++)
                {
                    int cgi = (startCg + i) % sb.CylinderGroups;
                    long cgOff = _partitionOffset + (long)cgi * sb.FragsPerGroup * sb.FragmentSize;
                    long cgHdrOff = cgOff + (long)cblkno * sb.FragmentSize;
                    byte[] hdr = _disk.ReadBytes(cgHdrOff, 40);
                    if (BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(0x04)) != 0x00090255) continue;
                    
                    int cgDirs = BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(0x18));
                    int cgFreeBlocks = BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(0x1C));
                    int cgFreeInodes = BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(0x20));
                    
                    if (cgDirs < bestDirs && cgFreeInodes >= avgFreeInodes && cgFreeBlocks >= avgFreeBlocks)
                    {
                        bestCg = cgi;
                        bestDirs = cgDirs;
                    }
                }
                
                targetCg = bestCg;
                _log($"  ffs_dirpref: selected CG {targetCg} for root-level directory");
            }
        }
        
        var cg = ReadCylinderGroup(targetCg);

        if (cg.Magic != 0x00090255)
            throw new IOException($"CG {targetCg} has invalid magic: 0x{cg.Magic:X8}");

        _log($"  CG {targetCg}: {cg.FreeInodes} free inodes, {cg.FreeBlocks} free blocks");

        // 2. Allocate an inode (try target CG first, then scan others)
        int inodeCg = targetCg;
        var inodeCgInfo = cg;
        int freeIdx = FindFreeInode(inodeCgInfo);
        if (freeIdx < 0)
        {
            for (int i = 1; i < sb.CylinderGroups; i++)
            {
                int tryCg = (targetCg + i) % sb.CylinderGroups;
                inodeCgInfo = ReadCylinderGroup(tryCg);
                freeIdx = FindFreeInode(inodeCgInfo);
                if (freeIdx >= 0) { inodeCg = tryCg; break; }
            }
            if (freeIdx < 0) throw new IOException("No free inodes on disk.");
        }
        long newInodeNumber = (long)inodeCg * sb.InodesPerGroup + freeIdx;
        _log($"  Allocating inode {newInodeNumber} (CG {inodeCg}, idx {freeIdx})");

        // 3. Allocate a single data FRAGMENT for the directory
        // CRITICAL: Must be at a BLOCK-ALIGNED fragment address (frag%fragsPerBlock==0).
        // FreeBSD/PS3 natively allocates directory block 0 at block boundaries
        // so the kernel can safely read a full block from di_db[0] when the
        // directory grows to span multiple blocks.
        int fragsPerBlock = (int)(sb.BlockSize / sb.FragmentSize);
        int blockCg = inodeCg;
        var blockCgInfo = inodeCgInfo;
        long freeFrag = FindFreeBlockAlignedFragment(blockCgInfo, fragsPerBlock);
        if (freeFrag < 0)
        {
            for (int i = 1; i < sb.CylinderGroups; i++)
            {
                int tryCg = (inodeCg + i) % sb.CylinderGroups;
                blockCgInfo = ReadCylinderGroup(tryCg);
                freeFrag = FindFreeBlockAlignedFragment(blockCgInfo, fragsPerBlock);
                if (freeFrag >= 0) { blockCg = tryCg; _log($"  CG {inodeCg} full, using CG {tryCg} for data block"); break; }
            }
            if (freeFrag < 0) throw new IOException("No free blocks on disk.");
        }
        long absFragAddr = (long)blockCg * sb.FragsPerGroup + freeFrag;
        _log($"  Allocating fragment at frag 0x{absFragAddr:X} (CG {blockCg}, frag {freeFrag})");

        // 4. Claim the inode and the data fragment in the CG bitmaps FIRST.
        // MarkInodeUsed may zero whole inode blocks when it extends cg_initediblk, so it
        // must run before the new inode is written (upstream wrote the inode first and
        // could wipe it right afterwards).
        MarkInodeUsed(inodeCgInfo, freeIdx);
        int ndirCount = BinaryPrimitives.ReadInt32BigEndian(inodeCgInfo.RawData.AsSpan(0x18));
        BinaryPrimitives.WriteInt32BigEndian(inodeCgInfo.RawData.AsSpan(0x18), ndirCount + 1);
        MarkFragmentUsed(blockCgInfo, (int)freeFrag);
        WriteCylinderGroup(inodeCgInfo);
        if (blockCg != inodeCg)
            WriteCylinderGroup(blockCgInfo);
        _log($"  Updated CG bitmaps");

        // 5. Build the directory data block with "." and ".."
        // Directory is 512 bytes but we write a full fragment (4096)
        byte[] dirBlock = BuildEmptyDirectoryBlock(newInodeNumber, parentInodeNumber);
        if (VerboseLog)
        {
            _log($"  DIR BLOCK we write ({dirBlock.Length} bytes):");
            for (int r = 0; r < Math.Min(64, dirBlock.Length); r += 32)
                _log($"    0x{r:X2}: {BitConverter.ToString(dirBlock, r, Math.Min(32, dirBlock.Length - r))}");
        }
        WriteDataBlock(absFragAddr, dirBlock);

        // Bitmaps and the directory block must be stable before the inode points at them.
        if (SafeWriteOrdering) DurabilityBarrier();

        // 6. Build and write the new inode
        // PS3 native uses 0x41FF for ALL game-related directories including game/ itself
        ushort dirMode = (ushort)0x41FF;
        byte[] inodeData = BuildDirectoryInode(absFragAddr, 2, dirMode); // nlink=2 (self + ".")
        if (VerboseLog)
        {
            _log($"  INODE we write:");
            for (int r = 0; r < Math.Min(256, inodeData.Length); r += 32)
                _log($"    0x{r:X2}: {BitConverter.ToString(inodeData, r, Math.Min(32, inodeData.Length - r))}");
        }
        WriteInode(newInodeNumber, inodeData);

        // 7. Add entry to parent directory (try existing blocks, expand if needed)
        _log($"  Adding '{name}' to parent directory (inode {parentInodeNumber})");
        var parentInode = _fs.ReadInode(parentInodeNumber);
        bool expanded = false;
        byte[]? expandedRawParent = null;
        AddEntryToDirectory(parentInodeNumber, parentInode, newInodeNumber, name, 4, inodeCg, inodeCgInfo, blockCg, blockCgInfo, fragsPerBlock, out expanded, out expandedRawParent);

        // 8. Update parent inode nlink (add 1 for the new ".." reference)
        // If directory was expanded, AddEntryToDirectory already wrote an updated inode.
        // We need to read THAT version (or use the returned bytes) and add nlink to it.
        byte[] rawParent;
        if (expanded && expandedRawParent != null)
        {
            rawParent = expandedRawParent;
        }
        else
        {
            rawParent = _disk.ReadBytes(
                _partitionOffset + (parentInodeNumber / sb.InodesPerGroup) * sb.FragsPerGroup * sb.FragmentSize
                + sb.InodeBlockOffset * sb.FragmentSize + (parentInodeNumber % sb.InodesPerGroup) * sb.InodeSize,
                (int)sb.InodeSize);
        }
        short nlink = BinaryPrimitives.ReadInt16BigEndian(rawParent.AsSpan(0x02));
        BinaryPrimitives.WriteInt16BigEndian(rawParent.AsSpan(0x02), (short)(nlink + 1));
        if (VerboseLog)
        {
            _log($"  PARENT INODE we write (nlink {nlink} -> {nlink+1}):");
            for (int r = 0; r < Math.Min(128, rawParent.Length); r += 32)
                _log($"    0x{r:X2}: {BitConverter.ToString(rawParent, r, Math.Min(32, rawParent.Length - r))}");
        }
        WriteInode(parentInodeNumber, rawParent);

        _log($"  Directory '{name}' created as inode {newInodeNumber}");
        return newInodeNumber;
    }

    /// <summary>
    /// High-level: Write a file from a host stream into the filesystem.
    /// </summary>
    /// <summary>Bookkeeping for one WriteFile call so a failure can be undone.</summary>
    private sealed class WriteFileState
    {
        public readonly List<(long AbsFrag, int Count)> Allocated = new();
        public long InodeNumber = -1;
        public bool InodeMarked;
        public bool InodeWritten;
        public bool Linked;
    }

    public long WriteFile(long parentInodeNumber, string name, Stream sourceData, long fileSize)
    {
        ValidateEntryName(name);
        if (fileSize < 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
        // Never inherit a reservation left behind by an earlier, failed call.
        ReleaseBatchReservation();
        var state = new WriteFileState();
        try
        {
            long ino = WriteFileCore(parentInodeNumber, name, sourceData, fileSize, state);
            ReleaseBatchReservation();
            return ino;
        }
        catch (Exception ex) when (!state.Linked)
        {
            _log($"  ERROR writing '{name}': {ex.Message} — rolling back its allocations");
            RollbackWriteFile(state);
            throw;
        }
    }

    private void RollbackWriteFile(WriteFileState state)
    {
        var sb = _fs.Superblock!;
        try { FlushDataBatch(); } catch (Exception flushEx) { _log($"  (data flush during rollback failed: {flushEx.Message})"); }
        ReleaseBatchReservation();
        foreach (var (absFrag, count) in state.Allocated)
            FreeFragmentRun(absFrag, count);
        if (state.InodeMarked && state.InodeNumber >= 0)
        {
            var icg = ReadCylinderGroup((int)(state.InodeNumber / sb.InodesPerGroup));
            MarkInodeFree(icg, (int)(state.InodeNumber % sb.InodesPerGroup));
            WriteCylinderGroup(icg);
        }
        if (state.InodeWritten && state.InodeNumber >= 0)
        {
            WriteInode(state.InodeNumber, new byte[(int)sb.InodeSize]);
            DurabilityBarrier();
        }
        _log($"  Rolled back {state.Allocated.Count} allocation(s)");
    }

    private long WriteFileCore(long parentInodeNumber, string name, Stream sourceData, long fileSize, WriteFileState state)
    {
        var sb = _fs.Superblock!;
        _log($"[WRITE] Writing file '{name}' ({fileSize} bytes) into inode {parentInodeNumber}");

        // Check for duplicate entry
        if (DirectoryContainsEntry(parentInodeNumber, name))
            throw new IOException($"Directory already contains a file named '{name}'");

        int parentCg = (int)(parentInodeNumber / sb.InodesPerGroup);

        // Protect directory fragments from PS3 bitmap inconsistencies
        _protectedFragments.Clear();
        CollectProtectedFragments(2);
        if (parentInodeNumber != 2)
            CollectProtectedFragments(parentInodeNumber);

        // 1. Allocate inode — parent's CG first, then any other CG (upstream issue #7)
        int freeIdx = FindFreeInodeAnyCg(parentCg, out int inodeCg, out var cg);
        long newInodeNumber = (long)inodeCg * sb.InodesPerGroup + freeIdx;
        state.InodeNumber = newInodeNumber;

        // 2. Calculate blocks needed
        int fragsPerBlock = (int)(sb.BlockSize / sb.FragmentSize);
        long blocksNeeded = (fileSize + sb.BlockSize - 1) / sb.BlockSize;
        int blockSize = (int)sb.BlockSize;
        // Direct-only files end in a fragment "tail"; only count whole blocks when
        // reserving runs, otherwise the block reserved for the tail is never used.
        bool lastIsTail = blocksNeeded > 0 && blocksNeeded <= 12 && (fileSize % blockSize) != 0;
        long fullBlocksNeeded = blocksNeeded - (lastIsTail ? 1 : 0);

        // 3. Allocate and write data blocks
        var directBlocks = new long[12];
        long indirectBlock = 0;
        long doubleIndirectBlock = 0;
        _currentL1Block = 0;
        long bytesRemaining = fileSize;

        // In-memory indirect block buffers (avoid re-reading from disk)
        byte[]? indirectBlockBuf = null;
        byte[]? doubleIndirectBuf = null;
        byte[]? currentL1Buf = null;

        var deferredMetaBlocks = new List<(long Addr, byte[] Data)>();

        // Write batching: use persistent batch state for cross-file batching
        EnsureBatchBuffers(blockSize);
        
        // For files with indirect blocks, flush any pending batch first
        // (indirect metadata blocks will break sequential continuity)
        bool needsIndirect = blocksNeeded > 12;
        if (needsIndirect)
            FlushDataBatch();

        int currentCg = inodeCg;
        var currentCgInfo = cg;

        // Timing instrumentation
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var swAlloc = new System.Diagnostics.Stopwatch();
        var swRead = new System.Diagnostics.Stopwatch();
        var swFlush = new System.Diagnostics.Stopwatch();
        var swWait = new System.Diagnostics.Stopwatch();
        var swMeta = new System.Diagnostics.Stopwatch(); // inode + CG + indirect block writes
        var swDir = new System.Diagnostics.Stopwatch();  // directory entry addition
        int flushCount = 0;
        long totalFlushedBytes = 0;

        void FlushBatch(bool wait = true)
        {
            if (_batchBlockCount <= 0) return;
            swFlush.Start();
            int flushedBlocks = _batchBlockCount;
            InternalFlushBatch(blockSize);
            if (wait) _pendingWrite?.Wait();
            totalFlushedBytes += flushedBlocks * blockSize;
            flushCount++;
            swFlush.Stop();
        }

        for (long b = 0; b < blocksNeeded; b++)
        {
            // Determine how many fragments this block needs.
            // UFS2 rule (verified from PS3 native data):
            //   - Files needing indirect blocks: ALL data blocks are full blocks (no tail)
            //   - Files fitting in 12 direct blocks: last block uses fragment tail allocation
            bool isLastBlock = (b == blocksNeeded - 1);
            bool usesTail = isLastBlock && blocksNeeded <= 12; // direct-only files get tail
            int fragsThisBlock = fragsPerBlock;
            if (usesTail)
            {
                long remainingBytes = fileSize - b * blockSize;
                fragsThisBlock = (int)((remainingBytes + sb.FragmentSize - 1) / sb.FragmentSize);
                if (fragsThisBlock > fragsPerBlock) fragsThisBlock = fragsPerBlock;
                if (fragsThisBlock < 1) fragsThisBlock = 1;
            }

            swAlloc.Start();
            long freeFrag;
            
            // Use batch allocation for full blocks — avoids per-block bitmap scanning
            if (fragsThisBlock == fragsPerBlock && _batchAllocRemaining > 0 && _batchAllocCg == currentCg)
            {
                // Use next block from pre-allocated run (already marked used in bitmap)
                freeFrag = _batchAllocNextFrag;
                _batchAllocNextFrag += fragsPerBlock;
                _batchAllocRemaining--;
            }
            else if (fragsThisBlock == fragsPerBlock)
            {
                // Batch allocate: find a contiguous run of blocks
                _batchAllocRemaining = 0; // invalidate stale batch
                int wantBlocks = (int)Math.Max(1, Math.Min(fullBlocksNeeded - b, 4096)); // up to 4096 blocks at once
                var (runStart, runCount) = FindFreeBlockRun(currentCgInfo, fragsPerBlock, wantBlocks);
                if (runStart < 0 || runCount == 0)
                {
                    swAlloc.Stop();
                    FlushBatch();
                    swAlloc.Start();
                    WriteCylinderGroup(currentCgInfo);
                    bool foundCg = false;
                    for (int ci = 1; ci < sb.CylinderGroups; ci++)
                    {
                        int tryCg = (currentCg + ci) % sb.CylinderGroups;
                        currentCgInfo = ReadCylinderGroup(tryCg);
                        (runStart, runCount) = FindFreeBlockRun(currentCgInfo, fragsPerBlock, wantBlocks);
                        if (runStart >= 0 && runCount > 0) { currentCg = tryCg; foundCg = true; break; }
                    }
                    if (!foundCg) throw new IOException("No free blocks available on disk.");
                }
                
                freeFrag = runStart;
                _batchAllocNextFrag = runStart + fragsPerBlock;
                _batchAllocRemaining = runCount - 1;
                _batchAllocCg = currentCg;
                // BUG FIX #23: Mark ALL blocks in the pre-allocated run as used in the
                // bitmap immediately. Previously only the first block was marked, leaving
                // the rest appearing "free" in the bitmap. When metadata blocks (L1 indirect,
                // double indirect) were allocated via FindFreeFragments during the same file
                // write, they would collide with unconsumed batch blocks — both pointing to
                // the same disk location. Data blocks would then overwrite the metadata,
                // corrupting files >33MB that use double indirect blocks.
                for (int rb = 0; rb < runCount; rb++)
                    MarkFragmentsUsed(currentCgInfo, (int)(runStart + rb * fragsPerBlock), fragsPerBlock);
            }
            else
            {
                // Tail allocation — use single-block FindFreeFragments
                freeFrag = FindFreeFragments(currentCgInfo, fragsThisBlock);
                if (freeFrag < 0)
                {
                    swAlloc.Stop();
                    FlushBatch();
                    swAlloc.Start();
                    WriteCylinderGroup(currentCgInfo);
                    bool foundCg = false;
                    for (int ci = 1; ci < sb.CylinderGroups; ci++)
                    {
                        int tryCg = (currentCg + ci) % sb.CylinderGroups;
                        currentCgInfo = ReadCylinderGroup(tryCg);
                        freeFrag = FindFreeFragments(currentCgInfo, fragsThisBlock);
                        if (freeFrag >= 0) { currentCg = tryCg; foundCg = true; break; }
                    }
                    if (!foundCg) throw new IOException("No free blocks available on disk.");
                }
                // Tail allocation: mark individual fragments
                for (int tf = 0; tf < fragsThisBlock; tf++)
                    MarkFragmentUsed(currentCgInfo, (int)freeFrag + tf);
            }

            long absFragAddr = (long)currentCg * sb.FragsPerGroup + freeFrag;
            state.Allocated.Add((absFragAddr, fragsThisBlock));
            swAlloc.Stop();

            // Tail block: flush any pending batch and write the tail separately
            // to avoid writing blockSize bytes into a smaller fragment allocation
            if (usesTail && fragsThisBlock < fragsPerBlock)
            {
                FlushBatch();

                // Read remaining data into a fragment-sized buffer
                int tailSize = fragsThisBlock * (int)sb.FragmentSize;
                byte[] tailBuf = new byte[tailSize];
                swRead.Start();
                int toRead = (int)Math.Min(tailSize, bytesRemaining);
                int totalRead = 0;
                while (totalRead < toRead)
                {
                    int r = sourceData.Read(tailBuf, totalRead, toRead - totalRead);
                    if (r == 0) break;
                    totalRead += r;
                }
                swRead.Stop();
                if (totalRead < toRead)
                    throw new EndOfStreamException($"Source for '{name}' ended early ({fileSize - bytesRemaining + totalRead} of {fileSize} bytes); nothing was linked.");
                bytesRemaining -= toRead;

                if (!_dryRun)
                {
                    _pendingWrite?.Wait();
                    long tailOffset = _partitionOffset + (absFragAddr * sb.FragmentSize);
                    _disk.WriteBytes(tailOffset, tailBuf);
                }
                totalFlushedBytes += tailSize;
                flushCount++;
            }
            else
            {
            // Full block: normal batch logic
            // Check if this block is sequential with the current batch
            bool isSequential = (_batchBlockCount > 0 &&
                                  absFragAddr == _batchStartFrag + (long)_batchBlockCount * fragsPerBlock &&
                                  currentCg == _batchCg &&
                                  _batchBlockCount < MAX_BATCH);

            if (!isSequential && _batchBlockCount > 0)
                FlushBatch(wait: false);

            if (_batchBlockCount == 0)
            {
                _batchStartFrag = absFragAddr;
                _batchCg = currentCg;
            }

            // Read data from source into batch buffer
            swRead.Start();
            int toRead = (int)Math.Min(blockSize, bytesRemaining);
            int bufOffset = _batchBlockCount * blockSize;
            if (toRead < blockSize)
                Array.Clear(_pingPong![_activeBuf], bufOffset, blockSize);

            int totalRead = 0;
            while (totalRead < toRead)
            {
                int r = sourceData.Read(_pingPong![_activeBuf], bufOffset + totalRead, toRead - totalRead);
                if (r == 0) break;
                totalRead += r;
            }
            swRead.Stop();
            if (totalRead < toRead)
            {
                _batchBlockCount++; // keep batch bookkeeping consistent before bailing out
                throw new EndOfStreamException($"Source for '{name}' ended early ({fileSize - bytesRemaining + totalRead} of {fileSize} bytes); nothing was linked.");
            }
            _batchBlockCount++;
            bytesRemaining -= toRead;
            } // end full-block branch

            // Store block pointer
            if (b < 12)
            {
                directBlocks[b] = absFragAddr;
            }
            else
            {
                long ptrsPerBlock = blockSize / 8;

                if (b < 12 + ptrsPerBlock)
                {
                    int ptrIdx = (int)(b - 12);
                    if (ptrIdx == 0)
                    {
                        long indFrag = FindFreeFragments(currentCgInfo, fragsPerBlock);
                        if (indFrag < 0)
                        {
                            // Current CG full — search for next CG with space
                            FlushBatch();
                            WriteCylinderGroup(currentCgInfo);
                            for (int ci = 1; ci < sb.CylinderGroups; ci++)
                            {
                                int tryCg = (currentCg + ci) % sb.CylinderGroups;
                                currentCgInfo = ReadCylinderGroup(tryCg);
                                indFrag = FindFreeFragments(currentCgInfo, fragsPerBlock);
                                if (indFrag >= 0) { currentCg = tryCg; break; }
                            }
                            if (indFrag < 0) throw new IOException("No space for indirect block.");
                        }
                        indirectBlock = (long)currentCg * sb.FragsPerGroup + indFrag;
                        MarkFragmentsUsed(currentCgInfo, (int)indFrag, fragsPerBlock);
                        state.Allocated.Add((indirectBlock, fragsPerBlock));
                        indirectBlockBuf = new byte[blockSize];
                    }
                    BinaryPrimitives.WriteInt64BigEndian(indirectBlockBuf!.AsSpan(ptrIdx * 8), absFragAddr);
                }
                else if (b < 12 + ptrsPerBlock + ptrsPerBlock * ptrsPerBlock)
                {
                    long dblIdx = b - 12 - ptrsPerBlock;
                    int l1Idx = (int)(dblIdx / ptrsPerBlock);
                    int l2Idx = (int)(dblIdx % ptrsPerBlock);

                    if (dblIdx == 0)
                    {
                        if (indirectBlockBuf != null)
                        {
                            deferredMetaBlocks.Add((indirectBlock, indirectBlockBuf));
                            indirectBlockBuf = null;
                        }
                        long diFrag = FindFreeFragments(currentCgInfo, fragsPerBlock);
                        if (diFrag < 0)
                        {
                            FlushBatch();
                            WriteCylinderGroup(currentCgInfo);
                            for (int ci = 1; ci < sb.CylinderGroups; ci++)
                            {
                                int tryCg = (currentCg + ci) % sb.CylinderGroups;
                                currentCgInfo = ReadCylinderGroup(tryCg);
                                diFrag = FindFreeFragments(currentCgInfo, fragsPerBlock);
                                if (diFrag >= 0) { currentCg = tryCg; break; }
                            }
                            if (diFrag < 0) throw new IOException("No space for double indirect block.");
                        }
                        doubleIndirectBlock = (long)currentCg * sb.FragsPerGroup + diFrag;
                        MarkFragmentsUsed(currentCgInfo, (int)diFrag, fragsPerBlock);
                        state.Allocated.Add((doubleIndirectBlock, fragsPerBlock));
                        doubleIndirectBuf = new byte[blockSize];
                    }

                    if (l2Idx == 0)
                    {
                        if (currentL1Buf != null && _currentL1Block != 0)
                            deferredMetaBlocks.Add((_currentL1Block, currentL1Buf));

                        long l1Frag = FindFreeFragments(currentCgInfo, fragsPerBlock);
                        if (l1Frag < 0)
                        {
                            FlushBatch();
                            WriteCylinderGroup(currentCgInfo);
                            for (int ci = 1; ci < sb.CylinderGroups; ci++)
                            {
                                int tryCg = (currentCg + ci) % sb.CylinderGroups;
                                currentCgInfo = ReadCylinderGroup(tryCg);
                                l1Frag = FindFreeFragments(currentCgInfo, fragsPerBlock);
                                if (l1Frag >= 0) { currentCg = tryCg; break; }
                            }
                            if (l1Frag < 0) throw new IOException("No space for L1 indirect block.");
                        }
                        long l1Addr = (long)currentCg * sb.FragsPerGroup + l1Frag;
                        MarkFragmentsUsed(currentCgInfo, (int)l1Frag, fragsPerBlock);
                        state.Allocated.Add((l1Addr, fragsPerBlock));

                        currentL1Buf = new byte[blockSize];
                        BinaryPrimitives.WriteInt64BigEndian(doubleIndirectBuf!.AsSpan(l1Idx * 8), l1Addr);
                        _currentL1Block = l1Addr;
                    }
                    BinaryPrimitives.WriteInt64BigEndian(currentL1Buf!.AsSpan(l2Idx * 8), absFragAddr);
                }
                else
                {
                    throw new NotImplementedException("Triple indirect blocks not yet implemented (file > ~68 GB).");
                }
            }
        }

        // Flush remaining batch:
        // - Files with indirect blocks: flush now (they're standalone large writes)
        // - Direct-only files: leave batch open for next small file to continue
        if (needsIndirect)
        {
            FlushBatch();
            _pendingWrite?.Wait();
            _pendingWrite = null;
        }

        swMeta.Start();
        deferredMetaBlocks.Sort((a, b) => a.Addr.CompareTo(b.Addr));
        foreach (var (addr, data) in deferredMetaBlocks)
            WriteDataBlock(addr, data);
        if (indirectBlockBuf != null)
            WriteDataBlock(indirectBlock, indirectBlockBuf);
        if (currentL1Buf != null && _currentL1Block != 0)
            WriteDataBlock(_currentL1Block, currentL1Buf);
        if (doubleIndirectBuf != null)
            WriteDataBlock(doubleIndirectBlock, doubleIndirectBuf);

        // Write updated CG(s)
        WriteCylinderGroup(currentCgInfo);
        if (currentCg != inodeCg)
            WriteCylinderGroup(cg);

        // 4. Mark inode used
        MarkInodeUsed(cg, freeIdx);
        state.InodeMarked = true;
        WriteCylinderGroup(cg);

        // Data, indirect blocks and bitmaps must be on disk before the inode refers to them.
        if (SafeWriteOrdering) DurabilityBarrier();

        // 5. Build and write inode
        byte[] inodeData = BuildFileInode(fileSize, directBlocks, indirectBlock, doubleIndirectBlock);
        WriteInode(newInodeNumber, inodeData);
        state.InodeWritten = true;
        swMeta.Stop();

        // Dump the written inode for debugging
        if (VerboseLog)
        {
            _log($"  === WRITTEN FILE INODE {newInodeNumber} ===");
            for (int r = 0; r < inodeData.Length; r += 32)
                _log($"    0x{r:X2}: {BitConverter.ToString(inodeData, r, Math.Min(32, inodeData.Length - r))}");
            _log($"    di_size=0x{fileSize:X} ({fileSize}), di_blocks={BinaryPrimitives.ReadInt64BigEndian(inodeData.AsSpan(0x18))}, indirect=0x{indirectBlock:X}, dblIndirect=0x{doubleIndirectBlock:X}");
        }

        // 6. Add to parent directory
        swDir.Start();
        var parentInode = _fs.ReadInode(parentInodeNumber);
        AddEntryToDirectory(parentInodeNumber, parentInode, newInodeNumber, name, 8, inodeCg, cg, currentCg, currentCgInfo, fragsPerBlock, out var fileExpanded, out _);
        state.Linked = true;
        swDir.Stop();

        swTotal.Stop();
        _log($"  File '{name}' written as inode {newInodeNumber} ({fileSize} bytes, {blocksNeeded} blocks)");
        _log($"  TIMING: total={swTotal.ElapsedMilliseconds}ms alloc={swAlloc.ElapsedMilliseconds}ms read={swRead.ElapsedMilliseconds}ms flush={swFlush.ElapsedMilliseconds}ms (wait={swWait.ElapsedMilliseconds}ms) meta={swMeta.ElapsedMilliseconds}ms dir={swDir.ElapsedMilliseconds}ms flushes={flushCount} flushedMB={totalFlushedBytes / (1024 * 1024)}");
        return newInodeNumber;
    }


    /// <summary>
    /// Add a directory entry to a parent directory. Tries all existing data blocks first.
    /// If all blocks are full, allocates a new block and expands the directory.
    /// </summary>
    public void AddEntryToDirectory(long parentInodeNumber, Ufs2Inode parentInode,
        long newInodeNumber, string name, byte entryType,
        int inodeCg, CylinderGroupInfo inodeCgInfo, int blockCg, CylinderGroupInfo blockCgInfo, int fragsPerBlock,
        out bool expanded, out byte[]? expandedRawParent)
    {
        var sb = _fs.Superblock!;
        long dirSize = parentInode.Size;
        long bytesLeft = dirSize;
        expanded = false;
        expandedRawParent = null;

        // Try each existing directory data block (scan up to di_size)
        for (int i = 0; i < 12; i++)
        {
            long blockAddr = parentInode.DirectBlocks[i];
            if (blockAddr == 0) break;
            if (bytesLeft <= 0) break;

            // di_size never exceeds what is allocated, so reading up to di_size is safe
            // whether the last block is a fragment run or a whole block.
            int blockCapacity = (int)sb.BlockSize;
            int thisBlockSize = (int)Math.Min(blockCapacity, bytesLeft);
            long blockDiskOffset = _partitionOffset + blockAddr * sb.FragmentSize;
            byte[] dirBlock = _disk.ReadBytes(blockDiskOffset, thisBlockSize);

            try
            {
                byte[] updated = AddEntryToDirectoryBlock(dirBlock, newInodeNumber, name, entryType);
                QueueWrite(blockDiskOffset, updated, $"Parent dir block {i} (add '{name}')");
                return; // Success
            }
            catch (IOException ex)
            {
                _log($"  Block {i} full ({thisBlockSize} bytes): {ex.Message}");
                // This block is full, try next
            }
            bytesLeft -= thisBlockSize;
        }

        GrowDirectory(parentInodeNumber, newInodeNumber, name, entryType, blockCg, blockCgInfo, fragsPerBlock,
            out expandedRawParent);
        expanded = true;
    }

    /// <summary>
    /// Append one 512-byte directory chunk (DIRBLKSIZ) holding the new entry, the way
    /// FFS does it: the last block only owns enough fragments to cover di_size. When the
    /// chunk does not fit, the last block grows by one fragment (in place if the next
    /// fragment is free, otherwise the fragments are copied to a new run), and a new
    /// block starts as a single fragment. Upstream allocated whole blocks while counting
    /// di_size in 512-byte steps, which fsck reports as INCORRECT BLOCK COUNT and
    /// BLK(S) MISSING IN BIT MAPS.
    /// </summary>
    private void GrowDirectory(long dirInode, long newInodeNumber, string name, byte entryType,
        int preferredCg, CylinderGroupInfo preferredCgInfo, int fpb, out byte[]? rawDirOut)
    {
        var sb = _fs.Superblock!;
        int fsize = (int)sb.FragmentSize;
        int bsize = (int)sb.BlockSize;
        byte[] nameBytes = ValidateEntryName(name);

        byte[] raw = ReadRawInode(dirInode);
        long size = BinaryPrimitives.ReadInt64BigEndian(raw.AsSpan(0x10));
        long diBlocks = BinaryPrimitives.ReadInt64BigEndian(raw.AsSpan(0x18));
        if (size <= 0 || size % 512 != 0)
            throw new InvalidDataException($"Directory inode {dirInode} has an unexpected size ({size}).");
        if (BinaryPrimitives.ReadInt64BigEndian(raw.AsSpan(0xD0)) != 0)
            throw new IOException("Directory uses indirect blocks — growing it is not supported yet.");

        int lastIdx = (int)((size - 1) / bsize);
        long totalFrags = diBlocks * 512 / fsize;
        int allocLast = (int)Math.Clamp(totalFrags - (long)lastIdx * fpb, 1, fpb);
        long lastAddr = BinaryPrimitives.ReadInt64BigEndian(raw.AsSpan(0x70 + lastIdx * 8));

        long newSize = size + 512;
        int targetIdx;
        long targetAddr;
        int sectionOffset; // byte offset of the new chunk inside the target block

        if (size % bsize == 0)
        {
            // Last block is full: start a new block with one fragment.
            targetIdx = lastIdx + 1;
            if (targetIdx >= 12)
                throw new IOException("Directory has 12 direct blocks full — indirect directory blocks not supported yet.");
            var (cgNum, cgInfo, local) = AllocFragments(preferredCg, preferredCgInfo, 1);
            targetAddr = (long)cgNum * sb.FragsPerGroup + local;
            MarkFragmentsUsed(cgInfo, local, 1);
            WriteCylinderGroup(cgInfo);
            diBlocks += fsize / 512;
            sectionOffset = 0;
            byte[] frag = new byte[fsize];
            for (int sec = 0; sec < fsize; sec += 512)
                BinaryPrimitives.WriteUInt16BigEndian(frag.AsSpan(sec + 4), 512);
            WriteEntryInChunk(frag, 0, newInodeNumber, nameBytes, entryType);
            QueueWrite(_partitionOffset + targetAddr * fsize, frag, $"New directory block {targetIdx} (add '{name}')");
            _log($"  Directory grew: new block {targetIdx} at frag 0x{targetAddr:X}");
        }
        else
        {
            targetIdx = lastIdx;
            sectionOffset = (int)(size - (long)lastIdx * bsize);
            int needed = (sectionOffset + 512 + fsize - 1) / fsize;
            targetAddr = lastAddr;

            if (needed > allocLast)
            {
                // Need one more fragment for this block.
                int cgNum = (int)(lastAddr / sb.FragsPerGroup);
                int local = (int)(lastAddr % sb.FragsPerGroup);
                var cgInfo = ReadCylinderGroup(cgNum);
                int nextLocal = local + allocLast;
                bool inPlace = needed <= fpb && (nextLocal % fpb) != 0
                    && IsFragmentFree(cgInfo, nextLocal)
                    && !_protectedFragments.Contains((long)cgNum * sb.FragsPerGroup + nextLocal);
                if (inPlace)
                {
                    MarkFragmentsUsed(cgInfo, nextLocal, needed - allocLast);
                    WriteCylinderGroup(cgInfo);
                    _log($"  Directory block {lastIdx} extended in place to {needed} fragment(s)");
                }
                else
                {
                    var (newCg, newCgInfo, newLocal) = AllocFragments(cgNum, cgInfo, needed);
                    long newAddr = (long)newCg * sb.FragsPerGroup + newLocal;
                    byte[] oldData = _disk.ReadBytes(_partitionOffset + lastAddr * fsize, allocLast * fsize);
                    byte[] moved = new byte[needed * fsize];
                    Array.Copy(oldData, moved, oldData.Length);
                    for (int sec = oldData.Length; sec < moved.Length; sec += 512)
                        BinaryPrimitives.WriteUInt16BigEndian(moved.AsSpan(sec + 4), 512);
                    MarkFragmentsUsed(newCgInfo, newLocal, needed);
                    WriteCylinderGroup(newCgInfo);
                    QueueWrite(_partitionOffset + newAddr * fsize, moved, $"Relocate directory block {lastIdx} to grow it");
                    // The old fragments are released after the inode points at the copy.
                    _pendingDirFree = (lastAddr, allocLast);
                    targetAddr = newAddr;
                    BinaryPrimitives.WriteInt64BigEndian(raw.AsSpan(0x70 + lastIdx * 8), newAddr);
                    _log($"  Directory block {lastIdx} relocated from 0x{lastAddr:X} to 0x{newAddr:X} ({needed} fragments)");
                }
                diBlocks += (long)(needed - allocLast) * fsize / 512;
            }

            // Write the new chunk (a fresh 512-byte section) inside the target block.
            long chunkOffset = _partitionOffset + targetAddr * fsize + sectionOffset;
            byte[] chunk = new byte[512];
            WriteEntryInChunk(chunk, 0, newInodeNumber, nameBytes, entryType);
            QueueWrite(chunkOffset, chunk, $"Directory chunk at block {targetIdx}+{sectionOffset} (add '{name}')");
        }

        if (targetIdx != lastIdx)
            BinaryPrimitives.WriteInt64BigEndian(raw.AsSpan(0x70 + targetIdx * 8), targetAddr);
        BinaryPrimitives.WriteInt64BigEndian(raw.AsSpan(0x10), newSize);
        BinaryPrimitives.WriteInt64BigEndian(raw.AsSpan(0x18), diBlocks);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BinaryPrimitives.WriteInt64BigEndian(raw.AsSpan(0x28), now); // mtime
        BinaryPrimitives.WriteInt64BigEndian(raw.AsSpan(0x30), now); // ctime
        if (SafeWriteOrdering) DurabilityBarrier();
        WriteInode(dirInode, raw);

        if (_pendingDirFree.HasValue)
        {
            var (freeAddr, freeCount) = _pendingDirFree.Value;
            _pendingDirFree = null;
            FreeFragmentRun(freeAddr, freeCount);
        }
        rawDirOut = raw;
        _log($"  Directory inode {dirInode}: size {size} -> {newSize}, di_blocks {diBlocks}");
    }

    private (long Addr, int Count)? _pendingDirFree;

    private static void WriteEntryInChunk(byte[] buf, int offset, long inode, byte[] nameBytes, byte entryType)
    {
        Array.Clear(buf, offset, 512);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset), (uint)inode);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(offset + 4), 512);
        buf[offset + 6] = entryType;
        buf[offset + 7] = (byte)nameBytes.Length;
        Array.Copy(nameBytes, 0, buf, offset + 8, nameBytes.Length);
    }

    /// <summary>Find <paramref name="count"/> free fragments inside one block, preferring a CG.</summary>
    private (int CgNum, CylinderGroupInfo CgInfo, int Local) AllocFragments(int preferredCg, CylinderGroupInfo preferredCgInfo, int count)
    {
        var sb = _fs.Superblock!;
        long local = FindFreeFragments(preferredCgInfo, count);
        if (local >= 0) return (preferredCg, preferredCgInfo, (int)local);
        for (int i = 1; i < sb.CylinderGroups; i++)
        {
            int tryCg = (preferredCg + i) % sb.CylinderGroups;
            var info = ReadCylinderGroup(tryCg);
            if (info.Magic != 0x00090255) continue;
            local = FindFreeFragments(info, count);
            if (local >= 0) return (tryCg, info, (int)local);
        }
        throw new IOException("No free space left for the directory.");
    }

    /// <summary>
    /// Queue a write operation. In dry-run mode, just logs it.
    /// In live mode, executes immediately.
    /// </summary>
    private void QueueWrite(long diskOffset, byte[] data, string description)
    {
        var write = new PendingWrite
        {
            DiskOffset = diskOffset,
            Data = data,
            Description = description
        };
        _pendingWrites.Add(write);

        if (_dryRun)
        {
            _log($"  [FAKE WRITE TEST] Would write {data.Length} bytes at offset 0x{diskOffset:X}: {description}");
        }
        else
        {
            int sectorSize = 512;
            long alignedStart = (diskOffset / sectorSize) * sectorSize;
            int startDelta = (int)(diskOffset - alignedStart);

            if (startDelta == 0 && (data.Length % sectorSize) == 0)
            {
                // Already sector-aligned — write directly, no read needed
                _disk.WriteBytes(diskOffset, data);
            }
            else
            {
                // Unaligned write — read-modify-write
                long alignedEnd = ((diskOffset + data.Length + sectorSize - 1) / sectorSize) * sectorSize;
                int alignedLen = (int)(alignedEnd - alignedStart);
                byte[] aligned = _disk.ReadBytes(alignedStart, alignedLen);
                Array.Copy(data, 0, aligned, startDelta, data.Length);
                _disk.WriteBytes(alignedStart, aligned);
            }
        }
    }

    /// <summary>
    /// Update the superblock's global summary (fs_cstotal) and timestamp after writes.
    /// Recalculates totals by scanning all CG headers.
    /// Also updates the on-disk CS summary table.
    /// </summary>
    public void UpdateSuperblock()
    {
        // Flush all pending data writes and deferred CG writes first, and make them
        // stable before the superblock summary that describes them is rewritten.
        ReleaseBatchReservation();
        FlushDataBatch();
        FlushDirtyCgs();
        if (!_dryRun) _disk.Flush();
        
        var sb = _fs.Superblock!;
        int fragsPerBlock = (int)(sb.BlockSize / sb.FragmentSize);
        
        long sbOffset = _partitionOffset + 65536;
        byte[] sbData = _disk.ReadBytes(sbOffset, 8192);
        
        // fs_cstotal at 0x3F0: int64 BE fields (cs_ndir, cs_nbfree, cs_nifree, cs_nffree, cs_numclusters)
        long oldNdir = BinaryPrimitives.ReadInt64BigEndian(sbData.AsSpan(0x3F0));
        long oldNbfree = BinaryPrimitives.ReadInt64BigEndian(sbData.AsSpan(0x3F8));
        long oldNifree = BinaryPrimitives.ReadInt64BigEndian(sbData.AsSpan(0x400));
        long oldNffree = BinaryPrimitives.ReadInt64BigEndian(sbData.AsSpan(0x408));
        long oldNclusters = BinaryPrimitives.ReadInt64BigEndian(sbData.AsSpan(0x410));
        
        _log($"  SB before: dirs={oldNdir}, blocks={oldNbfree}, inodes={oldNifree}, frags={oldNffree}, clusters={oldNclusters}");
        
        int cblkno = BinaryPrimitives.ReadInt32BigEndian(_fs.RawSuperblockData.AsSpan(0x0C));
        long totalNdir = 0, totalNbfree = 0, totalNifree = 0, totalNffree = 0, totalNclusters = 0;
        
        // Read CS summary table
        long csAddr = BinaryPrimitives.ReadInt64BigEndian(sbData.AsSpan(0x448));
        int csSize = BinaryPrimitives.ReadInt32BigEndian(sbData.AsSpan(0x9C));
        long csSummaryOffset = (csAddr > 0 && csSize > 0) ? _partitionOffset + csAddr * sb.FragmentSize : 0;
        byte[]? csData = (csSummaryOffset > 0) ? _disk.ReadBytes(csSummaryOffset, csSize) : null;
        
        int cgSize = BinaryPrimitives.ReadInt32BigEndian(_fs.RawSuperblockData.AsSpan(0xA0)); // fs_cgsize

        for (int cgi = 0; cgi < sb.CylinderGroups; cgi++)
        {
            long cgOffset = _partitionOffset + (long)cgi * sb.FragsPerGroup * sb.FragmentSize;
            long cgHeaderOffset = cgOffset + (long)cblkno * sb.FragmentSize;

            byte[] cgHeader = _cgCache.TryGetValue(cgi, out var cachedCg) ? cachedCg.RawData : _disk.ReadBytes(cgHeaderOffset, cgSize);

            int magic = BinaryPrimitives.ReadInt32BigEndian(cgHeader.AsSpan(0x04));
            if (magic != 0x00090255)
            {
                if (csData != null && cgi * 16 + 16 <= csSize)
                    Array.Clear(csData, cgi * 16, 16);
                continue;
            }

            int ndir = BinaryPrimitives.ReadInt32BigEndian(cgHeader.AsSpan(0x18));
            int nbfree = BinaryPrimitives.ReadInt32BigEndian(cgHeader.AsSpan(0x1C));
            int nifree = BinaryPrimitives.ReadInt32BigEndian(cgHeader.AsSpan(0x20));
            int nffree = BinaryPrimitives.ReadInt32BigEndian(cgHeader.AsSpan(0x24));

            // Count clusters from cluster bitmap
            int clusteroff = BinaryPrimitives.ReadInt32BigEndian(cgHeader.AsSpan(0x6C));
            int nclusterblks = BinaryPrimitives.ReadInt32BigEndian(cgHeader.AsSpan(0x70));
            if (clusteroff > 0 && nclusterblks > 0)
            {
                int bitmapBytes = (nclusterblks + 7) / 8;
                byte[] fullCg = (clusteroff + bitmapBytes <= cgHeader.Length) ? cgHeader : _disk.ReadBytes(cgHeaderOffset, clusteroff + bitmapBytes);

                // popcount whole bytes, mask the trailing partial byte
                int wholeBytes = nclusterblks / 8;
                for (int cbi = 0; cbi < wholeBytes; cbi++)
                    totalNclusters += System.Numerics.BitOperations.PopCount(fullCg[clusteroff + cbi]);
                int tailBits = nclusterblks % 8;
                if (tailBits > 0)
                    totalNclusters += System.Numerics.BitOperations.PopCount(
                        (uint)(fullCg[clusteroff + wholeBytes] & ((1 << tailBits) - 1)));
            }
            
            totalNdir += ndir;
            totalNbfree += nbfree;
            totalNifree += nifree;
            totalNffree += nffree;
            
            if (csData != null && cgi * 16 + 16 <= csSize)
            {
                BinaryPrimitives.WriteInt32BigEndian(csData.AsSpan(cgi * 16 + 0), ndir);
                BinaryPrimitives.WriteInt32BigEndian(csData.AsSpan(cgi * 16 + 4), nbfree);
                BinaryPrimitives.WriteInt32BigEndian(csData.AsSpan(cgi * 16 + 8), nifree);
                BinaryPrimitives.WriteInt32BigEndian(csData.AsSpan(cgi * 16 + 12), nffree);
            }
        }
        
        _log($"  SB recalc: dirs={totalNdir}, blocks={totalNbfree}, inodes={totalNifree}, frags={totalNffree}, clusters={totalNclusters}");
        
        // Write fs_cstotal (5 fields, each int64)
        BinaryPrimitives.WriteInt64BigEndian(sbData.AsSpan(0x3F0), totalNdir);
        BinaryPrimitives.WriteInt64BigEndian(sbData.AsSpan(0x3F8), totalNbfree);
        BinaryPrimitives.WriteInt64BigEndian(sbData.AsSpan(0x400), totalNifree);
        BinaryPrimitives.WriteInt64BigEndian(sbData.AsSpan(0x408), totalNffree);
        BinaryPrimitives.WriteInt64BigEndian(sbData.AsSpan(0x410), totalNclusters);
        
        QueueWrite(sbOffset, sbData, "Superblock fs_cstotal + cs_numclusters");
        
        if (csData != null && csSummaryOffset > 0)
        {
            QueueWrite(csSummaryOffset, csData, $"CS summary ({csSize} bytes)");
            _log($"  CS summary written ({sb.CylinderGroups} entries).");
        }
        if (!_dryRun) _disk.Flush();
        
        _log($"  Done (delta: dirs={totalNdir - oldNdir}, blocks={totalNbfree - oldNbfree}, inodes={totalNifree - oldNifree}, frags={totalNffree - oldNffree}, clusters={totalNclusters - oldNclusters}).");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // DELETE / MOVE operations
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mark a single fragment as free. Reverse of MarkFragmentUsed.
    /// </summary>
    public void MarkFragmentFree(CylinderGroupInfo cg, int fragIdx)
    {
        var sb = _fs.Superblock!;
        int fragsPerBlock = (int)(sb.BlockSize / sb.FragmentSize);
        int blockStart = (fragIdx / fragsPerBlock) * fragsPerBlock;

        int[] oldRuns = GetBlockFreeRuns(cg, blockStart, fragsPerBlock);

        int byteIdx = cg.FreeBlocksOffset + (fragIdx / 8);
        int bitIdx = fragIdx % 8;
        cg.RawData[byteIdx] |= (byte)(1 << bitIdx); // Set bit = free

        int[] newRuns = GetBlockFreeRuns(cg, blockStart, fragsPerBlock);
        UpdateFrsum(cg, oldRuns, newRuns, fragsPerBlock);

        int oldFreeCount = 0, newFreeCount = 0;
        foreach (int r in oldRuns) oldFreeCount += r;
        foreach (int r in newRuns) newFreeCount += r;

        if (newFreeCount == fragsPerBlock)
        {
            // All frags now free — move from nffree to nbfree
            int nffree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x24));
            BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x24), nffree - oldFreeCount);
            int nbfree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x1C));
            BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x1C), nbfree + 1);
            cg.FreeBlocks = nbfree + 1;
        }
        else
        {
            int nffree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x24));
            BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x24), nffree + 1);
        }

        UpdateClusterBitmap(cg, fragIdx / fragsPerBlock, fragsPerBlock);
    }

    /// <summary>
    /// Mark a full block of fragments as free. Reverse of MarkFragmentsUsed.
    /// </summary>
    public void MarkFragmentsFree(CylinderGroupInfo cg, int startFrag, int count)
    {
        var sb = _fs.Superblock!;
        int fragsPerBlock = (int)(sb.BlockSize / sb.FragmentSize);
        int blockStart = (startFrag / fragsPerBlock) * fragsPerBlock;

        int[] oldRuns = GetBlockFreeRuns(cg, blockStart, fragsPerBlock);

        for (int f = startFrag; f < startFrag + count; f++)
        {
            int byteIdx = cg.FreeBlocksOffset + (f / 8);
            int bitIdx = f % 8;
            cg.RawData[byteIdx] |= (byte)(1 << bitIdx);
        }

        int[] newRuns = GetBlockFreeRuns(cg, blockStart, fragsPerBlock);
        UpdateFrsum(cg, oldRuns, newRuns, fragsPerBlock);

        int oldFreeCount = 0, newFreeCount = 0;
        foreach (int r in oldRuns) oldFreeCount += r;
        foreach (int r in newRuns) newFreeCount += r;
        bool wasFullBlock = (oldFreeCount == fragsPerBlock);
        bool isFullBlock = (newFreeCount == fragsPerBlock);

        if (!wasFullBlock && isFullBlock)
        {
            int nffree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x24));
            BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x24), nffree - oldFreeCount);
            int nbfree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x1C));
            BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x1C), nbfree + 1);
            cg.FreeBlocks = nbfree + 1;
        }
        else if (!isFullBlock)
        {
            int nffree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x24));
            BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x24), nffree + (newFreeCount - oldFreeCount));
        }

        UpdateClusterBitmap(cg, startFrag / fragsPerBlock, fragsPerBlock);
    }

    /// <summary>
    /// Mark an inode as free in the CG bitmap.
    /// </summary>
    public void MarkInodeFree(CylinderGroupInfo cg, int inodeIdx)
    {
        int byteIdx = cg.InodesUsedOffset + (inodeIdx / 8);
        int bitIdx = inodeIdx % 8;
        cg.RawData[byteIdx] &= (byte)~(1 << bitIdx);
        cg.FreeInodes++;
        BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x20), cg.FreeInodes);
    }

    /// <summary>
    /// Fragments occupied by direct block <paramref name="i"/>, using the FFS rule
    /// (blksize): every block is full except the last direct block of a file with no
    /// indirect blocks, which holds only enough fragments for the remaining bytes.
    /// The count is cross-checked against di_blocks and the smaller value wins, so a
    /// disagreement can leak space but can never free fragments owned by another file.
    /// Upstream freed a whole block at every tail address, releasing neighbours' data.
    /// </summary>
    private int DirectBlockFragments(Ufs2Inode inode, int i, int lastDirect, bool hasIndirect, int nonZeroDirect)
    {
        var sb = _fs.Superblock!;
        int fpb = (int)(sb.BlockSize / sb.FragmentSize);
        if (hasIndirect || i != lastDirect) return fpb;

        long bySize = inode.Size >= (long)(i + 1) * sb.BlockSize
            ? fpb
            : (inode.Size - (long)i * sb.BlockSize + sb.FragmentSize - 1) / sb.FragmentSize;
        long totalFrags = inode.Blocks * 512 / sb.FragmentSize;
        long byBlocks = totalFrags - (long)(nonZeroDirect - 1) * fpb;

        long frags;
        if (bySize >= 1 && byBlocks >= 1) frags = Math.Min(bySize, byBlocks);
        else if (byBlocks >= 1) frags = byBlocks;
        else frags = bySize;
        if (bySize != byBlocks)
            _log($"  NOTE inode {inode.InodeNumber}: last block size says {bySize} frag(s), di_blocks says {byBlocks}; freeing {Math.Clamp(frags, 1, fpb)}");
        return (int)Math.Clamp(frags, 1, fpb);
    }

    /// <summary>
    /// List every fragment run owned by an inode (data and indirect blocks, all three
    /// indirection levels, holes skipped) and check each run is inside the filesystem
    /// and currently marked in use. Nothing is changed, so a bad inode aborts the
    /// operation before any bitmap is touched.
    /// </summary>
    private List<(long AbsFrag, int Count)> CollectInodeFragments(Ufs2Inode inode)
    {
        var sb = _fs.Superblock!;
        int fpb = (int)(sb.BlockSize / sb.FragmentSize);
        var runs = new List<(long AbsFrag, int Count)>();

        // Inline symlinks and empty files own no blocks; their di_db area may hold text.
        if (inode.Blocks <= 0) return runs;

        bool hasIndirect = inode.IndirectBlock != 0 || inode.DoubleIndirectBlock != 0 || inode.TripleIndirectBlock != 0;
        int lastDirect = -1, nonZeroDirect = 0;
        for (int i = 0; i < 12; i++)
            if (inode.DirectBlocks[i] != 0) { lastDirect = i; nonZeroDirect++; }

        for (int i = 0; i < 12; i++)
        {
            long frag = inode.DirectBlocks[i];
            if (frag == 0) continue; // hole
            runs.Add((frag, DirectBlockFragments(inode, i, lastDirect, hasIndirect, nonZeroDirect)));
        }

        void Walk(long addr, int level)
        {
            if (addr == 0) return;
            CheckFragmentAddress(addr, fpb, $"Indirect block (level {level}) of inode {inode.InodeNumber}");
            byte[] blk = _disk.ReadBytes(_partitionOffset + addr * sb.FragmentSize, (int)sb.BlockSize);
            for (int p = 0; p < blk.Length / 8; p++)
            {
                long ptr = BinaryPrimitives.ReadInt64BigEndian(blk.AsSpan(p * 8));
                if (ptr == 0) continue; // hole
                if (level == 1) runs.Add((ptr, fpb));
                else Walk(ptr, level - 1);
            }
            runs.Add((addr, fpb));
        }
        Walk(inode.IndirectBlock, 1);
        Walk(inode.DoubleIndirectBlock, 2);
        Walk(inode.TripleIndirectBlock, 3);

        var seen = new HashSet<long>();
        var result = new List<(long AbsFrag, int Count)>();
        int alreadyFree = 0;
        foreach (var (absFrag, count) in runs)
        {
            CheckFragmentAddress(absFrag, count, $"Block pointer of inode {inode.InodeNumber}");
            var cg = ReadCylinderGroup((int)(absFrag / sb.FragsPerGroup));
            int local = (int)(absFrag % sb.FragsPerGroup);
            long runStart = -1;
            int runLen = 0;
            for (int f = 0; f < count; f++)
            {
                if (!seen.Add(absFrag + f))
                    throw new InvalidDataException($"Inode {inode.InodeNumber} references fragment 0x{absFrag + f:X} twice; refusing to free it.");
                if (IsFragmentFree(cg, local + f))
                {
                    // Already free (seen on some native PS3 directories): nothing to release,
                    // and releasing it again would corrupt the free counts.
                    alreadyFree++;
                    if (runLen > 0) { result.Add((runStart, runLen)); runLen = 0; }
                    continue;
                }
                if (runLen == 0) runStart = absFrag + f;
                runLen++;
            }
            if (runLen > 0) result.Add((runStart, runLen));
        }
        if (alreadyFree > 0)
            _log($"  NOTE inode {inode.InodeNumber}: {alreadyFree} fragment(s) were already marked free; skipped");
        return result;
    }

    /// <summary>
    /// Free all data blocks owned by an inode (direct, single, double and triple indirect).
    /// </summary>
    public void FreeInodeBlocks(Ufs2Inode inode)
    {
        foreach (var (absFrag, count) in CollectInodeFragments(inode))
            FreeFragmentRun(absFrag, count);
    }

    /// <summary>
    /// Remove a directory entry by name from a parent directory.
    /// Merges the entry's reclen into the previous entry, or zeros it if first in section.
    /// When <paramref name="expectedInode"/> is given, the entry must point at that inode.
    /// Returns the inode number the entry pointed at.
    /// </summary>
    public long RemoveDirectoryEntry(long parentInodeNumber, string name, long expectedInode = -1)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var loc = LocateEntry(parentInodeNumber, nameBytes)
            ?? throw new IOException($"Entry '{name}' not found in directory (inode {parentInodeNumber})");
        if (expectedInode >= 0 && loc.Inode != expectedInode)
            throw new IOException($"Entry '{name}' points at inode {loc.Inode}, expected {expectedInode}; refusing to remove it.");

        byte[] dirBlock = loc.Block;
        int offset = loc.Offset;
        ushort recLen = BinaryPrimitives.ReadUInt16BigEndian(dirBlock.AsSpan(offset + 4));
        if (loc.PrevOffset >= 0)
        {
            // Merge into previous entry
            ushort prevRecLen = BinaryPrimitives.ReadUInt16BigEndian(dirBlock.AsSpan(loc.PrevOffset + 4));
            BinaryPrimitives.WriteUInt16BigEndian(dirBlock.AsSpan(loc.PrevOffset + 4), (ushort)(prevRecLen + recLen));
            Array.Clear(dirBlock, offset, recLen);
        }
        else
        {
            // First in section — zero inode, keep reclen for chain
            BinaryPrimitives.WriteUInt32BigEndian(dirBlock.AsSpan(offset), 0);
            dirBlock[offset + 6] = 0;
            dirBlock[offset + 7] = 0;
            Array.Clear(dirBlock, offset + 8, recLen - 8);
        }

        QueueWrite(loc.BlockDiskOffset, dirBlock, $"Remove entry '{name}' from dir block {loc.BlockIndex}");
        return loc.Inode;
    }

    private void ChangeLinkCount(long inodeNumber, int delta)
    {
        byte[] raw = ReadRawInode(inodeNumber);
        short nlink = BinaryPrimitives.ReadInt16BigEndian(raw.AsSpan(0x02));
        int updated = nlink + delta;
        if (updated > short.MaxValue)
            throw new InvalidDataException($"Link count of inode {inodeNumber} would overflow.");
        if (updated < 1)
        {
            // Never write 0 for a live inode; fsck can correct a count that is too high.
            _log($"  NOTE inode {inodeNumber}: link count {nlink}{delta:+#;-#;0} clamped to 1");
            updated = 1;
        }
        BinaryPrimitives.WriteInt16BigEndian(raw.AsSpan(0x02), (short)updated);
        BinaryPrimitives.WriteInt64BigEndian(raw.AsSpan(0x30), DateTimeOffset.UtcNow.ToUnixTimeSeconds()); // ctime
        WriteInode(inodeNumber, raw);
    }

    /// <summary>
    /// Delete a file: remove the directory entry, then release the inode and its blocks.
    /// </summary>
    public void DeleteFile(long parentInodeNumber, string name, long fileInodeNumber)
    {
        var sb = _fs.Superblock!;
        _log($"[DELETE] Deleting file '{name}' (inode {fileInodeNumber}) from parent {parentInodeNumber}");

        var inode = _fs.ReadInode(fileInodeNumber);
        if (inode.FileType == Ufs2FileType.Directory)
            throw new IOException($"'{name}' is a directory; use DeleteDirectory.");

        // Validate everything we are about to free before changing anything.
        var runs = inode.LinkCount > 1 ? new List<(long AbsFrag, int Count)>() : CollectInodeFragments(inode);

        // 1. Unlink first: a crash afterwards leaks space instead of leaving a name
        //    that points at a released inode.
        RemoveDirectoryEntry(parentInodeNumber, name, fileInodeNumber);
        if (SafeWriteOrdering) DurabilityBarrier();

        if (inode.LinkCount > 1)
        {
            // Other names still reference this inode: just drop one link.
            ChangeLinkCount(fileInodeNumber, -1);
            _log($"  Removed one of {inode.LinkCount} links to inode {fileInodeNumber}");
            return;
        }

        // 2. Release the inode and its blocks.
        WriteInode(fileInodeNumber, new byte[(int)sb.InodeSize]);
        foreach (var (absFrag, count) in runs)
            FreeFragmentRun(absFrag, count);
        int cgNum = (int)(fileInodeNumber / sb.InodesPerGroup);
        var cg = ReadCylinderGroup(cgNum);
        MarkInodeFree(cg, (int)(fileInodeNumber % sb.InodesPerGroup));
        WriteCylinderGroup(cg);
        _log($"  File '{name}' deleted");
    }

    /// <summary>
    /// Recursively delete a directory and all its contents.
    /// </summary>
    public void DeleteDirectory(long parentInodeNumber, string name, long dirInodeNumber)
    {
        var sb = _fs.Superblock!;
        _log($"[DELETE] Deleting directory '{name}' (inode {dirInodeNumber}) from parent {parentInodeNumber}");
        if (dirInodeNumber == 2) throw new IOException("Refusing to delete the root directory.");

        var inode = _fs.ReadInode(dirInodeNumber);
        if (inode.FileType != Ufs2FileType.Directory)
            throw new IOException($"'{name}' is not a directory.");
        var entries = _fs.ReadDirectory(inode);

        // Recursively delete children
        foreach (var entry in entries)
        {
            if (entry.Name == "." || entry.Name == "..") continue;
            var childInode = _fs.ReadInode(entry.InodeNumber);
            if (childInode.FileType == Ufs2FileType.Directory)
                DeleteDirectory(dirInodeNumber, entry.Name, entry.InodeNumber);
            else
                DeleteFile(dirInodeNumber, entry.Name, entry.InodeNumber);
        }

        // Re-read: deleting children may have rewritten this directory's inode.
        inode = _fs.ReadInode(dirInodeNumber);
        var runs = CollectInodeFragments(inode);

        // 1. Unlink from the parent and drop the parent's link from our "..".
        RemoveDirectoryEntry(parentInodeNumber, name, dirInodeNumber);
        ChangeLinkCount(parentInodeNumber, -1);
        if (SafeWriteOrdering) DurabilityBarrier();

        // 2. Release the directory's inode and blocks.
        WriteInode(dirInodeNumber, new byte[(int)sb.InodeSize]);
        foreach (var (absFrag, count) in runs)
            FreeFragmentRun(absFrag, count);
        int cgNum = (int)(dirInodeNumber / sb.InodesPerGroup);
        var cg = ReadCylinderGroup(cgNum);
        MarkInodeFree(cg, (int)(dirInodeNumber % sb.InodesPerGroup));
        int ndir = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x18));
        if (ndir > 0) BinaryPrimitives.WriteInt32BigEndian(cg.RawData.AsSpan(0x18), ndir - 1);
        WriteCylinderGroup(cg);

        _log($"  Directory '{name}' deleted ({entries.Count - 2} children removed)");
    }

    /// <summary>
    /// Rename an entry inside one directory. The new name is linked before the old one
    /// is removed, so an interruption leaves two names for the file, never zero.
    /// </summary>
    public void RenameEntry(long parentInodeNumber, string oldName, string newName)
    {
        var sb = _fs.Superblock!;
        int fragsPerBlock = (int)(sb.BlockSize / sb.FragmentSize);
        ValidateEntryName(newName);
        if (oldName == newName) return;
        _log($"[RENAME] '{oldName}' -> '{newName}' in inode {parentInodeNumber}");

        var loc = LocateEntry(parentInodeNumber, System.Text.Encoding.UTF8.GetBytes(oldName))
            ?? throw new IOException($"Entry '{oldName}' not found.");
        if (DirectoryContainsEntry(parentInodeNumber, newName))
            throw new IOException($"An entry named '{newName}' already exists.");

        int parentCg = (int)(parentInodeNumber / sb.InodesPerGroup);
        var cgInfo = ReadCylinderGroup(parentCg);
        AddEntryToDirectory(parentInodeNumber, _fs.ReadInode(parentInodeNumber), loc.Inode, newName, loc.Type,
            parentCg, cgInfo, parentCg, cgInfo, fragsPerBlock, out _, out _);
        if (SafeWriteOrdering) DurabilityBarrier();
        RemoveDirectoryEntry(parentInodeNumber, oldName, loc.Inode);
        _log($"  Renamed '{oldName}' to '{newName}'");
    }

    /// <summary>
    /// Move a file or directory from one parent to another (optionally renaming it).
    /// No data is copied — only directory entries, ".." and link counts change.
    /// </summary>
    public void MoveEntry(long srcParentInode, long destParentInode, string name, long entryInode, bool isDirectory)
        => MoveEntry(srcParentInode, destParentInode, name, name, entryInode);

    public void MoveEntry(long srcParentInode, long destParentInode, string name, string newName, long entryInode)
    {
        var sb = _fs.Superblock!;
        int fragsPerBlock = (int)(sb.BlockSize / sb.FragmentSize);
        ValidateEntryName(newName);
        _log($"[MOVE] Moving '{name}' (inode {entryInode}) from {srcParentInode} to {destParentInode} as '{newName}'");

        if (srcParentInode == destParentInode)
        {
            RenameEntry(srcParentInode, name, newName);
            return;
        }

        var loc = LocateEntry(srcParentInode, System.Text.Encoding.UTF8.GetBytes(name))
            ?? throw new IOException($"Entry '{name}' not found in the source directory.");
        if (loc.Inode != entryInode)
            throw new IOException($"Entry '{name}' points at inode {loc.Inode}, expected {entryInode}.");
        if (_fs.ReadInode(destParentInode).FileType != Ufs2FileType.Directory)
            throw new IOException("Destination is not a directory.");
        if (DirectoryContainsEntry(destParentInode, newName))
            throw new IOException($"Destination directory already contains '{newName}'");

        bool isDirectory = _fs.ReadInode(entryInode).FileType == Ufs2FileType.Directory;
        if (isDirectory)
        {
            // Refuse to move a directory into itself or one of its descendants.
            long walk = destParentInode;
            for (int depth = 0; depth < 4096; depth++)
            {
                if (walk == entryInode)
                    throw new IOException("Cannot move a folder inside itself.");
                if (walk == 2) break;
                long up = ReadParentOf(walk);
                if (up == walk) break;
                walk = up;
            }
        }

        // 1. Link into the destination first.
        var destInodeData = _fs.ReadInode(destParentInode);
        int destCg = (int)(destParentInode / sb.InodesPerGroup);
        var destCgInfo = ReadCylinderGroup(destCg);
        AddEntryToDirectory(destParentInode, destInodeData, entryInode, newName, loc.Type,
            destCg, destCgInfo, destCg, destCgInfo, fragsPerBlock, out _, out _);
        if (isDirectory) ChangeLinkCount(destParentInode, +1);
        if (SafeWriteOrdering) DurabilityBarrier();

        // 2. Point ".." at the new parent.
        if (isDirectory)
        {
            var dotdot = LocateEntry(entryInode, new[] { (byte)'.', (byte)'.' })
                ?? throw new InvalidDataException($"Directory inode {entryInode} has no '..' entry.");
            BinaryPrimitives.WriteUInt32BigEndian(dotdot.Block.AsSpan(dotdot.Offset), (uint)destParentInode);
            QueueWrite(dotdot.BlockDiskOffset, dotdot.Block, $"Update '..' in moved dir to {destParentInode}");
        }

        // 3. Unlink from the source.
        RemoveDirectoryEntry(srcParentInode, name, entryInode);
        if (isDirectory) ChangeLinkCount(srcParentInode, -1);

        _log($"  '{name}' moved successfully");
    }

    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verify every CG's bitmap matches its reported free counts.
    /// Logs any mismatches.
    /// </summary>
    public void VerifyCgBitmaps()
    {
        var sb = _fs.Superblock!;
        int fragsPerBlock = (int)(sb.BlockSize / sb.FragmentSize);
        int totalErrors = 0;

        for (int cgi = 0; cgi < sb.CylinderGroups; cgi++)
        {
            var cg = ReadCylinderGroup(cgi);
            if (cg.Magic != 0x00090255) continue;

            int reportedNbfree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x1C));
            int reportedNifree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x20));
            int reportedNffree = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x24));

            int totalFragsInCg = BinaryPrimitives.ReadInt32BigEndian(cg.RawData.AsSpan(0x14));
            int actualFreeBlocks = 0;
            int actualFreeFrags = 0;

            for (int b = 0; b < totalFragsInCg; b += fragsPerBlock)
            {
                int freeInBlock = 0;
                int fragsThisBlock = Math.Min(fragsPerBlock, totalFragsInCg - b);
                for (int f = b; f < b + fragsThisBlock; f++)
                {
                    int byteIdx = cg.FreeBlocksOffset + (f / 8);
                    int bitIdx = f % 8;
                    if ((cg.RawData[byteIdx] & (1 << bitIdx)) != 0)
                        freeInBlock++;
                }
                if (freeInBlock == fragsThisBlock && fragsThisBlock == fragsPerBlock)
                    actualFreeBlocks++;
                else
                    actualFreeFrags += freeInBlock;
            }

            int inodeBitmapOffset = cg.InodesUsedOffset; // cg_iusedoff (offset 0x5C in CG header)
            int actualFreeInodes = 0;
            for (int i = 0; i < (int)sb.InodesPerGroup; i++)
            {
                int byteIdx = inodeBitmapOffset + (i / 8);
                int bitIdx = i % 8;
                if (byteIdx < cg.RawData.Length && (cg.RawData[byteIdx] & (1 << bitIdx)) == 0)
                    actualFreeInodes++;
            }

            bool blockMismatch = reportedNbfree != actualFreeBlocks;
            bool fragMismatch = reportedNffree != actualFreeFrags;
            bool inodeMismatch = reportedNifree != actualFreeInodes;

            if (blockMismatch || fragMismatch || inodeMismatch)
            {
                totalErrors++;
                _log($"  CG {cgi} BITMAP MISMATCH!");
                if (blockMismatch)
                    _log($"    nbfree: reported={reportedNbfree} actual={actualFreeBlocks}");
                if (fragMismatch)
                    _log($"    nffree: reported={reportedNffree} actual={actualFreeFrags}");
                if (inodeMismatch)
                    _log($"    nifree: reported={reportedNifree} actual={actualFreeInodes}");
            }
        }

        if (totalErrors == 0)
            _log("  CG bitmap verification: ALL OK");
        else
            _log($"  CG bitmap verification: {totalErrors} CGs with mismatches!");
    }

    /// <summary>
    /// Verify all directory entries under a given inode satisfy the UFS2 DIRSIZ constraint:
    ///   d_reclen >= ((8 + d_namlen + 1 + 3) &amp; ~3)
    /// Walks the directory tree recursively. Logs violations.
    /// Returns the total number of violations found.
    /// </summary>
    public int VerifyDirectoryEntries(long dirInodeNumber, string path = "/")
    {
        var sb = _fs.Superblock!;
        int totalErrors = 0;

        Ufs2Inode dirInode;
        try { dirInode = _fs.ReadInode(dirInodeNumber); }
        catch { _log($"  DIRSIZ check: could not read inode {dirInodeNumber}"); return 0; }

        if (dirInode.FileType != Ufs2FileType.Directory) return 0;

        // Read the raw directory data (all blocks, up to di_size)
        byte[] dirData;
        try { dirData = _fs.ReadInodeData(dirInode); }
        catch { _log($"  DIRSIZ check: could not read data for inode {dirInodeNumber}"); return 0; }

        // Parse entries and validate DIRSIZ
        int offset = 0;
        var subdirs = new List<(long inode, string name)>();

        while (offset + 8 <= dirData.Length)
        {
            uint ino = BinaryPrimitives.ReadUInt32BigEndian(dirData.AsSpan(offset));
            ushort recLen = BinaryPrimitives.ReadUInt16BigEndian(dirData.AsSpan(offset + 4));
            byte fileType = dirData[offset + 6];
            byte nameLen = dirData[offset + 7];

            if (recLen == 0) break;
            if (recLen < 8 || offset + recLen > dirData.Length) break;

            if (ino != 0 && nameLen > 0)
            {
                // DIRSIZ: minimum reclen = (8 + namelen + 1 + 3) & ~3  (4-byte aligned, +1 for null terminator)
                int dirsiz = (8 + nameLen + 1 + 3) & ~3;
                if (recLen < dirsiz)
                {
                    string name = System.Text.Encoding.ASCII.GetString(dirData, offset + 8, Math.Min(nameLen, dirData.Length - offset - 8));
                    _log($"  DIRSIZ VIOLATION: {path}{name} — d_reclen={recLen} < DIRSIZ={dirsiz} (namelen={nameLen})");
                    totalErrors++;
                }

                // Collect subdirectories for recursive check (skip . and ..)
                if (fileType == 4 && nameLen > 0) // DT_DIR
                {
                    string name = System.Text.Encoding.ASCII.GetString(dirData, offset + 8, Math.Min(nameLen, dirData.Length - offset - 8));
                    if (name != "." && name != "..")
                        subdirs.Add((ino, name));
                }
            }

            offset += recLen;
        }

        // Recurse into subdirectories
        foreach (var (subIno, subName) in subdirs)
        {
            totalErrors += VerifyDirectoryEntries(subIno, path + subName + "/");
        }

        return totalErrors;
    }

    /// <summary>
    /// Execute all pending writes (for dry-run mode, call this to commit).
    /// </summary>
    public void CommitPendingWrites()
    {
        _log($"Committing {_pendingWrites.Count} pending writes...");
        foreach (var write in _pendingWrites)
        {
            int sectorSize = 512;
            long alignedStart = (write.DiskOffset / sectorSize) * sectorSize;
            long alignedEnd = ((write.DiskOffset + write.Data.Length + sectorSize - 1) / sectorSize) * sectorSize;
            int alignedLen = (int)(alignedEnd - alignedStart);
            int startDelta = (int)(write.DiskOffset - alignedStart);

            byte[] aligned = _disk.ReadBytes(alignedStart, alignedLen);
            Array.Copy(write.Data, 0, aligned, startDelta, write.Data.Length);
            _disk.WriteBytes(alignedStart, aligned);
            _log($"  Committed: {write.Description}");
        }
        _log($"All {_pendingWrites.Count} writes committed.");
        _pendingWrites.Clear();
    }
}

/// <summary>
/// Represents a pending disk write operation.
/// </summary>
public class PendingWrite
{
    public long DiskOffset { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Description { get; set; } = "";
}

/// <summary>
/// Parsed cylinder group descriptor info.
/// </summary>
public class CylinderGroupInfo
{
    public int CgNumber { get; set; }
    public long DiskOffset { get; set; }
    public byte[] RawData { get; set; } = Array.Empty<byte>();
    public uint Magic { get; set; }
    public int CgxFromDisk { get; set; }
    public int NumDataBlocks { get; set; }
    public int FreeBlocks { get; set; }
    public int FreeInodes { get; set; }
    public int InodesUsedOffset { get; set; }
    public int FreeBlocksOffset { get; set; }
    public int InodeRotor { get; set; }
    public int InitedIblk { get; set; } // cg_initediblk: number of initialized inodes
}
