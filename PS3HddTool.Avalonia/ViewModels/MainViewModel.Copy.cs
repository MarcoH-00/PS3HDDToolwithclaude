using PS3HddTool.Core.Disk;
using PS3HddTool.Core.FileSystem;

namespace PS3HddTool.Avalonia.ViewModels;

/// <summary>
/// Copy any mix of files and folders from this computer onto the PS3 drive in one
/// operation (toolbar "Copy to PS3", drag and drop). The progress bar follows the file
/// being copied, with its size, the write speed and an "N/M files" counter.
/// </summary>
public partial class MainViewModel
{
    private sealed record CopyPlanFile(string Source, string Relative, long Size);

    public Task CopyFileToPs3WithPath(string sourceFilePath) => CopyItemsToPs3Async(new[] { sourceFilePath });

    public Task CopyFolderToPs3WithPath(string sourceFolderPath) => CopyItemsToPs3Async(new[] { sourceFolderPath });

    /// <summary>Copy files and folders into the selected folder (or the folder of the selected file, or root).</summary>
    public async Task CopyItemsToPs3Async(IReadOnlyList<string> paths)
    {
        if (RejectPs4Write() || IsBusy || _fileSystem == null || paths.Count == 0) return;

        var (parentInodeNumber, destPath) = ResolveDestination(SelectedNode);
        bool dryRun = DryRunMode;

        // ── 1. Plan: list everything and validate names before writing anything ──
        var topLevel = new List<(string Path, bool IsDir)>();
        var files = new List<CopyPlanFile>();
        int folderCount = 0;
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in paths)
            {
                string path = Path.TrimEndingDirectorySeparator(raw);
                bool isDir = Directory.Exists(path);
                if (!isDir && !File.Exists(path))
                    throw new FileNotFoundException($"'{path}' does not exist or cannot be read.");
                string name = Path.GetFileName(path);
                Ufs2Writer.ValidateEntryName(name);
                if (!seen.Add(name))
                    throw new IOException($"Two selected items are both named '{name}'.");
                topLevel.Add((path, isDir));
                if (!isDir)
                {
                    files.Add(new CopyPlanFile(path, name, new FileInfo(path).Length));
                    continue;
                }
                folderCount++;
                foreach (string dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                {
                    Ufs2Writer.ValidateEntryName(Path.GetFileName(dir));
                    folderCount++;
                }
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    Ufs2Writer.ValidateEntryName(Path.GetFileName(file));
                    files.Add(new CopyPlanFile(file, Path.Combine(name, Path.GetRelativePath(path, file)), new FileInfo(file).Length));
                }
            }

            var existing = await Task.Run(() =>
            {
                var dirInode = _fileSystem.ReadInode(parentInodeNumber);
                return _fileSystem.ReadDirectory(dirInode).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            });
            var clash = topLevel.Select(t => Path.GetFileName(t.Path)).FirstOrDefault(existing.Contains);
            if (clash != null)
                throw new IOException($"'{destPath}' already contains '{clash}'. Rename or delete it first.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            StatusText = $"Copy cancelled: {ex.Message}";
            Log($"Copy cancelled before writing: {ex.Message}");
            return;
        }

        long totalBytes = files.Sum(f => f.Size);
        int totalFiles = files.Count;
        string what = topLevel.Count == 1
            ? $"'{Path.GetFileName(topLevel[0].Path)}'"
            : $"{topLevel.Count} items";

        int filesDone = 0;
        long bytesDone = 0;
        string? error = null;

