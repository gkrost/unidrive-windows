using Unidrive.Ipc;
using Xunit;

namespace Unidrive.Ipc.Tests;

public class IpcSocketPathTests
{
    [Fact]
    public void ShortProfile_UsesPlainName()
        => Assert.Equal("unidrive-work.sock", IpcSocketPath.SocketBaseName("work"));

    [Fact]
    public void LongProfile_FallsBackToHashedName()
    {
        // "unidrive-" (9) + profile + ".sock" (5) > 90  =>  profile longer than 76 chars hashes.
        var name = IpcSocketPath.SocketBaseName(new string('a', 100));
        Assert.Matches("^unidrive-[0-9a-f]{8}[.]sock$", name);
    }

    [Fact]
    public void HashedName_IsDeterministic()
        => Assert.Equal(IpcSocketPath.HashedSocketName("acct"), IpcSocketPath.HashedSocketName("acct"));

    [Fact]
    public void Resolve_LandsUnderTempUnidriveIpc()
    {
        var p = IpcSocketPath.Resolve("work");
        Assert.Contains("unidrive-ipc", p);
        Assert.EndsWith("unidrive-work.sock", p);
    }
}
