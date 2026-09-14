using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpSense.Server.Authentication;

namespace McpSense.Tool;

/// <summary>
/// Stores OAuth tokens as files under a per-user directory.
/// </summary>
/// <remarks>
/// <para>
/// Tokens have to outlive the process: a CLI exits after every invocation, so holding them in
/// memory would send the user back through the browser on each start and make the refresh token
/// pointless.
/// </para>
/// <para>
/// On Windows the file is encrypted with DPAPI, scoped to the current user, so another account on
/// the machine cannot read it. DPAPI has no counterpart on Linux or macOS, so there the file is
/// written with owner-only permissions and is otherwise plaintext — the same posture as the
/// credential files most CLI tools write, and worth knowing before storing a token with wide scope.
/// </para>
/// </remarks>
internal sealed class FileTokenStore : ITokenStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string _directory;

    /// <summary>Creates a store under the given directory, creating it if needed.</summary>
    internal FileTokenStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>The default location, alongside the other per-user McpSense state.</summary>
    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "mcpsense",
        "tokens");

    /// <inheritdoc />
    public async ValueTask<OAuthTokens?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stored = await File.ReadAllBytesAsync(path, cancellationToken);
            var json = Unprotect(stored);
            return JsonSerializer.Deserialize<OAuthTokens>(json, SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException)
        {
            // An unreadable token file is treated as "not logged in" so that `mcpsense login` can
            // simply overwrite it.
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(string key, OAuthTokens tokens, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        var json = JsonSerializer.SerializeToUtf8Bytes(tokens, SerializerOptions);

        await File.WriteAllBytesAsync(path, Protect(json), cancellationToken);
        RestrictToOwner(path);
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            File.Delete(PathFor(key));
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private static byte[] Protect(byte[] plaintext)
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser)
            : plaintext;

    private static byte[] Unprotect(byte[] stored)
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ProtectedData.Unprotect(stored, optionalEntropy: null, DataProtectionScope.CurrentUser)
            : stored;

    /// <summary>Removes group and world access on platforms where the file is not encrypted.</summary>
    private static void RestrictToOwner(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Hashes the key, which contains URLs and scopes that a filename cannot hold.</summary>
    private string PathFor(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_directory, Convert.ToHexStringLower(hash) + ".token");
    }
}
