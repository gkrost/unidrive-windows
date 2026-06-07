using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Unidrive.Recovery;

internal static class CfApi
{
    internal const uint IoReparseTagCloud = 0x9000001A;
    internal const uint CloudTagMask = 0xFFFFF01F;

    internal static bool IsCloudReparseTag(uint tag) => (tag & CloudTagMask) == IoReparseTagCloud;

    internal static bool IsSyncRoot(string path)
    {
        uint bytesReturned;
        int hr = CfGetSyncRootInfoByPath(path, 0, null, 0, out bytesReturned);
        return hr >= 0 || (hr < 0 && unchecked((uint)hr) != 0x80070103);
    }

    internal static int RevertPlaceholder(string path)
    {
        return CfRevertPlaceholder(path, 0, IntPtr.Zero);
    }

    internal static int UnregisterSyncRoot(string path)
    {
        return CfUnregisterSyncRoot(path);
    }

    internal static uint GetReparseTag(string path)
    {
        using var handle = CreateFile(
            path,
            0,
            FileShare.ReadWrite,
            IntPtr.Zero,
            FileMode.Open,
            FileFlags.FILE_FLAG_OPEN_REPARSE_POINT | FileFlags.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return 0;

        var buf = new byte[REPARSE_DATA_BUFFER_SIZE];
        uint bytesReturned;
        if (!DeviceIoControl(
                handle,
                FSCTL_GET_REPARSE_POINT,
                IntPtr.Zero,
                0,
                buf,
                REPARSE_DATA_BUFFER_SIZE,
                out bytesReturned,
                IntPtr.Zero))
            return 0;

        return (uint)BitConverter.ToInt32(buf, 0);
    }

    private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
    private const int REPARSE_DATA_BUFFER_SIZE = 16384;

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CfGetSyncRootInfoByPath(
        string filePath,
        int infoClass,
        byte[]? infoBuffer,
        int infoBufferLength,
        out uint infoBufferLengthRet);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CfRevertPlaceholder(
        string filePath,
        int revertFlags,
        IntPtr overlapped);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CfUnregisterSyncRoot(
        string syncRootPath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        int dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        FileFlags dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        [Out] byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [Flags]
    private enum FileFlags : uint
    {
        FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000,
        FILE_FLAG_BACKUP_SEMANTICS = 0x02000000,
    }
}
