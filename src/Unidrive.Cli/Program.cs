using Unidrive.CfApi;
using Unidrive.Ipc;

string cmd = args.Length > 0 ? args[0] : "help";
string profile = "default";
string? root = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] is "--profile" or "-p")
        profile = args[i + 1];
    else if (args[i] is "--root" or "-r")
        root = args[i + 1];

root ??= Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "unidrive", profile);

switch (cmd)
{
    case "status": return await StatusCommand(profile);
    case "mount":  return await MountCommand(profile, root);
    case "unmount": return UnmountCommand(root);
    default:
        Console.Error.WriteLine("""
            unidrive-win — unidrive Windows CfAPI mount client

            USAGE
              status  [--profile <name>]   Show daemon status (Phase 0.3 smoke test)
              mount   [--profile <name>]
                      [--root <path>]      Register sync root + connect callbacks (Phase 1.1)
              unmount [--root <path>]      Unregister sync root + revert placeholders (Phase 1.6)

            --profile defaults to "default"
            --root    defaults to %%USERPROFILE%%\\unidrive\\<profile>
            """);
        return 2;
}

static async Task<int> StatusCommand(string profile)
{
    await using var client = new IpcClient(profile);
    Console.WriteLine($"connecting to {client.SocketPath} ...");
    try
    {
        var s = await client.DaemonStatusAsync();
        Console.WriteLine(
            $"OK - daemon up {s.UptimeMs} ms, {s.ClientsConnected} client(s), " +
            $"refresh_in_flight={s.RefreshInFlight}, job={s.RefreshJobId ?? "-"}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAILED: {ex.Message}");
        Console.Error.WriteLine($"Is the daemon running?  java -jar unidrive.jar -p {profile} daemon run");
        return 1;
    }
}

static async Task<int> MountCommand(string profile, string root)
{
    await using var client = new IpcClient(profile);
    Console.WriteLine($"connecting to {client.SocketPath} ...");
    try
    {
        var s = await client.DaemonStatusAsync();
        Console.WriteLine($"daemon up {s.UptimeMs} ms, {s.ClientsConnected} client(s)");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAILED: {ex.Message}");
        Console.Error.WriteLine($"Is the daemon running?  java -jar unidrive.jar -p {profile} daemon run");
        return 1;
    }

    Console.WriteLine($"Registering sync root at {root} ...");

    Func<string, IReadOnlyList<Unidrive.CfApi.FileEntry>> fetcher = prefix =>
    {
        var entries = client.ListDirectoryAsync(prefix)
            .GetAwaiter().GetResult();
        return entries.Select(e => new Unidrive.CfApi.FileEntry(
            e.Name, e.Size, e.LastWriteTimeMs, e.IsDirectory, e.IsHydrated)).ToList();
    };

    Func<string, string?> hydrator = logicalPath =>
    {
        try
        {
            return client.OpenReadAsync(logicalPath)
                .GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    };

    Func<string, bool> dehydrator = logicalPath =>
    {
        try
        {
            client.RequestAsync("hydration.dehydrate",
                new Dictionary<string, object?> { ["path"] = logicalPath })
                .GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    };

    using var cts = new CancellationTokenSource();
    using var mgr = new SyncRootManager(root, fetcher, hydrator, dehydrator);
    try
    {
        mgr.Register();
        Console.WriteLine("OK — sync root registered + callbacks active. Press Ctrl+C to unmount.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAILED: {ex.Message}");
        return 1;
    }

    var refreshTask = RefreshLoop(client, root, cts.Token);

    var done = new TaskCompletionSource();
    Console.CancelKeyPress += (_, _) =>
    {
        Console.Error.WriteLine("\nShutting down ...");
        cts.Cancel();
        done.TrySetResult();
    };
    await done.Task;
    Console.Error.Write("Unregistering ... ");
    mgr.Unregister();
    Console.Error.WriteLine("OK");
    return 0;
}

static async Task RefreshLoop(IpcClient client, string root, CancellationToken ct)
{
    try
    {
        await foreach (var evt in client.SubscribeAsync(ct))
        {
            if (evt.TryGetProperty("type", out var t) && t.GetString() == "view.invalidated")
            {
                if (evt.TryGetProperty("full", out var f) && f.GetBoolean())
                    await RefreshDirectory(client, root, "", ct);

                if (evt.TryGetProperty("paths", out var paths) && paths.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var p in paths.EnumerateArray())
                        await RefreshDirectory(client, root, p.GetString() ?? "", ct);
            }
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Refresh loop error: {ex.Message}");
    }
}

static async Task RefreshDirectory(IpcClient client, string root, string prefix, CancellationToken ct)
{
    try
    {
        var entries = await client.ListDirectoryAsync(prefix, ct);
        foreach (var e in entries)
        {
            string localPath = Path.Combine(root, prefix.TrimStart('/'), e.Name);
            if (!File.Exists(localPath) && !Directory.Exists(localPath)) continue;

            var meta = new Unidrive.CfApi.CF_FS_METADATA
            {
                FileSize = e.Size,
                FileAttributes = e.IsDirectory ? 0x10u : 0u,
                LastWriteTime = e.LastWriteTimeMs * 10_000 + 116444736000000000,
            };
            Unidrive.CfApi.Native.CfUpdatePlaceholder(
                localPath, ref meta, IntPtr.Zero, 0,
                Unidrive.CfApi.Native.CF_UPDATE_FLAG_MARK_IN_SYNC, IntPtr.Zero);
        }
    }
    catch { }
}

static int UnmountCommand(string root)
{
    Console.Error.Write($"Unregistering sync root at {root} ... ");
    try
    {
        var mgr = new SyncRootManager(root);
        mgr.Unregister();
        mgr.Dispose();
        Console.Error.WriteLine("OK");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAILED: {ex.Message}");
        Console.Error.WriteLine("Try running unidrive-recovery clean --path <dir>");
        return 1;
    }
}
