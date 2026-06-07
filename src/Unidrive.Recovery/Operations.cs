namespace Unidrive.Recovery;

internal static class Operations
{
    public static bool TryRevert(string path, out string? error)
    {
        var hr = CfApi.RevertPlaceholder(path);
        if (hr >= 0)
        {
            error = null;
            return true;
        }
        error = $"CfRevertPlaceholder failed: 0x{hr:X8}";
        return false;
    }

    public static bool TryUnregister(string syncRootPath, out string? error)
    {
        var hr = CfApi.UnregisterSyncRoot(syncRootPath);
        if (hr >= 0)
        {
            error = null;
            return true;
        }
        error = $"CfUnregisterSyncRoot failed: 0x{hr:X8}";
        return false;
    }

    public static int AttemptDetach()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "fltmc.exe",
            Arguments = "detach cldflt C:",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) return -1;
        proc.WaitForExit(30000);
        return proc.ExitCode;
    }
}
