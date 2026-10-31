using CommunityToolkit.Mvvm.ComponentModel;
using PS3HddTool.Core.FileSystem;
using PS3HddTool.Core.Models;

namespace PS3HddTool.Avalonia.ViewModels;

/// <summary>
/// Multi-select, cut / paste and drag-to-move inside the PS3 filesystem. A move never
/// copies data: each entry is linked into the destination, ".." is repointed for folders
/// and the old entry is removed (Ufs2Writer.MoveEntry), so it is fast and atomic per item.
/// </summary>
public partial class MainViewModel
{
    private readonly List<FileTreeNode> _cutNodes = new();

    [ObservableProperty] private string _clipboardText = "";
    [ObservableProperty] private bool _isMultiSelectMode;
    [ObservableProperty] private int _checkedCount;

    public bool HasClipboard => _cutNodes.Count > 0;
    public bool CanPaste => CanWriteFiles && _cutNodes.Count > 0;
    public bool CanCut => CanEditSelection && ActionTargets().All(n => n.ParentInodeNumber != 0);
    public bool HasCheckedItems => CheckedCount > 0;
    public string CheckedCountText => CheckedCount == 1 ? "1 selected" : $"{CheckedCount} selected";

    private void NotifyClipboard()
    {
        OnPropertyChanged(nameof(HasClipboard));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanCut));
    }

    // ── multi-select ─────────────────────────────────────────────────────────

    partial void OnIsMultiSelectModeChanged(bool value)
    {
        if (!value) UncheckAll();
        NotifyCapabilities();
        StatusText = value
            ? "Multi-select on: tick files and folders, then Extract, Cut or Delete them together."
            : "Multi-select off.";
    }

    partial void OnCheckedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasCheckedItems));
        OnPropertyChanged(nameof(CheckedCountText));
        NotifyCapabilities();
    }

    /// <summary>Call after a checkbox changes.</summary>
    public void RefreshCheckedCount() => CheckedCount = CheckedNodes().Count;

    /// <summary>Every ticked node among the loaded part of the tree.</summary>
    public List<FileTreeNode> CheckedNodes()
    {
        var result = new List<FileTreeNode>();
        void Walk(IEnumerable<FileTreeNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (!n.IsSelectable) continue;
                if (n.IsChecked) result.Add(n);
                if (n.IsDirectory && n.Children.Count > 0) Walk(n.Children);
            }
        }
        Walk(FileTree);
        return result;
    }

    /// <summary>
    /// The items an action applies to: the ticked items in multi-select mode (dropping any
    /// item whose folder is also ticked, so nothing is processed twice), else the selection.
    /// </summary>
    public List<FileTreeNode> ActionTargets()
    {
        if (IsMultiSelectMode)
        {
            var ticked = CheckedNodes();
            if (ticked.Count > 0)
            {
                return ticked.Where(n => !ticked.Any(other => !ReferenceEquals(other, n) && other.IsDirectory &&
                    n.FullPath.StartsWith(other.FullPath + "/", StringComparison.Ordinal))).ToList();
            }
        }
        return SelectedNode is { IsSelectable: true } ? new List<FileTreeNode> { SelectedNode } : new List<FileTreeNode>();
    }

    /// <summary>Tick every item inside the selected folder (or at the root).</summary>
    public void CheckAllInCurrentFolder()
    {
        if (!IsMultiSelectMode) IsMultiSelectMode = true;
        IEnumerable<FileTreeNode> scope = SelectedNode is { IsDirectory: true, ChildrenLoaded: true } dir
            ? dir.Children
            : FileTree;
        foreach (var n in scope)
            if (n.IsSelectable) n.IsChecked = true;
        RefreshCheckedCount();
    }

    public void UncheckAll()
    {
        void Walk(IEnumerable<FileTreeNode> nodes)
        {
            foreach (var n in nodes)
            {
                n.IsChecked = false;
                if (n.Children.Count > 0) Walk(n.Children);
            }
        }
        Walk(FileTree);
        RefreshCheckedCount();
    }

    // ── cut / paste ──────────────────────────────────────────────────────────

    /// <summary>Mark the selected (or ticked) items to be moved by the next paste.</summary>
    public void CutSelected()
    {
        if (RejectPs4Write()) return;
        var targets = ActionTargets();
        if (targets.Count == 0 || !CanWriteFiles)
        {
            StatusText = "Select a file or folder to cut.";
            return;
        }
        if (targets.Any(n => n.ParentInodeNumber == 0))
        {
            StatusText = "Root-level system directories cannot be moved.";
            return;
        }
        _cutNodes.Clear();
        _cutNodes.AddRange(targets);
        ClipboardText = targets.Count == 1 ? $"✂ {targets[0].FullPath}" : $"✂ {targets.Count} items";
        StatusText = $"Cut {DescribeItems(targets)}. Select a destination folder and paste (Ctrl+V).";
        NotifyClipboard();
    }

    public void ClearClipboard()
    {
        _cutNodes.Clear();
        ClipboardText = "";
        NotifyClipboard();
    }

    /// <summary>Move the cut items into the selected folder (or the folder of the selected file, or root).</summary>
    public async Task PasteAsync()
    {
        if (_cutNodes.Count == 0)
        {
            StatusText = "Nothing to paste. Cut a file or folder first (Ctrl+X).";
            return;
        }
        if (await MoveNodesAsync(_cutNodes.ToList(), SelectedNode))
            ClearClipboard();
    }

    private static string DescribeItems(IReadOnlyList<FileTreeNode> nodes)
        => nodes.Count == 1 ? $"'{nodes[0].FullPath}'" : $"{nodes.Count} items";

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

    public Task<bool> MoveNodeAsync(FileTreeNode node, FileTreeNode? destination)
        => MoveNodesAsync(new[] { node }, destination);

    /// <summary>Move files and folders into another folder. Returns true when at least one moved (or was simulated).</summary>
    public async Task<bool> MoveNodesAsync(IReadOnlyList<FileTreeNode> nodes, FileTreeNode? destination)
    {
        if (RejectPs4Write() || IsBusy || _fileSystem == null || nodes.Count == 0) return false;

        var movable = new List<FileTreeNode>();
        foreach (var node in nodes)
        {
            string? reason = ExplainMoveRejection(node, destination);
            if (reason == null) movable.Add(node);
            else Log($"Skipped '{node.FullPath}': {reason}");
        }
        if (movable.Count == 0)
        {
            StatusText = nodes.Count == 1 ? ExplainMoveRejection(nodes[0], destination)! : "None of the items can be moved there.";
            return false;
        }

        var (destInode, destPath) = ResolveDestination(destination);
        bool dryRun = DryRunMode;
        int done = 0;
        string? error = null;

        try
        {
            IsBusy = true;
            StatusText = $"Moving {DescribeItems(movable)} to '{destPath}'...";

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
                try
                {
                    foreach (var node in movable)
                    {
                        writer.MoveEntry(node.ParentInodeNumber, destInode, node.Name, node.Name, node.InodeNumber);
                        Log(dryRun
                            ? $"[FAKE WRITE TEST] Move '{node.FullPath}' → '{destPath}' — no data written."
                            : $"Moved '{node.FullPath}' → '{destPath}'.");
                        done++;
                    }
                }
                catch (Exception ex)
                {
                    // Each MoveEntry is complete on its own; stop here and keep what finished.
                    error = ex.Message;
                    Log($"ERROR moving: {ex.Message}\n{ex.StackTrace}");
                }
                if (!dryRun && done > 0) writer.UpdateSuperblock();
                return true;
            });

            if (!wrote)
            {
                StatusText = "Disk is not writable.";
                return false;
            }

            if (!dryRun && done > 0)
            {
                await Task.Run(() => _fileSystem?.Mount());
                RefreshFreeSpace();
                await LoadDirectoryTreeAsync();
                RefreshCheckedCount();
            }

            string prefix = dryRun ? "[DRY RUN] Would move" : "Moved";
            StatusText = error == null
                ? $"{prefix} {(done == 1 ? $"'{movable[0].Name}'" : $"{done} items")} to '{destPath}'"
                : $"Move stopped after {done} of {movable.Count}: {error}";
            return done > 0;
        }
        catch (Exception ex)
        {
            StatusText = $"Move Error: {ex.Message}";
            Log($"ERROR moving: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
        finally { IsBusy = false; }
    }

    // ── delete several items ─────────────────────────────────────────────────

    /// <summary>Delete every ticked item (or the selection). Stops at the first error.</summary>
    public async Task DeleteTargetsAsync()
    {
        if (RejectPs4Write() || IsBusy || _fileSystem == null) return;
        var targets = ActionTargets();
        if (targets.Count == 0) { StatusText = "No item selected."; return; }
        if (targets.Count == 1 && !IsMultiSelectMode) { await DeleteSelectedAsync(); return; }
        if (targets.Any(n => n.ParentInodeNumber == 0))
        {
            StatusText = "Cannot delete root-level system directories.";
            return;
        }

        bool dryRun = DryRunMode;
        int done = 0;
        string? error = null;
        try
        {
            IsBusy = true;
            StatusText = $"Deleting {targets.Count} items...";
            bool wrote = await Task.Run(() =>
            {
                var writeDisk = _fileSystem.DiskSource;
                if (!writeDisk.CanWrite) { Log("ERROR: Disk is not writable."); return false; }
                var writer = new Ufs2Writer(_fileSystem, writeDisk, _fileSystem.PartitionOffsetBytes,
                    dryRun, msg => Log(msg));
                writer.VerboseLog = VerboseDiagnostics;
                try
                {
                    foreach (var node in targets)
                    {
                        if (node.IsDirectory) writer.DeleteDirectory(node.ParentInodeNumber, node.Name, node.InodeNumber);
                        else writer.DeleteFile(node.ParentInodeNumber, node.Name, node.InodeNumber);
                        Log(dryRun ? $"[FAKE WRITE TEST] Delete '{node.FullPath}' — no data written." : $"Deleted '{node.FullPath}'.");
                        done++;
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Log($"ERROR deleting: {ex.Message}\n{ex.StackTrace}");
                }
                if (!dryRun && done > 0) writer.UpdateSuperblock();
                return true;
            });
            if (!wrote) { StatusText = "Disk is not writable."; return; }

            if (!dryRun && done > 0)
            {
                await Task.Run(() => _fileSystem?.Mount());
                RefreshFreeSpace();
                await LoadDirectoryTreeAsync();
                RefreshCheckedCount();
            }
            string prefix = dryRun ? "[DRY RUN] Would delete" : "Deleted";
            StatusText = error == null ? $"{prefix} {done} items" : $"Delete stopped after {done} of {targets.Count}: {error}";
        }
        catch (Exception ex)
        {
            StatusText = $"Delete Error: {ex.Message}";
            Log($"ERROR deleting: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    // ── extract several items ────────────────────────────────────────────────

    /// <summary>Extract every ticked item (or the selection) into <paramref name="folder"/>.</summary>
    public async Task ExtractTargetsToFolderAsync(string folder)
    {
        var targets = ActionTargets();
        int ok = 0;
        foreach (var node in targets)
        {
            string outputPath;
            try { outputPath = Ufs2FileSystem.GetExtractionPath(folder, node.Name); }
            catch (Exception ex) { Log($"Skipped '{node.FullPath}': {ex.Message}"); continue; }
            await ExtractAsync((node, outputPath));
            if (StatusText.StartsWith("Extraction complete", StringComparison.Ordinal)) ok++;
            else break;
        }
        if (targets.Count > 1)
            StatusText = ok == targets.Count
                ? $"Extracted {ok} items to {folder}"
                : $"Extracted {ok} of {targets.Count} items to {folder} — see the log";
    }
}
