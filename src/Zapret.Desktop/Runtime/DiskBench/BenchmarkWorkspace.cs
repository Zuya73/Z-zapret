using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Zapret.Core;

namespace Zapret.Desktop.Runtime;

internal sealed class BenchmarkWorkspace : IDisposable
{
    private FileStream? file;
    private SafeFileHandle? directoryGuard;
    private bool ownsDirectory;
    internal string DirectoryPath { get; }
    internal string FilePath => Path.Combine(DirectoryPath, DiskBenchmarkPolicy.DataName);
    internal string CleanupNote { get; private set; } = "";
    private string MarkerPath => Path.Combine(DirectoryPath, "owner.json");

    internal BenchmarkWorkspace(string root)
    {
        root = DiskBenchmarkPolicy.Root(root);
        DirectoryPath = root + DiskBenchmarkPolicy.FolderPrefix + Guid.NewGuid().ToString("N");
    }
    internal async Task PrepareAsync(long bytes, IProgress<DiskBenchmarkProgress> progress, int total, CancellationToken token)
    {
        if (!CreateDirectory(DirectoryPath, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось создать отдельную папку теста.");
        ownsDirectory = true;
        if (new DriveInfo(Path.GetPathRoot(DirectoryPath)!).DriveFormat is "NTFS" or "ReFS")
        {
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(DirectoryPath).SetAccessControl(security);
        }
        directoryGuard = OpenDirectory(DirectoryPath, 0x80, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (directoryGuard.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось защитить папку теста от переименования.");
        if (File.GetAttributes(DirectoryPath).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Ссылки и точки перенаправления не используются для теста.");
        using (var marker = new FileStream(MarkerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(marker, new { kind = "x2zapret.diskbench.v1", directory = Path.GetFileName(DirectoryPath), user = WindowsIdentity.GetCurrent().User!.Value, created = DateTimeOffset.UtcNow });
        file = new FileStream(FilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if ((File.GetAttributes(FilePath) & (FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile)) != 0)
            throw new IOException("Для точного замера нужен обычный файл без сжатия, EFS и разреженного размещения.");
        byte[] buffer = new byte[4 * 1024 * 1024]; long completed = 0, reported = 0;
        while (completed < bytes)
        {
            token.ThrowIfCancellationRequested();
            RandomNumberGenerator.Fill(buffer);
            int count = (int)Math.Min(buffer.Length, bytes - completed);
            await file.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            completed += count;
            if (completed != bytes && completed - reported < 16 * 1024 * 1024) continue;
            reported = completed;
            progress.Report(new("Подготовка файла: " + (completed / 1048576) + " / " + (bytes / 1048576) + " МиБ", 0, total, (int)(15 * completed / bytes)));
        }
        await file.FlushAsync(token).ConfigureAwait(false); file.Flush(flushToDisk: true);
        if (file.Length != bytes) throw new IOException("Не удалось полностью подготовить тестовый файл.");
    }
    public void Dispose()
    {
        file?.Dispose(); file = null;
        if (!ownsDirectory) return;
        try
        {
            if (File.GetAttributes(DirectoryPath).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Папка теста была перенаправлена; очистка остановлена.");
            // Only the two files created by this instance may be removed. Never recurse through a volume or directory.
            if (File.Exists(FilePath)) File.Delete(FilePath);
            if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
            directoryGuard?.Dispose(); directoryGuard = null;
            Directory.Delete(DirectoryPath, recursive: false);
            ownsDirectory = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { CleanupNote = "Не удалось полностью убрать временную папку " + DirectoryPath + ". Посторонние файлы не удаляются. " + error.Message; }
        finally { directoryGuard?.Dispose(); directoryGuard = null; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectory(string path, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle OpenDirectory(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