        try
        {
            IsBusy = true;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            StatusText = (dryRun ? "[FAKE WRITE TEST] " : "") +
                         $"Copying {what} to {destPath} ({totalFiles} files, {FormatSize(totalBytes)})...";
            Log($"[WRITE] Copying {what} → {destPath}: {totalFiles} files, {folderCount} folders, {FormatSize(totalBytes)}");

            var startTime = DateTime.UtcNow;
            var lastUi = DateTime.MinValue;

            void ShowProgress(CopyPlanFile file, long fileBytes, bool force)
            {
                var now = DateTime.UtcNow;
                if (!force && (now - lastUi).TotalMilliseconds < 100) return;
                lastUi = now;

                double pct = file.Size > 0 ? (double)fileBytes / file.Size * 100 : 100;
                double elapsed = (now - startTime).TotalSeconds;
                long movedSoFar = bytesDone + fileBytes;
                double speed = elapsed > 0.2 ? movedSoFar / 1048576.0 / elapsed : 0;
                string eta = "";
                if (speed > 0.01 && fileBytes < file.Size)
                {
                    int sec = (int)((file.Size - fileBytes) / 1048576.0 / speed);
                    eta = sec >= 60 ? $"  ETA {sec / 60}m{sec % 60}s" : $"  ETA {sec}s";
                }
                string counter = totalFiles > 1 ? $"{filesDone}/{totalFiles} files  " : "";
                string name = totalFiles > 1 ? $"{Path.GetFileName(file.Source)}  " : "";
                string text = $"{counter}{name}{pct:F1}%  {FormatSize(fileBytes)}/{FormatSize(file.Size)}  {speed:F1} MB/s{eta}";
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ProgressValue = pct;
                    ProgressText = text;
                });
            }

            await Task.Run(() =>
            {
                var writer = new Ufs2Writer(_fileSystem, _fileSystem.DiskSource, _fileSystem.PartitionOffsetBytes,
                    dryRun, msg => Log(msg));
                writer.VerboseLog = VerboseDiagnostics;
                var byRelative = files.ToDictionary(f => f.Relative, StringComparer.Ordinal);

                void CopyOne(long dirInode, CopyPlanFile file)
                {
                    ShowProgress(file, 0, force: true);
                    using var source = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                    var progress = new ProgressStream(source, file.Size, (read, _) => ShowProgress(file, read, force: false));
                    writer.WriteFile(dirInode, Path.GetFileName(file.Source), progress, file.Size);
                    filesDone++;
                    bytesDone += file.Size;
                    ShowProgress(file, file.Size, force: true);
                }

                try
                {
                    foreach (var (path, isDir) in topLevel)
                    {
                        string name = Path.GetFileName(path);
                        if (!isDir)
                        {
                            CopyOne(parentInodeNumber, byRelative[name]);
                            continue;
                        }

                        // Breadth-first, files before sub-folders at each level (PS3 native order).
                        long rootInode = writer.CreateDirectory(parentInodeNumber, name);
                        var queue = new Queue<(string Dir, long Inode)>();
                        queue.Enqueue((path, rootInode));
                        while (queue.Count > 0)
                        {
                            var (dir, inode) = queue.Dequeue();
                            foreach (string file in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
                                CopyOne(inode, byRelative[Path.Combine(name, Path.GetRelativePath(path, file))]);
                            foreach (string sub in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
                                queue.Enqueue((sub, writer.CreateDirectory(inode, Path.GetFileName(sub))));
                        }

                        if (VerboseDiagnostics && !dryRun)
                        {
                            int dirsizErrors = writer.VerifyDirectoryEntries(rootInode, name + "/");
                            Log(dirsizErrors == 0 ? "  DIRSIZ verification: ALL OK" : $"  DIRSIZ verification: {dirsizErrors} violations!");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // The file that failed is rolled back by WriteFile; everything finished
                    // before it is kept and committed below.
                    error = ex.Message;
                    Log($"ERROR copying: {ex.Message}");
                }

                if (!dryRun)
                {
                    writer.UpdateSuperblock();
                    if (VerboseDiagnostics) writer.VerifyCgBitmaps();
                }
                else
                    Log($"[FAKE WRITE TEST] {writer.PendingWrites.Count} writes would be performed. No data written.");

                double secs = (DateTime.UtcNow - startTime).TotalSeconds;
                Log($"Copied {filesDone}/{totalFiles} files, {FormatSize(bytesDone)} in {secs:F1}s ({(secs > 0.1 ? bytesDone / 1048576.0 / secs : 0):F1} MB/s)");

                if (_diskSource is PhysicalDiskSource pds && pds.UnbufferedBytesWritten > 0)
                {
                    double mb = pds.UnbufferedBytesWritten / 1048576.0;
                    double s = pds.UnbufferedWriteMs / 1000.0;
                    Log($"  IO unbuffered: {mb:F1} MB in {pds.UnbufferedWriteMs} ms = {(s > 0 ? mb / s : 0):F1} MB/s");
                }
            });

            if (error != null)
                StatusText = $"Copy stopped after {filesDone} of {totalFiles} files: {error}";
            else
                StatusText = dryRun
                    ? $"[FAKE WRITE TEST] Would copy {what} ({totalFiles} files) — check log"
                    : $"Copied {what} to {destPath} ({totalFiles} files, {FormatSize(totalBytes)})";

            if (!dryRun)
            {
                await Task.Run(() => _fileSystem?.Mount());
                RefreshFreeSpace();
                await LoadDirectoryTreeAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            Log($"ERROR copying: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            // Clear after any queued progress updates have run.
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => { ProgressValue = 0; ProgressText = ""; });
        }
    }

    // ── Extract several PKG files ──────────────────────────────────────────────

    /// <summary>Extract several .pkg files, each into &lt;outputDir&gt;/&lt;TITLE_ID&gt;.</summary>
    public async Task ExtractPkgsAsync(IReadOnlyList<string> pkgPaths, string outputDir)
    {
        if (IsBusy || pkgPaths.Count == 0) return;
        int ok = 0;
        for (int i = 0; i < pkgPaths.Count; i++)
        {
            string prefix = pkgPaths.Count > 1 ? $"{i}/{pkgPaths.Count} PKGs  " : "";
            if (await ExtractPkgCoreAsync(pkgPaths[i], outputDir, prefix)) ok++;
            else if (pkgPaths.Count > 1) Log($"Continuing with the next PKG after an error in {Path.GetFileName(pkgPaths[i])}.");
        }
        if (pkgPaths.Count > 1)
            StatusText = ok == pkgPaths.Count
                ? $"Extracted {ok} PKGs to {outputDir}"
                : $"Extracted {ok} of {pkgPaths.Count} PKGs to {outputDir} — see the log";
    }
}
