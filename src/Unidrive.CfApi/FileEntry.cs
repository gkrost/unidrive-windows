namespace Unidrive.CfApi;

public sealed record FileEntry(
    string Name,
    long Size,
    long LastWriteTimeMs,
    bool IsDirectory,
    bool IsHydrated);
