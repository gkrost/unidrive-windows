using System.Runtime.InteropServices;

namespace Unidrive.CfApi;

internal static class Callbacks
{
    private const int CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS = 0;
    private const int CF_CALLBACK_TYPE_FETCH_DATA = 1;
    private const int CF_CALLBACK_TYPE_DEHYDRATE = 17;

    private static CfCallbackDelegate? _callbackDelegate;
    private static GCHandle _callbackHandle;
    private static Func<string, IReadOnlyList<FileEntry>>? _fetcher;
    private static Func<string, string?>? _hydrator;
    private static Func<string, bool>? _dehydrator;

    private const int TRANSFER_CHUNK_SIZE = 1024 * 1024; // 1 MiB

    internal static IntPtr CreateCallbackTable(
        string syncRootPath,
        Func<string, IReadOnlyList<FileEntry>>? fetcher = null,
        Func<string, string?>? hydrator = null,
        Func<string, bool>? dehydrator = null)
    {
        _fetcher = fetcher;
        _hydrator = hydrator;
        _dehydrator = dehydrator;
        _callbackDelegate = CallbackDispatcher;
        _callbackHandle = GCHandle.Alloc(_callbackDelegate);

        var table = new[]
        {
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS,
                Callback = Marshal.GetFunctionPointerForDelegate(_callbackDelegate),
            },
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE_FETCH_DATA,
                Callback = Marshal.GetFunctionPointerForDelegate(_callbackDelegate),
            },
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE_DEHYDRATE,
                Callback = Marshal.GetFunctionPointerForDelegate(_callbackDelegate),
            },
            default,
        };

        int size = Marshal.SizeOf<CF_CALLBACK_REGISTRATION>();
        IntPtr ptr = Marshal.AllocHGlobal(table.Length * size);
        for (int i = 0; i < table.Length; i++)
            Marshal.StructureToPtr(table[i], ptr + i * size, false);

        return ptr;
    }

    internal static void FreeCallbackTable(IntPtr table)
    {
        if (table != IntPtr.Zero) Marshal.FreeHGlobal(table);
        if (_callbackHandle.IsAllocated) _callbackHandle.Free();
        _callbackDelegate = null;
        _fetcher = null;
        _hydrator = null;
        _dehydrator = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CfCallbackDelegate(IntPtr callbackInfo, IntPtr callbackParameters);

    private static void CallbackDispatcher(IntPtr info, IntPtr parameters)
    {
        int cbType = Marshal.ReadInt32(parameters, 4);

        switch (cbType)
        {
            case CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS:
                HandleFetchPlaceholders(info, parameters);
                break;
            case CF_CALLBACK_TYPE_FETCH_DATA:
                HandleFetchData(info, parameters);
                break;
            case CF_CALLBACK_TYPE_DEHYDRATE:
                HandleDehydrate(info, parameters);
                break;
        }
    }

    private static void HandleFetchPlaceholders(IntPtr info, IntPtr parameters)
    {
        if (_fetcher is null) return;

        var fileIdentity = Marshal.ReadIntPtr(info, 8);
        string prefix = Marshal.PtrToStringUni(fileIdentity) ?? "";

        var entries = _fetcher(prefix);
        if (entries.Count == 0) return;

        var placeholders = new CF_PLACEHOLDER_CREATE_INFO[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            string combined = CombinePrefix(prefix, e.Name);
            placeholders[i] = new CF_PLACEHOLDER_CREATE_INFO
            {
                StructSize = Marshal.SizeOf<CF_PLACEHOLDER_CREATE_INFO>(),
                RelativeFileName = e.Name,
                FileSize = e.Size,
                AllocationSize = 0,
                LastWriteTime = e.LastWriteTimeMs * 10_000 + 116444736000000000,
                FileAttributes = e.IsDirectory ? (uint)0x10u : 0u,
                Flags = e.IsHydrated ? 2u : 0u,
                FileIdentity = Marshal.StringToCoTaskMemUni(combined),
                FileIdentityLength = (combined.Length + 1) * 2,
            };
        }

        string localParent = MapToLocalPath(prefix);
        Native.CfCreatePlaceholders(localParent, placeholders, placeholders.Length, out _, 0);

        foreach (var p in placeholders)
            if (p.FileIdentity != IntPtr.Zero)
                Marshal.FreeCoTaskMem(p.FileIdentity);
    }

    private static void HandleFetchData(IntPtr info, IntPtr parameters)
    {
        if (_hydrator is null) return;

        var fileIdentity = Marshal.ReadIntPtr(info, 8);
        string logicalPath = Marshal.PtrToStringUni(fileIdentity) ?? "";

        string? cachePath = _hydrator(logicalPath);
        if (cachePath is null)
        {
            FailTransfer(info);
            return;
        }

        try
        {
            long transferKey = Marshal.ReadInt64(info, 40);
            long reqOffset = Marshal.ReadInt64(parameters, 8);
            long reqLength = Marshal.ReadInt64(parameters, 16);

            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long remaining = fs.Length - reqOffset;
            if (remaining <= 0) { FailTransfer(info); return; }

            if (reqLength > 0 && reqLength < remaining)
                remaining = reqLength;

            var buffer = new byte[Math.Min(remaining, TRANSFER_CHUNK_SIZE)];
            fs.Seek(reqOffset, SeekOrigin.Begin);

            long totalSent = 0;
            while (totalSent < remaining)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining - totalSent);
                int read = fs.Read(buffer, 0, toRead);
                if (read <= 0) break;

                var opInfo = BuildOperationInfo(transferKey, buffer, read);
                var opParams = BuildTransferParams(reqOffset + totalSent, read, 0);
                int hr = Native.CfExecute(opInfo, opParams);
                if (hr < 0) break;

                totalSent += read;
            }

            if (totalSent >= remaining) return;
            FailTransfer(info);
        }
        catch
        {
            FailTransfer(info);
        }
    }

    private static void FailTransfer(IntPtr info)
    {
        long transferKey = Marshal.ReadInt64(info, 40);
        var opInfo = BuildOperationInfo(transferKey, [], 0);
        var opParams = BuildTransferParams(0, 0, -1); // failure status
        Native.CfExecute(opInfo, opParams);
    }

    private static byte[] BuildOperationInfo(long transferKey, byte[] buffer, int length)
    {
        var buf = new byte[64];
        BitConverter.GetBytes(64).CopyTo(buf, 0);     // StructSize
        BitConverter.GetBytes(2).CopyTo(buf, 4);       // Type = TRANSFER_DATA

        // FileIdentity (offset 8), FileIdentityLength (offset 16), SyncRootIdentity (offset 24):
        // leave zero — the root connection owns these.

        BitConverter.GetBytes(transferKey).CopyTo(buf, 40); // TransferKey

        if (length > 0)
        {
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            BitConverter.GetBytes(handle.AddrOfPinnedObject().ToInt64()).CopyTo(buf, 48); // Buffer
            BitConverter.GetBytes(length).CopyTo(buf, 56);  // BufferLength
        }

        return buf;
    }

    private static byte[] BuildTransferParams(long offset, long length, int status)
    {
        var buf = new byte[32];
        BitConverter.GetBytes(32).CopyTo(buf, 0);     // StructSize
        BitConverter.GetBytes(offset).CopyTo(buf, 8);  // Offset
        BitConverter.GetBytes(length).CopyTo(buf, 16); // Length
        BitConverter.GetBytes(status).CopyTo(buf, 24); // CompletionStatus (0=success, -1=fail)
        return buf;
    }

    private static void HandleDehydrate(IntPtr info, IntPtr parameters)
    {
        if (_dehydrator is null) return;

        var fileIdentity = Marshal.ReadIntPtr(info, 8);
        string logicalPath = Marshal.PtrToStringUni(fileIdentity) ?? "";
        if (string.IsNullOrEmpty(logicalPath)) return;

        _dehydrator(logicalPath);
    }

    private static string CombinePrefix(string prefix, string name)
    {
        if (string.IsNullOrEmpty(prefix) || prefix == "/")
            return name;
        return prefix.EndsWith('/') ? prefix + name : prefix + "/" + name;
    }

    private static string MapToLocalPath(string logicalPath)
    {
        return string.IsNullOrEmpty(logicalPath) || logicalPath == "/"
            ? ""
            : logicalPath.TrimStart('/');
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CF_CALLBACK_REGISTRATION
    {
        public int Type;
        public IntPtr Callback;
    }
}
