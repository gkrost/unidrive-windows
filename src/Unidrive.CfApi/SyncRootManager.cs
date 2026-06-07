using System.Runtime.InteropServices;

namespace Unidrive.CfApi;

public sealed class SyncRootManager : IDisposable
{
    private readonly string _syncRootPath;
    private readonly Func<string, IReadOnlyList<FileEntry>>? _fetcher;
    private readonly Func<string, string?>? _hydrator;
    private readonly Func<string, bool>? _dehydrator;
    private bool _registered;
    private IntPtr _callbackTable;
    private CF_CONNECTION_KEY _connectKey;

    public string SyncRootPath => _syncRootPath;
    public bool IsRegistered => _registered;

    public SyncRootManager(
        string syncRootPath,
        Func<string, IReadOnlyList<FileEntry>>? fetcher = null,
        Func<string, string?>? hydrator = null,
        Func<string, bool>? dehydrator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRootPath);
        _syncRootPath = syncRootPath;
        _fetcher = fetcher;
        _hydrator = hydrator;
        _dehydrator = dehydrator;
    }

    public void Register()
    {
        if (_registered) return;

        Directory.CreateDirectory(_syncRootPath);

        var registration = new CF_SYNC_REGISTRATION
        {
            StructSize = Marshal.SizeOf<CF_SYNC_REGISTRATION>(),
            ProviderName = "Unidrive",
            ProviderVersion = "1.0.0",
            ProviderId = Native.ProviderId,
        };

        var policies = new CF_SYNC_POLICIES
        {
            StructSize = Marshal.SizeOf<CF_SYNC_POLICIES>(),
            Hydration = new CF_HYDRATION_POLICY { Primary = 1 }, // PROGRESSIVE
            Population = new CF_POPULATION_POLICY { Primary = 2 }, // FULL
        };

        int hr = Native.CfRegisterSyncRoot(
            _syncRootPath, ref registration, ref policies, 0);
        if (hr < 0)
            throw new InvalidOperationException(
                $"CfRegisterSyncRoot failed: 0x{hr:X8}");

        _callbackTable = Callbacks.CreateCallbackTable(
            _syncRootPath, _fetcher, _hydrator, _dehydrator);

        hr = Native.CfConnectSyncRoot(
            _syncRootPath, _callbackTable, IntPtr.Zero, 0, out _connectKey);
        if (hr < 0)
        {
            Native.CfUnregisterSyncRoot(_syncRootPath);
            Callbacks.FreeCallbackTable(_callbackTable);
            _callbackTable = IntPtr.Zero;
            throw new InvalidOperationException(
                $"CfConnectSyncRoot failed: 0x{hr:X8}");
        }

        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered) return;

        try { Native.CfDisconnectSyncRoot(_connectKey); }
        catch { }

        RevertAllPlaceholders(_syncRootPath);

        int hr = Native.CfUnregisterSyncRoot(_syncRootPath);
        if (hr < 0 && unchecked((uint)hr) != Native.STATUS_CLOUD_FILE_NOT_SUPPORTED)
            throw new InvalidOperationException(
                $"CfUnregisterSyncRoot failed: 0x{hr:X8}");

        Callbacks.FreeCallbackTable(_callbackTable);
        _callbackTable = IntPtr.Zero;
        _registered = false;
    }

    private static void RevertAllPlaceholders(string root)
    {
        foreach (var entry in EnumerateCloudPlaceholders(root))
            Native.CfRevertPlaceholder(entry, 0, IntPtr.Zero);
    }

    private static IEnumerable<string> EnumerateCloudPlaceholders(string root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.TryDequeue(out var dir))
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch { continue; }

            foreach (var entry in entries)
            {
                var attrs = File.GetAttributes(entry);

                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    var tag = Native.GetReparseTag(entry);
                    if (Native.IsCloudReparseTag(tag))
                        yield return entry;
                }

                if ((attrs & FileAttributes.Directory) != 0 &&
                    (attrs & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0)
                {
                    queue.Enqueue(entry);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_registered) Unregister();
    }
}
