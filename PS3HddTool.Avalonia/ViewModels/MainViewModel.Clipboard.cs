using CommunityToolkit.Mvvm.ComponentModel;
using PS3HddTool.Core.FileSystem;
using PS3HddTool.Core.Models;

namespace PS3HddTool.Avalonia.ViewModels;

/// <summary>
/// Cut / paste and drag-to-move inside the PS3 filesystem. A move never copies data:
/// the entry is linked into the destination, ".." is repointed for folders and the old
/// entry is removed (Ufs2Writer.MoveEntry), so it is fast and atomic per item.
/// </summary>
public partial class MainViewModel
{
    private FileTreeNode? _cutNode;

    [ObservableProperty] private string _clipboardText = "";

    public bool HasClipboard => _cutNode != null;
    public bool CanPaste => CanWriteFiles && _cutNode != null;
    public bool CanCut => CanEditSelection && SelectedNode?.ParentInodeNumber != 0;

    private void NotifyClipboard()
    {
        OnPropertyChanged(nameof(HasClipboard));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanCut));
    }

    /// <summary>Mark the selected item to be moved by the next paste.</summary>
    public void CutSelected()
    {
        if (RejectPs4Write()) return;
        var node = SelectedNode;
        if (node == null || !CanEditSelection)
        {
            StatusText = "Select a file or folder to cut.";
            return;
        }
        if (node.ParentInodeNumber == 0)
        {
            StatusText = "Root-level system directories cannot be moved.";
            return;
        }
        _cutNode = node;
        ClipboardText = $"✂ {node.FullPath}";
        StatusText = $"Cut '{node.FullPath}'. Select a destination folder and paste (Ctrl+V).";
        NotifyClipboard();
    }

    public void ClearClipboard()
    {
        _cutNode = null;
        ClipboardText = "";
        NotifyClipboard();
    }

    /// <summary>Move the cut item into the selected folder (or the folder of the selected file, or root).</summary>
    public async Task PasteAsync()
    {
        if (_cutNode == null)
        {
            StatusText = "Nothing to paste. Cut a file or folder first (Ctrl+X).";
            return;
        }
        if (await MoveNodeAsync(_cutNode, SelectedNode))
            ClearClipboard();
    }

    /// <summary>The folder a paste or drop onto <paramref name="target"/> goes into.</summary>
    public static (long Inode, string Path) ResolveDestination(FileTreeNode? target)
    {
        if (target == null || target.InodeNumber <= 0) return (2, "/");
        if (target.IsDirectory) return (target.InodeNumber, target.FullPath);
        int slash = target.FullPath.LastIndexOf('/');
        string parentPath = slash <= 0 ? "/" : target.FullPath[..slash];
        long parentInode = target.ParentInodeNumber > 0 ? target.ParentInodeNumber : 2;
        return (parentInode, parentPath);
    }

    /// <summary>
    /// Why moving <paramref name="node"/> into <paramref name="destination"/> is not allowed,
    /// or null when it is. Used to give feedback while dragging.
    /// </summary>
    public string? ExplainMoveRejection(FileTreeNode node, FileTreeNode? destination)
    {
        if (IsPs4) return "PS4 disks are read-only.";
        if (!CanWriteFiles) return "Writing is not available right now.";
        if (node.ParentInodeNumber == 0) return "Root-level system directories cannot be moved.";
        var (destInode, destPath) = ResolveDestination(destination);
        if (destInode == node.ParentInodeNumber) return $"'{node.Name}' is already in {destPath}.";
        if (destInode == node.InodeNumber) return "A folder cannot be moved into itself.";
        if (node.IsDirectory && (destPath == node.FullPath || destPath.StartsWith(node.FullPath + "/", StringComparison.Ordinal)))
            return "A folder cannot be moved inside one of its own subfolders.";
        return null;
    }

    /// <summary>Move a file or folder into another folder. Returns true when it was moved (or simulated).</summary>
    public async Task<bool> MoveNodeAsync(FileTreeNode node, FileTreeNode? destination)
    {
        if (RejectPs4Write() || IsBusy || _fileSystem == null) return false;

        string? reason = ExplainMoveRejection(node, destination);
        if (reason != null)
        {
            StatusText = reason;
            return false;
        }

        var (destInode, destPath) = ResolveDestination(destination);
        string name = node.Name;
        string newPath = destPath == "/" ? $"/{name}" : $"{destPath}/{name}";

        try
        {
            IsBusy = true;
            StatusText = $"Moving '{node.FullPath}' to '{destPath}'...";
            bool dryRun = DryRunMode;

            bool wrote = await Task.Run(() =>
            {
                var writeDisk = _fileSystem.DiskSource;
                if (!writeDisk.CanWrite)
                {
                    Log("ERROR: Disk is not writable.");
                    return false;
                }
                var writer = new Ufs2Writer(_fileSystem, writeDisk, _fileSystem.PartitionOffsetBytes,
                    dryRun, msg => Log(msg));
                writer.VerboseLog = VerboseDiagnostics;
                writer.MoveEntry(node.ParentInodeNumber, destInode, name, name, node.InodeNumber);
                if (!dryRun) writer.UpdateSuperblock();
                return true;
            });

            if (!wrote)
            {
                StatusText = "Disk is not writable.";
                return false;
            }

            if (dryRun)
            {
                Log($"[FAKE WRITE TEST] Move '{node.FullPath}' → '{newPath}' — no data written.");
                StatusText = $"[DRY RUN] Would move '{name}' to '{destPath}'";
                return true;
            }

            Log($"Moved '{node.FullPath}' → '{newPath}'.");
            await Task.Run(() => _fileSystem?.Mount());
            RefreshFreeSpace();
            await LoadDirectoryTreeAsync();
            StatusText = $"Moved '{name}' to '{destPath}'";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Move Error: {ex.Message}";
            Log($"ERROR moving: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
        finally { IsBusy = false; }
    }
}
