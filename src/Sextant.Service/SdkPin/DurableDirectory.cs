using System.Runtime.InteropServices;

namespace Sextant.Service.SdkPin;

/// <summary>
/// Makes a rename durable by fsyncing its containing directory (issue #113 journal ordering). On Unix a
/// <c>rename</c> is atomic but only reaches stable storage once the directory itself is flushed, which .NET
/// has no API for (it refuses to open a directory as a file), so this calls libc directly. On Windows NTFS
/// journals the rename metadata itself and there is no portable directory flush, so it is a no-op. Best
/// effort: it never throws, and returns false when the flush could not be performed.
/// </summary>
internal static class DurableDirectory
{
    private const int ReadOnly = 0; // O_RDONLY

    public static bool TryFlush(string directory)
    {
        if (OperatingSystem.IsWindows())
            return true;

        try
        {
            var fd = NativeOpen(directory, ReadOnly);
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
