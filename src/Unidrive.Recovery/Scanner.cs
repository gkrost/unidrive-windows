namespace Unidrive.Recovery;

internal sealed record CloudPlaceholder(string Path, bool IsDirectory, uint ReparseTag);

internal static class Scanner
{
    public static IEnumerable<CloudPlaceholder> FindCloudPlaceholders(string rootPath)
    {
        var queue = new Queue<string>();
        queue.Enqueue(rootPath);

        while (queue.TryDequeue(out var dir))
        {
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var attrs = File.GetAttributes(entry);

                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    var tag = CfApi.GetReparseTag(entry);
                    if (CfApi.IsCloudReparseTag(tag))
                    {
                        yield return new CloudPlaceholder(entry,
                            (attrs & FileAttributes.Directory) != 0, tag);
                    }
                }

                if ((attrs & FileAttributes.Directory) != 0 &&
                    (attrs & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0)
                {
                    queue.Enqueue(entry);
                }
            }
        }
    }

    public static bool IsRunningAsAdmin()
    {
#pragma warning disable CA1416
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
#pragma warning restore CA1416
    }
}
