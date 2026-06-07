using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Unidrive.CfApi;

public static class Native
{
    internal const uint IoReparseTagCloud = 0x9000001A;
    internal const uint CloudTagMask = 0xFFFFF01F;
    internal const uint STATUS_CLOUD_FILE_NOT_SUPPORTED = 0x80070103;
    public const uint CF_UPDATE_FLAG_MARK_IN_SYNC = 1;

    internal static readonly Guid ProviderId = new("B1D5A3E0-4F8A-4C7D-9E2A-1B3C5D7F9E0A");

    internal static bool IsCloudReparseTag(uint tag) => (tag & CloudTagMask) == IoReparseTagCloud;

    // ──── cldapi.dll ────

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CfRegisterSyncRoot(
        string syncRootPath,
        ref CF_SYNC_REGISTRATION registration,
        ref CF_SYNC_POLICIES policies,
        uint registerFlags);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CfUnregisterSyncRoot(string syncRootPath);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CfConnectSyncRoot(
        string syncRootPath,
        IntPtr callbackTable,
        IntPtr callbackContext,
        uint connectFlags,
        out CF_CONNECTION_KEY connectKey);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    internal static extern int CfDisconnectSyncRoot(CF_CONNECTION_KEY connectKey);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CfRevertPlaceholder(string filePath, uint revertFlags, IntPtr overlapped);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CfCreatePlaceholders(
        string parentPath,
        [In, Out] CF_PLACEHOLDER_CREATE_INFO[] placeholders,
        int placeholderCount,
        out int entriesCreated,
        int flags);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    internal static extern int CfExecute(byte[] opInfo, byte[] opParams);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CfUpdatePlaceholder(
        string filePath,
        ref CF_FS_METADATA fsMetadata,
        IntPtr placeholderInfo,
        int placeholderInfoLength,
        uint updateFlags,
        IntPtr overlapped);

    // ──── kernel32.dll ────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess,
        FileShare dwShareMode, IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition, uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        [Out] byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    internal const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
    internal const int REPARSE_DATA_BUFFER_SIZE = 16384;

    internal static uint GetReparseTag(string path)
    {
        using var handle = CreateFile(
            path, 0, FileShare.ReadWrite, IntPtr.Zero,
            FileMode.Open,
            0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) return 0;
        var buf = new byte[REPARSE_DATA_BUFFER_SIZE];
        if (!DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT,
                IntPtr.Zero, 0, buf, REPARSE_DATA_BUFFER_SIZE,
                out _, IntPtr.Zero))
            return 0;
        return (uint)BitConverter.ToInt32(buf, 0);
    }

    internal static int HRESULT_CODE(int hr) => hr & 0xFFFF;
}

// ──── CfAPI structs (x64 Win11 22H2+ layouts) ────

[StructLayout(LayoutKind.Sequential)]
internal struct CF_CONNECTION_KEY
{
    public long Value;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CF_SYNC_REGISTRATION
{
    public int StructSize;
    [MarshalAs(UnmanagedType.LPWStr)] public string ProviderName;
    [MarshalAs(UnmanagedType.LPWStr)] public string ProviderVersion;
    public IntPtr SyncRootIdentity;
    public int SyncRootIdentityLength;
    public IntPtr FileIdentity;
    public int FileIdentityLength;
    public Guid ProviderId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_HYDRATION_POLICY
{
    public ushort Primary;
    public ushort Modifier;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_POPULATION_POLICY
{
    public ushort Primary;
    public ushort Modifier;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CF_SYNC_POLICIES
{
    public int StructSize;
    public CF_HYDRATION_POLICY Hydration;
    public CF_POPULATION_POLICY Population;
    public int InSyncPolicy;
    public int HardLinkPolicy;
}

[StructLayout(LayoutKind.Sequential)]
public struct CF_FS_METADATA
{
    public long FileSize;
    public long CreationTime;
    public long LastWriteTime;
    public long ChangeTime;
    public long AllocationSize;
    public uint FileAttributes;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CF_PLACEHOLDER_CREATE_INFO
{
    public int StructSize;
    [MarshalAs(UnmanagedType.LPWStr)] public string RelativeFileName;
    public long CreationTime;
    public long LastWriteTime;
    public long ChangeTime;
    public long AllocationSize;
    public long FileSize;
    public uint FileAttributes;
    public int ReparseDataLength;
    public short ReparseDataBuffer;
    public uint Flags;
    public uint Result;
    public int FileIdentityLength;
    public IntPtr FileIdentity;
}
