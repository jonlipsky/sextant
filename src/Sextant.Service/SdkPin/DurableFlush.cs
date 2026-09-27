using System.Runtime.InteropServices;

namespace Sextant.Service.SdkPin;

/// <summary>
/// fsyncs a file or directory by path (issue #113 journal ordering). On Unix a <c>rename</c> is atomic but
/// only reaches stable storage once the containing directory is flushed, and inode metadata (mode, mtime)
/// only once the file itself is — .NET has no API for either (it refuses to open a directory as a file, and
/// a read-only handle cannot be flushed), so this calls libc <c>open(O_RDONLY)</c> + <c>fsync</c> directly.
/// On Windows NTFS journals rename and metadata changes itself and there is no portable directory flush, so
/// it is a no-op. Best effort: it never throws, and returns false when the flush could not be performed.
/// </summary>
internal static class DurableFlush
{
    private const int ReadOnly = 0; // O_RDONLY

    public static bool TryFlush(string path)
    {
        if (OperatingSystem.IsWindows())
            return true;

        try
        {
            var fd = NativeOpen(path, ReadOnly);
            if (fd < 0)
                return false;
            try
            {
                return NativeFsync(fd) == 0;
            }
            finally
            {
                _ = NativeClose(fd);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeFsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeClose(int fd);
}
