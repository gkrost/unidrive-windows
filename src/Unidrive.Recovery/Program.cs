using Unidrive.Recovery;

string cmd = args.Length > 0 ? args[0] : "help";
string? targetPath = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] is "--path" or "-p")
        targetPath = args[i + 1];

targetPath ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
switch (cmd)
{
    case "scan":
        return ScanCommand(targetPath);
    case "revert":
        if (args.Length < 2) { PrintUsage(); return 2; }
        return RevertCommand(targetPath ?? args[1]);
    case "unregister":
        if (args.Length < 2) { PrintUsage(); return 2; }
        return UnregisterCommand(targetPath ?? args[1]);
    case "clean":
        return CleanCommand(targetPath);
    case "detach":
        if (!Scanner.IsRunningAsAdmin()) { Console.Error.WriteLine("detach requires admin elevation"); return 2; }
        return DetachCommand();
    default:
        PrintUsage();
        return 2;
}

static int ScanCommand(string root)
{
    Console.Error.WriteLine($"Scanning {root} for cloud placeholders ...");
    int found = 0;
    foreach (var p in Scanner.FindCloudPlaceholders(root))
    {
        Console.WriteLine($"{p.ReparseTag:X8}  {(p.IsDirectory ? 'd' : '-')}  {p.Path}");
        found++;
    }
    Console.Error.WriteLine($"Found {found} cloud placeholder(s)");
    return found > 0 ? 0 : 0;
}

static int RevertCommand(string path)
{
    if (!File.Exists(path) && !Directory.Exists(path))
    {
        Console.Error.WriteLine($"path not found: {path}");
        return 1;
    }
    Console.Error.Write($"Reverting {path} ... ");
    if (Operations.TryRevert(path, out var error))
    {
        Console.Error.WriteLine("OK");
        return 0;
    }
    Console.Error.WriteLine($"FAILED ({error})");
    return 1;
}

static int UnregisterCommand(string path)
{
    if (!Directory.Exists(path))
    {
        Console.Error.WriteLine($"directory not found: {path}");
        return 1;
    }
    Console.Error.Write($"Unregistering sync root {path} ... ");
    if (Operations.TryUnregister(path, out var error))
    {
        Console.Error.WriteLine("OK");
        return 0;
    }
    Console.Error.WriteLine($"FAILED ({error})");

    if (!Scanner.IsRunningAsAdmin())
        Console.Error.WriteLine("Try running as Administrator");
    return 1;
}

static int CleanCommand(string root)
{
    Console.Error.WriteLine($"Cleaning {root} ...");
    int reverted = 0, unregistered = 0, errors = 0;

    foreach (var p in Scanner.FindCloudPlaceholders(root))
    {
        bool isRoot = CfApi.IsSyncRoot(p.Path);

        if (isRoot)
        {
            Console.Error.Write($"  unregister sync root: {p.Path} ... ");
            if (Operations.TryUnregister(p.Path, out var ue))
            {
                Console.Error.WriteLine("OK");
                unregistered++;
            }
            else
            {
                Console.Error.WriteLine($"FAILED ({ue})");
                errors++;
            }
        }

        Console.Error.Write($"  revert: {p.Path} ... ");
        if (Operations.TryRevert(p.Path, out var re))
        {
            Console.Error.WriteLine("OK");
            reverted++;
        }
        else
        {
            Console.Error.WriteLine($"FAILED ({re})");
            errors++;
        }
    }

    Console.Error.WriteLine($"Done: {reverted} reverted, {unregistered} unregistered, {errors} error(s)");
    return errors > 0 ? 1 : 0;
}

static int DetachCommand()
{
    Console.Error.Write("Detaching cldflt from C: ... ");
    int ec = Operations.AttemptDetach();
    if (ec == 0)
    {
        Console.Error.WriteLine("OK (re-attach with: fltmc attach cldflt C:)");
        return 0;
    }
    Console.Error.WriteLine($"FAILED (exit code {ec})");
    return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        unidrive-recovery — CfAPI orphan-placeholder recovery tool

        USAGE
          scan       [--path <dir>]         List cloud placeholders under <dir>
          revert     <path>                 Revert a single placeholder to a regular file
          unregister <path>                 Unregister a sync root
          clean      [--path <dir>]         Revert + unregister all cloud placeholders under <dir>
          detach                            Detach cldflt from C: (admin, last resort)

        If --path is omitted, defaults to %%USERPROFILE%%.
        """);
}
