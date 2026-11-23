using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ToolTipIcon = System.Windows.Forms.ToolTipIcon;

namespace TeklaBackupManager;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<EmployeeBackupConfig> _employees = [];
    private readonly string _settingsPath;
    private readonly Action? _profileGuard;
    private readonly DispatcherTimer _scheduleTimer;
    private readonly NotifyIcon? _notifyIcon;
    private readonly HashSet<string> _automaticDb1RunKeys = [];
    private readonly HashSet<string> _automaticFullRunKeys = [];
    private bool _isLoadingSettings;
    private string _language = "fr";
    private bool _isExitRequested;
    private CancellationTokenSource? _backupCancellation;

    public MainWindow() : this(null, true) { }

    // Explicit storage and optional desktop integration allow isolated verification.
    internal MainWindow(string? storageDirectory, bool desktopIntegration)
    {
        _profileGuard = desktopIntegration && Application.Current is App app ? app.EnsureProfileHeld : null;
        _profileGuard?.Invoke();
        _isLoadingSettings = true;
        InitializeComponent();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var settingsDirectory = storageDirectory ?? Path.Combine(appData, "TeklaBackupManager");
        Directory.CreateDirectory(settingsDirectory);
        _settingsPath = Path.Combine(settingsDirectory, "settings.json");
        ConfigPathText.Text = _settingsPath;
        EmployeesGrid.ItemsSource = _employees;

        LoadSettings();
        StartWithWindowsBox.IsChecked = desktopIntegration && IsStartWithWindowsEnabled();
        ApplyLanguage(_language);

        _notifyIcon = desktopIntegration ? CreateNotifyIcon() : null;
        _scheduleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _scheduleTimer.Tick += ScheduleTimer_Tick;
        if (desktopIntegration) _scheduleTimer.Start();

        Log("Application prete.");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExitRequested)
        {
            e.Cancel = true;
            Hide();
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = true;
                _notifyIcon.ShowBalloonTip(
                    2500,
                    "Tekla Backup Manager",
                    "La console reste active en arriere-plan pour les sauvegardes automatiques.",
                    ToolTipIcon.Info);
            }
            return;
        }

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        base.OnClosing(e);
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        SelectFolder(SourcePathBox.Text, "Selectionner le dossier TeklaStructuresModels", path => SourcePathBox.Text = path);
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        SelectFolder(DestinationPathBox.Text, "Selectionner le dossier serveur", path => DestinationPathBox.Text = path);
    }

    private void Analyze_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var requests = BuildRequests(BackupMode.Db1Timestamped);
            long bytes = 0;
            var fileCount = 0;

            foreach (var request in requests)
            {
                var files = EnumerateFiles(request.SourcePath, request.ExcludeTemporaryFiles, request.Mode).ToList();
                var employeeBytes = files.Sum(file => new FileInfo(file).Length);
                bytes += employeeBytes;
                fileCount += files.Count;
                request.Employee.LastStatus = $"{files.Count:N0} fichier(s)";
            }

            Log($"Analyse DB1 terminee: {fileCount:N0} fichier(s), {FormatBytes(bytes)} a sauvegarder pour {requests.Count} salarie(s).");
            StatusText.Text = "Analyse OK";
            ProgressText.Text = $"DB1: {requests.Count} salarie(s), {fileCount:N0} fichier(s), {FormatBytes(bytes)}";
            BackupProgress.Value = 0;
            SaveSettings();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void BackupDb1_Click(object sender, RoutedEventArgs e)
    {
        await RunBackupForModeAsync(BackupMode.Db1Timestamped, Db1BackupButton);
    }

    private async void BackupFull_Click(object sender, RoutedEventArgs e)
    {
        await RunBackupForModeAsync(BackupMode.FullMirror, FullBackupButton);
    }

    private async Task RunBackupForModeAsync(
        BackupMode mode,
        System.Windows.Controls.Button activeButton,
        IEnumerable<EmployeeBackupConfig>? employees = null)
    {
        if (_backupCancellation is not null)
        {
            _backupCancellation.Cancel();
            return;
        }

        try
        {
            _profileGuard?.Invoke();
            var requests = BuildRequests(mode, employees);
            var schedule = BuildSchedule();
            SaveSettings();

            _backupCancellation = new CancellationTokenSource();
            SetBackupButtons(activeButton);
            StatusText.Text = "Sauvegarde en cours";
            BackupProgress.Value = 0;
            Log($"{GetModeLabel(mode)} lancee.");

            var results = new List<BackupResult>();
            var errors = new List<string>();
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                try
                {
                    if (index > 0 && schedule.StaggerMinutes > 0)
                    {
                        request.Employee.LastStatus = $"Attente {schedule.StaggerMinutes} min";
                        Log($"Attente de {schedule.StaggerMinutes} minute(s) avant {request.EmployeeName} pour lisser la charge serveur.");
                        await Task.Delay(TimeSpan.FromMinutes(schedule.StaggerMinutes), _backupCancellation.Token);
                    }

                    request.Employee.LastStatus = "En cours";
                    Log($"{GetModeLabel(mode)} de {request.EmployeeName} depuis {request.MachineName}.");

                    var progress = new Progress<BackupProgressInfo>(info => UpdateProgress(info, request.EmployeeName, index, requests.Count));
                    var result = await Task.Run(() => RunBackup(request, progress, _backupCancellation.Token));
                    results.Add(result);
                    request.Employee.LastStatus = "OK";
                    Log($"Sauvegarde terminee pour {request.EmployeeName}: {result.TargetPath}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    request.Employee.LastStatus = "Erreur";
                    errors.Add($"{request.EmployeeName}: {ex.Message}");
                    Log($"Erreur pour {request.EmployeeName}: {ex.Message}");
                }
            }

            BackupProgress.Value = 100;
            StatusText.Text = errors.Count == 0 ? "Sauvegarde terminee" : "Terminee avec erreurs";
            ProgressText.Text = $"Termine: {results.Count} OK, {errors.Count} erreur(s), {results.Sum(result => result.FileCount):N0} fichier(s), {FormatBytes(results.Sum(result => result.TotalBytes))}";
            Log($"{GetModeLabel(mode)} terminee.");

            MessageBox.Show(
                errors.Count == 0
                    ? $"{GetModeLabel(mode)} terminee pour {results.Count} salarie(s)."
                    : $"{GetModeLabel(mode)} terminee avec {results.Count} OK et {errors.Count} erreur(s).\n\n{string.Join(Environment.NewLine, errors.Take(8))}",
                "Tekla Backup Manager",
                MessageBoxButton.OK,
                errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Sauvegarde annulee";
            ProgressText.Text = "Sauvegarde annulee par l'utilisateur.";
            Log("Sauvegarde annulee.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _backupCancellation?.Dispose();
            _backupCancellation = null;
            ResetBackupButtons();
        }
    }

    private async void ScheduleTimer_Tick(object? sender, EventArgs e)
    {
        try { _profileGuard?.Invoke(); }
        catch (InvalidOperationException ex)
        {
            _scheduleTimer.Stop();
            _backupCancellation?.Cancel();
            ShowError(ex.Message);
            return;
        }
        if (_backupCancellation is not null)
        {
            return;
        }

        var now = DateTime.Now;
        var currentTime = now.TimeOfDay;

        var db1DueEmployees = _employees
            .Where(employee => employee.IsEnabled)
            .Where(employee => IsBusinessDay(now.DayOfWeek))
            .Where(employee => IsEmployeeDue(employee, BackupMode.Db1Timestamped, currentTime, now.Date))
            .ToList();

        if (db1DueEmployees.Count > 0)
        {
            foreach (var employee in db1DueEmployees)
            {
                _automaticDb1RunKeys.Add(BuildAutomaticRunKey(employee, BackupMode.Db1Timestamped, now.Date));
            }

            Log($"Declenchement automatique DB1 pour {db1DueEmployees.Count} salarie(s).");
            await RunBackupForModeAsync(BackupMode.Db1Timestamped, Db1BackupButton, db1DueEmployees);
            return;
        }

        var fullDueEmployees = _employees
            .Where(employee => employee.IsEnabled)
            .Where(employee => now.DayOfWeek == DayOfWeek.Friday)
            .Where(employee => IsEmployeeDue(employee, BackupMode.FullMirror, currentTime, now.Date))
            .ToList();

        if (fullDueEmployees.Count > 0)
        {
            foreach (var employee in fullDueEmployees)
            {
                _automaticFullRunKeys.Add(BuildAutomaticRunKey(employee, BackupMode.FullMirror, now.Date));
            }

            Log($"Declenchement automatique sauvegarde complete pour {fullDueEmployees.Count} salarie(s).");
            await RunBackupForModeAsync(BackupMode.FullMirror, FullBackupButton, fullDueEmployees);
        }
    }

    private List<BackupRequest> BuildRequests(BackupMode mode, IEnumerable<EmployeeBackupConfig>? employees = null)
    {
        var activeEmployees = (employees ?? _employees).Where(employee => employee.IsEnabled).ToList();

        if (activeEmployees.Count == 0)
        {
            throw new InvalidOperationException("Ajoute ou coche au moins un salarie.");
        }

        // Validate the complete configuration even when the scheduler selects only one user.
        var allEmployees = _employees.Concat(activeEmployees).Distinct().ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var employee in allEmployees)
        {
            var folder = SanitizeFolderName(employee.EmployeeName);
            var target = Path.GetFullPath(Path.Combine(DestinationPathBox.Text.Trim(), "TEKLASTRUCTUREMODEL", folder));
            if (!seen.Add(target))
                throw new InvalidOperationException("Duplicate user backup destination. Each configured user must have a distinct Windows folder name.");
        }
        return activeEmployees.Select(employee => BuildRequest(employee, mode)).ToList();
    }

    private BackupRequest BuildRequest(EmployeeBackupConfig employee, BackupMode mode)
    {
        var sourcePath = employee.SourcePath.Trim();
        var destinationRoot = DestinationPathBox.Text.Trim();
        var employeeName = SanitizeFolderName(employee.EmployeeName);
        var machineName = employee.MachineName.Trim();

        if (string.IsNullOrWhiteSpace(employeeName))
        {
            throw new InvalidOperationException("Indique le nom de l'employe.");
        }

        if (string.IsNullOrWhiteSpace(machineName))
        {
            throw new InvalidOperationException($"Indique le nom de machine pour {employeeName}.");
        }

        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            throw new InvalidOperationException("Indique un dossier serveur de destination.");
        }

        if (!int.TryParse(RetentionDaysBox.Text.Trim(), out var retentionDays) || retentionDays < 1)
        {
            throw new InvalidOperationException("La retention doit etre un nombre de jours superieur a 0.");
        }

        _ = ParseTime(employee.DailyDb1Time, $"DB1 time for {employeeName}");
        _ = ParseTime(employee.WeeklyFullTime, $"full backup time for {employeeName}");

        var safeEmployeeName = SanitizeFolderName(employeeName).ToUpperInvariant();
        var targetPath = BuildTargetPath(destinationRoot, safeEmployeeName, mode);

        if (mode == BackupMode.Db1Timestamped)
        {
            targetPath = Path.Combine(targetPath, DateTime.Now.ToString("yyyy-dd-MM"));
        }

        return new BackupRequest(
            employee,
            safeEmployeeName,
            machineName,
            sourcePath,
            destinationRoot,
            targetPath,
            CommentBox.Text.Trim(),
            mode == BackupMode.FullMirror ? false : ExcludeTempFilesBox.IsChecked == true,
            mode,
            EnableRetentionBox.IsChecked == true,
            retentionDays);
    }

    private static string NormalizeBackupPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path != path.Trim() ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new InvalidOperationException("Ambiguous/device backup paths are not supported.");
        foreach (var rawPart in path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            if (rawPart is not "." and not ".." && (rawPart.EndsWith('.') || rawPart.EndsWith(' ')))
                throw new InvalidOperationException("Ambiguous Windows path spelling is not allowed.");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new InvalidOperationException("Device path aliases are not supported.");
        var root = Path.GetPathRoot(full)!;
        foreach (var part in full[root.Length..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            if (part.EndsWith('.') || part.EndsWith(' '))
                throw new InvalidOperationException("Windows path components ending in dot or space are not allowed.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        path = NormalizeBackupPath(path);
        root = NormalizeBackupPath(root);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateBackupPaths(BackupRequest request)
    {
        if (!Path.IsPathFullyQualified(request.SourcePath) || !Path.IsPathFullyQualified(request.DestinationRoot))
            throw new InvalidOperationException("Source and destination must be absolute paths.");
        var source = NormalizeBackupPath(request.SourcePath);
        var destination = NormalizeBackupPath(request.DestinationRoot);
        EnsureNoReparseTree(source);
        EnsureNoReparseTree(destination);
        source = CanonicalPaths.Resolve(source);
        destination = CanonicalPaths.Resolve(destination);
        if (IsSameOrDescendant(source, destination) || IsSameOrDescendant(destination, source))
            throw new InvalidOperationException("Source and backup root must not overlap.");
        if (request.EmployeeName != SanitizeFolderName(request.EmployeeName) || request.RetentionDays < 1)
            throw new InvalidOperationException("Invalid backup user or retention.");
        var expected = BuildTargetPath(destination, request.EmployeeName, request.Mode);
        if (request.Mode == BackupMode.Db1Timestamped)
            expected = Path.Combine(expected, DateTime.Now.ToString("yyyy-dd-MM"));
        if (!CanonicalPaths.Resolve(NormalizeBackupPath(request.TargetPath)).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid backup target.");
        EnsureNoReparseTree(source);
        EnsureNoReparseTree(destination);
    }

    private static void EnsureNoReparseTree(string root)
    {
        // Check ancestors too: an ordinary-looking child may be below a junction.
        for (var parent = new DirectoryInfo(Path.GetFullPath(root)); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse points are not supported in backup paths.");
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Reparse points are not supported in backup trees.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
    }

    private static BackupResult RunBackup(
        BackupRequest request,
        IProgress<BackupProgressInfo> progress,
        CancellationToken cancellationToken)
    {
        ValidateBackupPaths(request);
        if (!Directory.Exists(request.SourcePath))
        {
            throw new DirectoryNotFoundException($"Source folder was not found: {request.SourcePath}");
        }

        request = request with
        {
            SourcePath = CanonicalPaths.Resolve(request.SourcePath),
            DestinationRoot = CanonicalPaths.Resolve(request.DestinationRoot),
            TargetPath = CanonicalPaths.Resolve(request.TargetPath)
        };
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = ExclusiveLease.Acquire(request.DestinationRoot, ".tekla-backup.lock");
        DestinationOwnership.Claim(request, lease);
        if (request.Mode == BackupMode.FullMirror)
        {
            return RunMirrorBackup(request, progress, cancellationToken, lease);
        }

        var files = EnumerateFiles(request.SourcePath, request.ExcludeTemporaryFiles, request.Mode).ToList();
        if (files.Count == 0)
            throw new InvalidOperationException("No DB1 files selected; backup and retention refused to preserve existing history.");
        var totalBytes = files.Sum(file => new FileInfo(file).Length);
        long copiedBytes = 0;
        var copiedFiles = 0;

        lease.EnsureHeld();
        Directory.CreateDirectory(request.TargetPath);

        foreach (var sourceFile in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lease.EnsureHeld();

            var relativePath = Path.GetRelativePath(request.SourcePath, sourceFile);
            var targetFile = Path.Combine(request.TargetPath, relativePath);
            var targetDirectory = Path.GetDirectoryName(targetFile);

            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            lease.EnsureHeld();
            CopyFileSafely(sourceFile, targetFile, lease, cancellationToken);
            lease.EnsureHeld();
            var fileBytes = new FileInfo(sourceFile).Length;
            copiedBytes += fileBytes;
            copiedFiles++;

            progress.Report(new BackupProgressInfo(copiedFiles, files.Count, copiedBytes, totalBytes, relativePath));
        }

        lease.EnsureHeld();
        WriteManifest(request, files.Count, totalBytes, lease);

        if (request.EnableRetention && request.Mode != BackupMode.FullMirror)
        {
            PurgeOldBackups(BuildTargetPath(request.DestinationRoot, request.EmployeeName, request.Mode), request.RetentionDays, lease);
        }

        return new BackupResult(request.TargetPath, files.Count, totalBytes);
    }

    private static BackupResult RunMirrorBackup(
        BackupRequest request,
        IProgress<BackupProgressInfo> progress,
        CancellationToken cancellationToken,
        ExclusiveLease lease)
    {
        var sourceFiles = EnumerateFiles(request.SourcePath, excludeTemporaryFiles: false, request.Mode).ToList();
        if (sourceFiles.Count == 0)
            throw new InvalidOperationException("Empty source: full mirror refused to preserve the existing backup.");
        var totalBytes = sourceFiles.Sum(file => new FileInfo(file).Length);
        var sourceRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long copiedBytes = 0;
        var processedFiles = 0;

        lease.EnsureHeld();
        Directory.CreateDirectory(request.TargetPath);

        foreach (var sourceFile in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lease.EnsureHeld();

            var relativePath = Path.GetRelativePath(request.SourcePath, sourceFile);
            sourceRelativePaths.Add(relativePath);

            var targetFile = Path.Combine(request.TargetPath, relativePath);
            var targetDirectory = Path.GetDirectoryName(targetFile);

            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            var sourceInfo = new FileInfo(sourceFile);
            if (ShouldCopyFile(sourceInfo, targetFile))
            {
                lease.EnsureHeld();
                CopyFileSafely(sourceFile, targetFile, lease, cancellationToken);
                lease.EnsureHeld();
                File.SetLastWriteTimeUtc(targetFile, sourceInfo.LastWriteTimeUtc);
            }

            copiedBytes += sourceInfo.Length;
            processedFiles++;
            progress.Report(new BackupProgressInfo(processedFiles, sourceFiles.Count, copiedBytes, totalBytes, relativePath));
        }

        EnsureNoReparseTree(request.SourcePath);
        EnsureNoReparseTree(request.TargetPath);
        if (!Directory.Exists(request.SourcePath) || sourceFiles.Any(file => !File.Exists(file)))
            throw new IOException("Source changed or became unavailable; deletion refused.");
        DeleteFilesMissingFromSource(request.TargetPath, sourceRelativePaths, cancellationToken, lease);
        DeleteEmptyDirectories(request.TargetPath, cancellationToken, lease);
        lease.EnsureHeld();
        WriteManifest(request, sourceFiles.Count, totalBytes, lease);

        return new BackupResult(request.TargetPath, sourceFiles.Count, totalBytes);
    }

    private static void CopyFileSafely(string sourceFile, string targetFile, ExclusiveLease lease, CancellationToken token)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(targetFile)!, ".tekla-copy-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            token.ThrowIfCancellationRequested();
            lease.EnsureHeld();
            var initial = new FileInfo(sourceFile);
            var length = initial.Length;
            var modified = initial.LastWriteTimeUtc;
            using (var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1024 * 1024];
                int count;
                while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    lease.EnsureHeld();
                    output.Write(buffer, 0, count);
                }
                output.Flush(flushToDisk: true);
            }
            var final = new FileInfo(sourceFile);
            if (final.Length != length || final.LastWriteTimeUtc != modified)
                throw new IOException("Source changed during copy; existing backup preserved.");
            File.SetLastWriteTimeUtc(temporary, modified);
            token.ThrowIfCancellationRequested();
            lease.EnsureHeld();
            File.Move(temporary, targetFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                try { lease.EnsureHeld(); File.Delete(temporary); }
                catch (InvalidOperationException) { /* Preserve orphaned staging file if lease was lost. */ }
        }
    }

    private static bool ShouldCopyFile(FileInfo sourceInfo, string targetFile)
    {
        if (!File.Exists(targetFile) || new FileInfo(targetFile).Length != sourceInfo.Length) return true;
        using var source = new FileStream(sourceInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var target = new FileStream(targetFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        return !System.Security.Cryptography.SHA256.HashData(source).AsSpan().SequenceEqual(
            System.Security.Cryptography.SHA256.HashData(target));
    }

    private static void DeleteFilesMissingFromSource(
        string targetRoot,
        HashSet<string> sourceRelativePaths,
        CancellationToken cancellationToken,
        ExclusiveLease lease)
    {
        foreach (var targetFile in Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lease.EnsureHeld();

            var relativePath = Path.GetRelativePath(targetRoot, targetFile);
            if (!sourceRelativePaths.Contains(relativePath))
            {
                File.Delete(targetFile);
            }
        }
    }

    private static void DeleteEmptyDirectories(string targetRoot, CancellationToken cancellationToken, ExclusiveLease lease)
    {
        foreach (var directory in Directory.EnumerateDirectories(targetRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lease.EnsureHeld();

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string sourcePath, bool excludeTemporaryFiles, BackupMode mode)
    {
        var excludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".backup",
            ".cache",
            "cache",
            "logs",
            "tmp",
            "temp"
        };

        var excludedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".tmp",
            ".bak",
            ".log",
            ".lck",
            ".lock"
        };

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            if (mode == BackupMode.Db1Timestamped && !Path.GetExtension(file).Equals(".db1", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!excludeTemporaryFiles)
            {
                yield return file;
                continue;
            }

            var relativeParts = Path.GetRelativePath(sourcePath, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (relativeParts.Any(part => excludedDirectories.Contains(part)))
            {
                continue;
            }

            if (excludedExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            yield return file;
        }
    }

    private static void PurgeOldBackups(string projectBackupRoot, int retentionDays, ExclusiveLease? lease)
    {
        if (!Directory.Exists(projectBackupRoot))
        {
            return;
        }

        using var ownLease = lease is null ? ExclusiveLease.Acquire(projectBackupRoot, ".purge-test.lock") : null;
        var guard = lease ?? ownLease!;
        var cutoff = DateTime.Now.AddDays(-retentionDays);

        foreach (var directory in Directory.EnumerateDirectories(projectBackupRoot))
        {
            var info = new DirectoryInfo(directory);
            if (DateTime.TryParseExact(info.Name, "yyyy-dd-MM", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var backupDate)
                && backupDate < cutoff.Date && info.CreationTime < cutoff && info.LastWriteTime < cutoff)
            {
                EnsureNoReparseTree(info.FullName);
                foreach (var file in Directory.EnumerateFiles(info.FullName, "*", SearchOption.AllDirectories))
                {
                    guard.EnsureHeld();
                    File.Delete(file);
                }
                foreach (var directoryToDelete in Directory.EnumerateDirectories(info.FullName, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                {
                    guard.EnsureHeld();
                    Directory.Delete(directoryToDelete);
                }
                guard.EnsureHeld();
                info.Delete();
            }
        }
    }

    private static void WriteManifest(BackupRequest request, int fileCount, long totalBytes, ExclusiveLease lease)
    {
        var manifest = new StringBuilder()
            .AppendLine("Tekla Backup Manager")
            .AppendLine($"Employee: {request.EmployeeName}")
            .AppendLine($"Source machine: {request.MachineName}")
            .AppendLine($"Source: {request.SourcePath}")
            .AppendLine($"Destination: {request.TargetPath}")
            .AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Files: {fileCount}")
            .AppendLine($"Size: {FormatBytes(totalBytes)}")
            .AppendLine($"Mode: {request.Mode}")
            .AppendLine($"Temporary files excluded: {request.ExcludeTemporaryFiles}")
            .AppendLine($"Retention enabled: {request.EnableRetention}")
            .AppendLine($"Retention days: {request.RetentionDays}")
            .AppendLine()
            .AppendLine("Comment:")
            .AppendLine(string.IsNullOrWhiteSpace(request.Comment) ? "-" : request.Comment)
            .ToString();

        var manifestRoot = Path.Combine(request.DestinationRoot, ".tekla-backup-manifests", request.EmployeeName, request.Mode.ToString());
        lease.EnsureHeld();
        Directory.CreateDirectory(manifestRoot);
        var path = Path.Combine(manifestRoot, DateTime.Now.ToString("yyyy-dd-MM") + ".txt");
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        lease.EnsureHeld();
        File.WriteAllText(staged, manifest, Encoding.UTF8);
        lease.EnsureHeld();
        File.Move(staged, path, overwrite: true);
    }

    private void UpdateProgress(BackupProgressInfo info, string employeeName, int employeeIndex, int employeeCount)
    {
        var percent = info.TotalBytes == 0 ? 100 : Math.Round(info.CopiedBytes * 100d / info.TotalBytes, 1);
        var globalPercent = ((employeeIndex + percent / 100d) / employeeCount) * 100d;
        BackupProgress.Value = globalPercent;
        ProgressText.Text = $"{employeeName} - {percent:0.0}% - {info.CopiedFiles:N0}/{info.TotalFiles:N0} fichier(s) - {info.CurrentFile}";
    }

    private void SelectFolder(string currentPath, string title, Action<string> setPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (Directory.Exists(currentPath))
        {
            dialog.InitialDirectory = currentPath;
        }

        if (dialog.ShowDialog(this) == true)
        {
            setPath(dialog.FolderName);
        }
    }

    private void LoadSettings()
    {
        _isLoadingSettings = true;
        if (!File.Exists(_settingsPath))
        {
            EmployeeNameBox.Text = Environment.UserName.ToUpperInvariant();
            MachineNameBox.Text = Environment.MachineName;
            SourcePathBox.Text = BuildDefaultSharePath(Environment.MachineName);
            DestinationPathBox.Text = string.Empty;
            AddDefaultEmployees();
            SetLanguageSelection("fr");
            _language = "fr";
            _isLoadingSettings = false;
            UpdateScheduleSummary();
            return;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath));
            _language = string.IsNullOrWhiteSpace(settings?.Language) ? "fr" : settings.Language;
            SetLanguageSelection(_language);
            EmployeeNameBox.Text = settings?.EmployeeName ?? Environment.UserName.ToUpperInvariant();
            MachineNameBox.Text = settings?.MachineName ?? Environment.MachineName;
            SourcePathBox.Text = settings?.SourcePath ?? BuildDefaultSharePath(MachineNameBox.Text);
            DestinationPathBox.Text = settings?.DestinationPath ?? string.Empty;
            ExcludeTempFilesBox.IsChecked = settings?.ExcludeTemporaryFiles ?? true;
            EnableRetentionBox.IsChecked = settings?.EnableRetention ?? true;
            RetentionDaysBox.Text = (settings?.RetentionDays ?? 30).ToString();
            DailyDb1TimeBox.Text = settings?.DailyDb1Time ?? "12:15";
            WeeklyFullTimeBox.Text = settings?.WeeklyFullTime ?? "12:30";
            StaggerMinutesBox.Text = (settings?.StaggerMinutes ?? 3).ToString();

            _employees.Clear();
            foreach (var employee in settings?.Employees ?? [])
            {
                _employees.Add(employee);
            }

            if (_employees.Count == 0 && !string.IsNullOrWhiteSpace(settings?.EmployeeName))
            {
                _employees.Add(new EmployeeBackupConfig
                {
                    EmployeeName = settings.EmployeeName,
                    MachineName = settings.MachineName,
                    SourcePath = settings.SourcePath,
                    DailyDb1Time = settings.DailyDb1Time,
                    WeeklyFullTime = settings.WeeklyFullTime,
                    IsEnabled = true
                });
            }

            if (_employees.Count == 0)
            {
                AddDefaultEmployees();
            }

            UpdateScheduleSummary();
        }
        catch (Exception ex)
        {
            Log($"Impossible de lire la configuration: {ex.Message}");
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void SaveSettings()
    {
        _profileGuard?.Invoke();
        var settings = new AppSettings
        {
            EmployeeName = EmployeeNameBox.Text.Trim(),
            MachineName = MachineNameBox.Text.Trim(),
            SourcePath = SourcePathBox.Text.Trim(),
            DestinationPath = DestinationPathBox.Text.Trim(),
            ExcludeTemporaryFiles = ExcludeTempFilesBox.IsChecked == true,
            EnableRetention = EnableRetentionBox.IsChecked == true,
            RetentionDays = int.TryParse(RetentionDaysBox.Text.Trim(), out var retentionDays) ? retentionDays : 30,
            DailyDb1Time = DailyDb1TimeBox.Text.Trim(),
            WeeklyFullDay = DayOfWeek.Friday,
            WeeklyFullTime = WeeklyFullTimeBox.Text.Trim(),
            StaggerMinutes = int.TryParse(StaggerMinutesBox.Text.Trim(), out var staggerMinutes) ? staggerMinutes : 3,
            Language = _language,
            Employees = _employees.ToList()
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
        UpdateScheduleSummary();
    }

    private void SaveConfiguration_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        Log("Parametrage sauvegarde.");
        MessageBox.Show(
            $"Parametrage sauvegarde.\n\nFichier:\n{_settingsPath}",
            "Tekla Backup Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStartWithWindows(StartWithWindowsBox.IsChecked == true);
            SaveSettings();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            StartWithWindowsBox.IsChecked = IsStartWithWindowsEnabled();
        }
    }

    private void ShowError(string message)
    {
        StatusText.Text = "Erreur";
        Log($"Erreur: {message}");
        MessageBox.Show(message, "Tekla Backup Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private NotifyIcon CreateNotifyIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Ouvrir", null, (_, _) => ShowFromTray());
        menu.Items.Add("Quitter", null, (_, _) => ExitApplication());

        var icon = new NotifyIcon
        {
            Text = "Tekla Backup Manager",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };

        icon.DoubleClick += (_, _) => ShowFromTray();
        return icon;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExitRequested = true;
        _scheduleTimer.Stop();
        _backupCancellation?.Cancel();
        Close();
        Application.Current.Shutdown();
    }

    private static string GetStartupCommandPath()
    {
        var startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return Path.Combine(startupDirectory, "TeklaBackupManager.cmd");
    }

    private static bool IsStartWithWindowsEnabled()
    {
        return File.Exists(GetStartupCommandPath());
    }

    private static void SetStartWithWindows(bool enabled)
    {
        var startupCommandPath = GetStartupCommandPath();

        if (!enabled)
        {
            if (File.Exists(startupCommandPath))
            {
                File.Delete(startupCommandPath);
            }

            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Impossible de trouver le chemin de l'application.");
        }

        var command = $"@echo off{Environment.NewLine}start \"\" \"{executablePath}\"{Environment.NewLine}";
        File.WriteAllText(startupCommandPath, command, Encoding.ASCII);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["o", "Ko", "Mo", "Go", "To"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    private static string BuildTargetPath(string destinationRoot, string employeeName, BackupMode mode)
    {
        return mode switch
        {
            BackupMode.Db1Timestamped => Path.Combine(destinationRoot, "BACKUPDB1", employeeName),
            BackupMode.FullMirror => Path.Combine(destinationRoot, "TEKLASTRUCTUREMODEL", employeeName),
            BackupMode.FullTimestamped => Path.Combine(destinationRoot, "TEKLASTRUCTUREMODEL", employeeName),
            _ => destinationRoot
        };
    }

    private static string BuildDefaultSharePath(string machineName)
    {
        return string.IsNullOrWhiteSpace(machineName)
            ? string.Empty
            : $@"\\{machineName.Trim()}\TeklaStructuresModels";
    }

    private static string SanitizeFolderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.EndsWith('.') ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Length > 100)
            throw new InvalidOperationException("Invalid Windows user folder name; characters are never silently replaced.");
        var normalized = value.Normalize(System.Text.NormalizationForm.FormC);
        var stem = normalized.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9¹²³]$"))
            throw new InvalidOperationException("Reserved Windows user folder name.");
        return normalized;
    }

    private BackupSchedule BuildSchedule()
    {
        var dailyDb1Time = ParseTime(DailyDb1TimeBox.Text.Trim(), "DB1 time");
        var weeklyFullTime = ParseTime(WeeklyFullTimeBox.Text.Trim(), "full backup time");

        if (!int.TryParse(StaggerMinutesBox.Text.Trim(), out var staggerMinutes) || staggerMinutes < 0)
        {
            throw new InvalidOperationException("Le decalage entre salaries doit etre un nombre de minutes positif ou egal a 0.");
        }

        return new BackupSchedule(dailyDb1Time, DayOfWeek.Friday, weeklyFullTime, staggerMinutes);
    }

    private static TimeSpan ParseTime(string value, string label)
    {
        if (!TimeSpan.TryParseExact(value, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var time)
            || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException($"{label} must use HH:mm format, for example 12:15.");
        }

        return time;
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.ToString(@"hh\:mm");
    }

    private bool IsEmployeeDue(EmployeeBackupConfig employee, BackupMode mode, TimeSpan currentTime, DateTime date)
    {
        var configuredTime = mode == BackupMode.Db1Timestamped
            ? employee.DailyDb1Time
            : employee.WeeklyFullTime;

        if (!TimeSpan.TryParseExact(configuredTime, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var scheduledTime)
            || currentTime < scheduledTime)
        {
            return false;
        }

        var key = BuildAutomaticRunKey(employee, mode, date);
        return mode == BackupMode.Db1Timestamped
            ? !_automaticDb1RunKeys.Contains(key)
            : !_automaticFullRunKeys.Contains(key);
    }

    private static string BuildAutomaticRunKey(EmployeeBackupConfig employee, BackupMode mode, DateTime date)
    {
        return $"{date:yyyy-MM-dd}|{mode}|{employee.EmployeeName}|{employee.MachineName}|{employee.SourcePath}";
    }

    private void Schedule_Changed(object sender, RoutedEventArgs e)
    {
        UpdateScheduleSummary();
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || LanguageBox.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        _language = selected.Tag?.ToString() == "en" ? "en" : "fr";
        ApplyLanguage(_language);
        SaveSettings();
    }

    private void UpdateScheduleSummary()
    {
        if (ScheduleSummaryText is null)
        {
            return;
        }

        ScheduleSummaryText.Text = _language == "en"
            ? $"DB1 workdays Monday-Friday: {DailyDb1TimeBox.Text.Trim()} | Full Friday: {WeeklyFullTimeBox.Text.Trim()} | Stagger: {StaggerMinutesBox.Text.Trim()} min between users"
            : $"DB1 jours ouvres lundi-vendredi : {DailyDb1TimeBox.Text.Trim()} | Complet vendredi : {WeeklyFullTimeBox.Text.Trim()} | Decalage : {StaggerMinutesBox.Text.Trim()} min entre salaries";
    }

    private static string GetModeLabel(BackupMode mode)
    {
        return mode == BackupMode.Db1Timestamped
            ? "Sauvegarde DB1"
            : "Sauvegarde complete";
    }

    private void SetLanguageSelection(string language)
    {
        if (LanguageBox is null)
        {
            return;
        }

        foreach (var item in LanguageBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == language)
            {
                LanguageBox.SelectedItem = item;
                return;
            }
        }

        LanguageBox.SelectedIndex = 0;
    }

    private void ApplyLanguage(string language)
    {
        _language = language == "en" ? "en" : "fr";
        SetLanguageSelection(_language);
        ApplyTranslations(this);
        ApplyDataGridTranslations();
        UpdateScheduleSummary();
    }

    private void ApplyTranslations(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            switch (child)
            {
                case TextBlock textBlock when !string.IsNullOrWhiteSpace(textBlock.Text):
                    textBlock.Text = Translate(textBlock.Text);
                    break;
                case System.Windows.Controls.Button button when button.Content is string buttonText:
                    button.Content = Translate(buttonText);
                    break;
                case System.Windows.Controls.CheckBox checkBox when checkBox.Content is string checkBoxText:
                    checkBox.Content = Translate(checkBoxText);
                    break;
                case System.Windows.Controls.TextBox textBox when textBox.IsReadOnly && !string.IsNullOrWhiteSpace(textBox.Text):
                    textBox.Text = Translate(textBox.Text);
                    break;
            }

            ApplyTranslations(child);
        }
    }

    private void ApplyDataGridTranslations()
    {
        if (EmployeesGrid is null)
        {
            return;
        }

        foreach (var column in EmployeesGrid.Columns)
        {
            if (column.Header is string header)
            {
                column.Header = Translate(header);
            }
        }
    }

    private string Translate(string value)
    {
        foreach (var (_, fr, en) in TranslationEntries)
        {
            if (string.Equals(value, fr, StringComparison.Ordinal) ||
                string.Equals(value, en, StringComparison.Ordinal))
            {
                return _language == "en" ? en : fr;
            }
        }

        return value;
    }

    private static readonly (string Key, string Fr, string En)[] TranslationEntries =
    [
        ("language", "Langue", "Language"),
        ("subtitle", "Console centralisee de sauvegarde des dossiers de modeles Tekla Structures", "Centralized backup console for Tekla Structures model folders"),
        ("ready", "Pret", "Ready"),
        ("backup", "Sauvegarde", "Backup"),
        ("users", "Postes a sauvegarder", "Users to back up"),
        ("active", "Actif", "Active"),
        ("user", "Utilisateur", "User"),
        ("machine", "Machine", "Machine"),
        ("source", "Source", "Source"),
        ("db1_time", "Heure DB1", "DB1 time"),
        ("full_time", "Heure complet", "Full time"),
        ("last_status", "Dernier statut", "Last status"),
        ("source_path", "Chemin source", "Source path"),
        ("browse", "Parcourir", "Browse"),
        ("path_from_machine", "Chemin depuis machine", "Path from machine"),
        ("add", "Ajouter", "Add"),
        ("update", "Mettre a jour", "Update"),
        ("remove", "Supprimer", "Remove"),
        ("backup_root", "Racine serveur de sauvegarde", "Backup server root"),
        ("schedule", "Horaires", "Schedule"),
        ("db1_workdays", "DB1 jours ouvres", "DB1 workdays"),
        ("full_backup", "Sauvegarde complete", "Full backup"),
        ("friday_only", "Vendredi uniquement", "Friday only"),
        ("friday_time", "Heure vendredi", "Friday time"),
        ("stagger", "Decalage", "Stagger"),
        ("apply_schedule", "Appliquer les horaires au poste selectionne", "Apply schedule to selected user"),
        ("mode_explanation", "DB1 : cree automatiquement un dossier date. Complet : synchronise le dossier hebdomadaire.", "DB1: creates a dated folder automatically. Full: mirrors the weekly backup folder."),
        ("ignore_temp", "Ignorer fichiers temporaires/cache", "Ignore temporary/cache files"),
        ("purge_old", "Purger les anciennes sauvegardes", "Purge old backups"),
        ("days", "jours", "days"),
        ("comment", "Commentaire de sauvegarde", "Backup comment"),
        ("analyze", "Analyser actifs", "Analyze active"),
        ("run_db1", "Lancer DB1 maintenant", "Run DB1 now"),
        ("run_full", "Lancer complet maintenant", "Run full now"),
        ("no_backup", "Aucune sauvegarde lancee", "No backup started"),
        ("side_title", "Console de sauvegarde Tekla", "Tekla backup console"),
        ("recommended", "Structure recommandee", "Recommended structure"),
        ("background", "Mode arriere-plan", "Background mode"),
        ("startup", "Lancer cette console au demarrage de Windows", "Start this console with Windows"),
        ("background_info", "Fermer la fenetre cache l'application pres de l'horloge. Les sauvegardes automatiques continuent tant que la session Windows est ouverte.", "Closing the window hides the application near the clock. Automatic backups continue while the Windows session is open."),
        ("planning", "Planning", "Planning"),
        ("local_config", "Configuration locale", "Local configuration"),
        ("save_settings", "Enregistrer les parametres", "Save settings"),
        ("manifest", "Un fichier manifest.txt est cree a chaque sauvegarde pour tracer la source, l'utilisateur, la date et le commentaire.", "A manifest.txt file is created for each backup to trace source, user, date and comment."),
        ("journal", "Journal", "Journal"),
        ("footer", "Configurez les chemins et les droits avant d'activer les sauvegardes automatiques.", "Configure paths and permissions before enabling automatic backups."),
        ("cancel", "Annuler", "Cancel")
    ];

    private static bool IsBusinessDay(DayOfWeek day)
    {
        return day is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
    }

    private void SetBackupButtons(System.Windows.Controls.Button activeButton)
    {
        Db1BackupButton.IsEnabled = ReferenceEquals(activeButton, Db1BackupButton);
        FullBackupButton.IsEnabled = ReferenceEquals(activeButton, FullBackupButton);
        activeButton.Content = Translate("Annuler");
    }

    private void ResetBackupButtons()
    {
        Db1BackupButton.Content = Translate("Lancer DB1 maintenant");
        FullBackupButton.Content = Translate("Lancer complet maintenant");
        Db1BackupButton.IsEnabled = true;
        FullBackupButton.IsEnabled = true;
    }

    private void BuildSourceFromMachine_Click(object sender, RoutedEventArgs e)
    {
        SourcePathBox.Text = BuildDefaultSharePath(MachineNameBox.Text);
    }

    private void AddEmployee_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var employee = BuildEmployeeFromFields();
            _employees.Add(employee);
            EmployeesGrid.SelectedItem = employee;
            SaveSettings();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void UpdateEmployee_Click(object sender, RoutedEventArgs e)
    {
        if (EmployeesGrid.SelectedItem is not EmployeeBackupConfig selected)
        {
            ShowError("Selectionne un salarie a mettre a jour.");
            return;
        }

        try
        {
            var updated = BuildEmployeeFromFields();
            selected.EmployeeName = updated.EmployeeName;
            selected.MachineName = updated.MachineName;
            selected.SourcePath = updated.SourcePath;
            selected.DailyDb1Time = updated.DailyDb1Time;
            selected.WeeklyFullTime = updated.WeeklyFullTime;
            selected.IsEnabled = updated.IsEnabled;
            SaveSettings();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void RemoveEmployee_Click(object sender, RoutedEventArgs e)
    {
        if (EmployeesGrid.SelectedItem is EmployeeBackupConfig selected)
        {
            _employees.Remove(selected);
            SaveSettings();
        }
    }

    private void ApplyScheduleToSelected_Click(object sender, RoutedEventArgs e)
    {
        if (EmployeesGrid.SelectedItem is not EmployeeBackupConfig selected)
        {
            ShowError("Selectionne un salarie pour appliquer les horaires.");
            return;
        }

        try
        {
            selected.DailyDb1Time = FormatTime(ParseTime(DailyDb1TimeBox.Text.Trim(), "DB1 time"));
            selected.WeeklyFullTime = FormatTime(ParseTime(WeeklyFullTimeBox.Text.Trim(), "full backup time"));
            SaveSettings();
            Log($"Horaires appliques pour {selected.EmployeeName}: DB1 {selected.DailyDb1Time}, complet {selected.WeeklyFullTime}.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void EmployeesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EmployeesGrid.SelectedItem is not EmployeeBackupConfig selected)
        {
            return;
        }

        EmployeeNameBox.Text = selected.EmployeeName;
        MachineNameBox.Text = selected.MachineName;
        SourcePathBox.Text = selected.SourcePath;
        DailyDb1TimeBox.Text = selected.DailyDb1Time;
        WeeklyFullTimeBox.Text = selected.WeeklyFullTime;
        UpdateScheduleSummary();
    }

    private EmployeeBackupConfig BuildEmployeeFromFields()
    {
        var employeeName = SanitizeFolderName(EmployeeNameBox.Text.Trim()).ToUpperInvariant();
        var machineName = MachineNameBox.Text.Trim().ToUpperInvariant();
        var sourcePath = SourcePathBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(employeeName))
        {
            throw new InvalidOperationException("Indique le prenom du salarie.");
        }

        if (string.IsNullOrWhiteSpace(machineName))
        {
            throw new InvalidOperationException("Indique le nom de la machine.");
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            sourcePath = BuildDefaultSharePath(machineName);
        }

        return new EmployeeBackupConfig
        {
            EmployeeName = employeeName,
            MachineName = machineName,
            SourcePath = sourcePath,
            DailyDb1Time = DailyDb1TimeBox.Text.Trim(),
            WeeklyFullTime = WeeklyFullTimeBox.Text.Trim(),
            IsEnabled = true
        };
    }

    private void AddDefaultEmployees()
    {
        _employees.Add(new EmployeeBackupConfig
        {
            EmployeeName = Environment.UserName.ToUpperInvariant(),
            MachineName = Environment.MachineName.ToUpperInvariant(),
            SourcePath = BuildDefaultSharePath(Environment.MachineName),
            DailyDb1Time = DailyDb1TimeBox.Text.Trim(),
            WeeklyFullTime = WeeklyFullTimeBox.Text.Trim(),
            IsEnabled = true
        });
    }
}

public sealed class EmployeeBackupConfig : INotifyPropertyChanged
{
    private bool _isEnabled = true;
    private string _employeeName = string.Empty;
    private string _machineName = string.Empty;
    private string _sourcePath = string.Empty;
    private string _dailyDb1Time = "12:15";
    private string _weeklyFullTime = "12:30";
    private string _lastStatus = "Jamais";

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value, nameof(IsEnabled));
    }

    public string EmployeeName
    {
        get => _employeeName;
        set => SetField(ref _employeeName, value, nameof(EmployeeName));
    }

    public string MachineName
    {
        get => _machineName;
        set => SetField(ref _machineName, value, nameof(MachineName));
    }

    public string SourcePath
    {
        get => _sourcePath;
        set => SetField(ref _sourcePath, value, nameof(SourcePath));
    }

    public string DailyDb1Time
    {
        get => _dailyDb1Time;
        set => SetField(ref _dailyDb1Time, value, nameof(DailyDb1Time));
    }

    public string WeeklyFullTime
    {
        get => _weeklyFullTime;
        set => SetField(ref _weeklyFullTime, value, nameof(WeeklyFullTime));
    }

    public string LastStatus
    {
        get => _lastStatus;
        set => SetField(ref _lastStatus, value, nameof(LastStatus));
    }

    private void SetField<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class AppSettings
{
    public string EmployeeName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public bool IncludeTimestamp { get; set; } = true;
    public bool ExcludeTemporaryFiles { get; set; } = true;
    public BackupMode Mode { get; set; } = BackupMode.FullTimestamped;
    public bool EnableRetention { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
    public string DailyDb1Time { get; set; } = "12:15";
    public DayOfWeek WeeklyFullDay { get; set; } = DayOfWeek.Friday;
    public string WeeklyFullTime { get; set; } = "12:30";
    public int StaggerMinutes { get; set; } = 3;
    public string Language { get; set; } = "fr";
    public List<EmployeeBackupConfig> Employees { get; set; } = [];
}

public sealed record BackupSchedule(
    TimeSpan DailyDb1Time,
    DayOfWeek WeeklyFullDay,
    TimeSpan WeeklyFullTime,
    int StaggerMinutes);

public sealed record BackupRequest(
    EmployeeBackupConfig Employee,
    string EmployeeName,
    string MachineName,
    string SourcePath,
    string DestinationRoot,
    string TargetPath,
    string Comment,
    bool ExcludeTemporaryFiles,
    BackupMode Mode,
    bool EnableRetention,
    int RetentionDays);

public sealed record BackupResult(string TargetPath, int FileCount, long TotalBytes);

public sealed record BackupProgressInfo(
    int CopiedFiles,
    int TotalFiles,
    long CopiedBytes,
    long TotalBytes,
    string CurrentFile);

public enum BackupMode
{
    FullTimestamped,
    Db1Timestamped,
    FullMirror
}
