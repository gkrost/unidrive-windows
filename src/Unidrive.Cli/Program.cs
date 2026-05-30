using Unidrive.Ipc;

// Phase 0.3 smoke client: round-trips daemon.status against a running unidrive JVM daemon.
//   Usage: unidrive-win status [--profile|-p <name>]

string command = args.Length > 0 ? args[0] : "status";
string profile = "default";
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] is "--profile" or "-p")
        profile = args[i + 1];

if (command != "status")
{
    Console.Error.WriteLine("usage: unidrive-win status [--profile <name>]");
    return 2;
}

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
