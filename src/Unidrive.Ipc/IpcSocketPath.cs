using System.Security.Cryptography;
using System.Text;

namespace Unidrive.Ipc;

/// <summary>
/// Computes the unidrive daemon's IPC socket path for a profile. This MUST match
/// <c>IpcServer.defaultSocketPath</c> in the engine (core/app/sync/.../IpcServer.kt) byte-for-byte,
/// or the client connects to the wrong path.
/// </summary>
public static class IpcSocketPath
{
    // Mirrors IpcServer.MAX_SOCKET_PATH_LENGTH.
    private const int MaxSocketPathLength = 90;

    /// <summary>The socket file name for a profile (hashed if the plain name would exceed the cap).</summary>
    public static string SocketBaseName(string profile)
    {
        var name = $"unidrive-{profile}.sock";
        return name.Length > MaxSocketPathLength ? HashedSocketName(profile) : name;
    }

    /// <summary>Mirrors IpcServer.hashedSocketName: first 4 bytes of SHA-1(profile), lowercase hex.</summary>
    public static string HashedSocketName(string profile)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(profile));
        var hex = Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
        return $"unidrive-{hex}.sock";
    }

    /// <summary>
    /// Full socket path on Windows: &lt;temp&gt;/unidrive-ipc/&lt;socket-base-name&gt;, with a
    /// path-length fallback to the hashed name. <see cref="Path.GetTempPath"/> must resolve the
    /// same directory as the daemon JVM's <c>java.io.tmpdir</c> (both are %TEMP% in practice).
    /// </summary>
    public static string Resolve(string profile)
    {
        var dir = Path.Combine(Path.GetTempPath(), "unidrive-ipc");
        Directory.CreateDirectory(dir);
        var candidate = Path.Combine(dir, SocketBaseName(profile));
        return candidate.Length > MaxSocketPathLength
            ? Path.Combine(dir, HashedSocketName(profile))
            : candidate;
    }
}
