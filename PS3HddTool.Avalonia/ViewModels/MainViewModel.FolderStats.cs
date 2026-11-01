using CommunityToolkit.Mvvm.ComponentModel;
using PS3HddTool.Core.FileSystem;
using PS3HddTool.Core.Models;

namespace PS3HddTool.Avalonia.ViewModels;

/// <summary>
/// Totals for the selected folder in the details panel: the size of everything inside it,
/// the space it occupies on the drive, and how many files and sub-folders it contains.
/// Computed in the background (the cached reader makes repeat visits cheap) and cancelled
/// when the selection changes.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _isFolderSelected;
    [ObservableProperty] private string _folderSizeText = "";
    [ObservableProperty] private string _folderContentsText = "";

    private int _folderStatsGeneration;

    public sealed record FolderTotals(long Files, long Folders, long Bytes, long BytesOnDisk, int Unreadable);

    private void StartFolderStats(FileTreeNode? node)
    {
        int generation = Interlocked.Increment(ref _folderStatsGeneration);
        IsFolderSelected = node is { IsDirectory: true, IsSelectable: true };
        if (!IsFolderSelected || _fileSystem == null)
        {
            FolderSizeText = "";
            FolderContentsText = "";
            return;
        }

        var fs = _fileSystem;
        long inode = node!.InodeNumber;
        FolderSizeText = "Calculating…";
        FolderContentsText = "Calculating…";
        _ = Task.Run(() =>
        {
            try
            {
                var lastUpdate = DateTime.UtcNow;
                var totals = ComputeFolderTotals(fs, inode,
                    () => generation != Volatile.Read(ref _folderStatsGeneration),
                    partial =>
                    {
                        if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds < 250) return;
                        lastUpdate = DateTime.UtcNow;
                        PostFolderStats(generation, partial, finished: false);
                    });
                if (totals != null) PostFolderStats(generation, totals, finished: true);
            }
            catch (Exception ex)
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _folderStatsGeneration) return;
                    FolderSizeText = "Unavailable";
                    FolderContentsText = $"Could not read: {ex.Message}";
                });
            }
        });
    }

    private void PostFolderStats(int generation, FolderTotals t, bool finished)
    {
        string suffix = finished ? "" : " …";
        string size = $"{FormatSize(t.Bytes)}  ({t.Bytes:N0} bytes) · {FormatSize(t.BytesOnDisk)} on disk{suffix}";
        string contents = $"{t.Files:N0} file{(t.Files == 1 ? "" : "s")}, {t.Folders:N0} folder{(t.Folders == 1 ? "" : "s")}{suffix}";
        if (finished && t.Unreadable > 0) contents += $" · {t.Unreadable} unreadable";
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (generation != _folderStatsGeneration) return;
            FolderSizeText = size;
            FolderContentsText = contents;
        });
    }

    /// <summary>
    /// Walk a folder tree and total it. Bytes = sum of file sizes; BytesOnDisk = blocks
    /// allocated to every file and sub-folder (and the folder itself). Returns null when
    /// <paramref name="cancelled"/> becomes true.
    /// </summary>
    public static FolderTotals? ComputeFolderTotals(Ufs2FileSystem fs, long rootInode,
        Func<bool>? cancelled = null, Action<FolderTotals>? progress = null)
    {
        long files = 0, folders = 0, bytes = 0, onDisk = 0;
        int unreadable = 0;
        var visited = new HashSet<long> { rootInode };
        var stack = new Stack<long>();
        stack.Push(rootInode);
        onDisk += fs.ReadInode(rootInode).Blocks * 512;

        while (stack.Count > 0)
        {
            if (cancelled?.Invoke() == true) return null;
            long dirIno = stack.Pop();
            List<Ufs2DirectoryEntry> entries;
            try { entries = fs.ReadDirectory(fs.ReadInode(dirIno)); }
            catch { unreadable++; continue; }

            foreach (var entry in entries)
            {
                if (entry.Name is "." or "..") continue;
                Ufs2Inode child;
                try { child = fs.ReadInode(entry.InodeNumber); }
                catch { unreadable++; continue; }
                onDisk += child.Blocks * 512;
                if (child.FileType == Ufs2FileType.Directory)
                {
                    folders++;
                    if (visited.Add(entry.InodeNumber)) stack.Push(entry.InodeNumber);
                }
                else
                {
                    files++;
                    bytes += Math.Max(0, child.Size);
                }
            }
            progress?.Invoke(new FolderTotals(files, folders, bytes, onDisk, unreadable));
        }
        return new FolderTotals(files, folders, bytes, onDisk, unreadable);
    }
}
