using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TeklaBackupManager;

/// <summary>Persistent lock file; never unlink it, since another process may already hold its inode.</summary>
internal sealed class ExclusiveLease : IDisposable
{
    private FileStream? stream;
    private bool initialized;
    private readonly byte[] token = RandomNumberGenerator.GetBytes(32);
    private ExclusiveLease(FileStream stream) { this.stream = stream; }

    internal static ExclusiveLease Acquire(string directory, string fileName)
    {
        directory = CanonicalPaths.Resolve(directory);
        if (fileName == ".tekla-backup.lock") CheckManagedRootOverlap(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        for (var parent = new DirectoryInfo(Path.GetFullPath(directory)); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Lock paths must not contain reparse points.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Lock file must not be a reparse point.");
        FileStream? opened = null;
        try
        {
            opened = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.WriteThrough);
            if (fileName == ".tekla-backup.lock") CheckManagedRootOverlap(directory);
            // Fail closed unless the filesystem/provider actually enforces exclusive sharing.
            bool denied = false;
            try { using var conflicting = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { denied = true; }
            if (!denied) throw new IOException("Filesystem does not enforce the required exclusive sharing.");
            var lease = new ExclusiveLease(opened);
            opened = null;
            try { lease.EnsureHeld(); return lease; }
            catch { lease.Dispose(); throw; }
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Operation refused: profile/destination is busy, unavailable, or cannot guarantee exclusive locking.", ex);
        }
        finally { opened?.Dispose(); }
    }

    internal void EnsureHeld()
    {
        var handle = stream ?? throw new InvalidOperationException("Exclusive lock is no longer held.");
        try
        {
            if (initialized)
            {
                handle.Position = 0;
                var previous = new byte[token.Length];
                handle.ReadExactly(previous);
                if (!CryptographicOperations.FixedTimeEquals(token, previous))
                    throw new IOException("Exclusive lock identity changed.");
            }
            bool denied = false;
            try { using var conflicting = new FileStream(handle.Name, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { denied = true; }
            if (!denied) throw new IOException("Exclusive sharing is no longer enforced.");
            handle.Position = 0;
            handle.Write(token);
            handle.SetLength(token.Length);
            handle.Flush(flushToDisk: true);
            handle.Position = 0;
            var read = new byte[token.Length];
            handle.ReadExactly(read);
            if (!CryptographicOperations.FixedTimeEquals(token, read))
                throw new IOException("Exclusive lock identity changed.");
            initialized = true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Backup stopped: the exclusive lock could not be verified. No further copy or deletion is allowed.", ex);
        }
    }

    private static void CheckManagedRootOverlap(string root)
    {
        var full = Path.GetFullPath(root);
        bool Marker(string directory) => File.Exists(Path.Combine(directory, ".tekla-backup.lock")) ||
            File.Exists(Path.Combine(directory, ".tekla-backup-owners.json"));
        for (var parent = Directory.GetParent(full); parent is not null; parent = parent.Parent)
            if (Marker(parent.FullName))
                throw new InvalidOperationException("Nested backup roots are not allowed. Select independent backup roots.");
        if (!Directory.Exists(full)) return;
        var pending = new Stack<string>(); pending.Push(full);
        while (pending.Count > 0)
            foreach (var child in Directory.EnumerateDirectories(pending.Pop()))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Reparse points in backup roots are not supported.");
                if (Marker(child))
                    throw new InvalidOperationException("This backup root contains another managed backup root.");
                pending.Push(child);
            }
    }

    public void Dispose() { stream?.Dispose(); stream = null; }
}

internal static class DestinationOwnership
{
    internal static void Claim(BackupRequest request, ExclusiveLease lease)
    {
        lease.EnsureHeld();
        var registry = Path.Combine(request.DestinationRoot, ".tekla-backup-owners.json");
        var owners = File.Exists(registry)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(registry))
                ?? throw new InvalidOperationException("Invalid destination ownership registry.")
            : new Dictionary<string, string>();
        owners = new Dictionary<string, string>(owners, StringComparer.OrdinalIgnoreCase);
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.SourcePath)).ToUpperInvariant();
        var identity = (source.StartsWith(@"\\", StringComparison.Ordinal) ? "UNC|" : Environment.MachineName.ToUpperInvariant() + "|") + source;
        var user = request.EmployeeName.Normalize().ToUpperInvariant();
        if (owners.TryGetValue(user, out var owner))
        {
            if (!string.Equals(owner, identity, StringComparison.Ordinal))
                throw new InvalidOperationException("This user destination already belongs to another source. Choose a distinct user name or a new backup root; no automatic merging is allowed.");
            return;
        }
        foreach (var modeDirectory in new[] { "BACKUPDB1", "TEKLASTRUCTUREMODEL" })
        {
            var existing = Path.Combine(request.DestinationRoot, modeDirectory, user);
            if (Directory.Exists(existing) && Directory.EnumerateFileSystemEntries(existing).Any())
                throw new InvalidOperationException("An existing backup has no verified owner. Use a new backup root; legacy backups are not automatically adopted.");
        }
        owners.Add(user, identity);
        var temporary = registry + "." + Guid.NewGuid().ToString("N") + ".tmp";
        lease.EnsureHeld();
        File.WriteAllText(temporary, JsonSerializer.Serialize(owners));
        lease.EnsureHeld();
        File.Move(temporary, registry, overwrite: true);
    }
}


/// <summary>Resolve directory handles, including mapped/substituted-drive aliases, before locking or comparing roots.</summary>
internal static class CanonicalPaths
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        System.Text.StringBuilder path, uint length, uint flags);

    internal static string Resolve(string path)
    {
        var existing = Path.GetFullPath(path);
        var missing = new Stack<string>();
        while (!Directory.Exists(existing))
        {
            var parent = Directory.GetParent(existing) ?? throw new IOException("Cannot resolve the backup filesystem root.");
            missing.Push(Path.GetFileName(existing));
            existing = parent.FullName;
        }
        using var handle = CreateFileW(existing, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Cannot resolve directory identity; operation refused.");
        var buffer = new System.Text.StringBuilder(512);
        var count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (count >= buffer.Capacity)
        {
            buffer = new System.Text.StringBuilder(checked((int)count + 1));
            count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (count == 0 || count >= buffer.Capacity) throw new IOException("Cannot resolve directory identity; operation refused.");
        var resolved = buffer.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        else throw new IOException("Unsupported directory identity; operation refused.");
        foreach (var part in missing) resolved = Path.Combine(resolved, part);
        return Path.TrimEndingDirectorySeparator(resolved);
    }
}
