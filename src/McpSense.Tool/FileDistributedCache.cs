using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace McpSense.Tool;

/// <summary>
/// An <see cref="IDistributedCache"/> that keeps entries as files on disk.
/// </summary>
/// <remarks>
/// <para>
/// The point of caching model responses is that a second startup over an unchanged spec costs
/// nothing. An in-memory cache cannot deliver that: a CLI process exits between runs, so every
/// start would pay the model again. A directory of files is the smallest thing that actually
/// survives a restart, and it keeps McpSense a single process with no server to install.
/// </para>
/// <para>
/// Entries never expire. The cache key is derived from the request, which contains the operation
/// text, so editing the spec produces a different key and the stale entry is simply never read
/// again. Delete the directory to force a full rebuild.
/// </para>
/// </remarks>
internal sealed class FileDistributedCache : IDistributedCache
{
    private readonly string _directory;

    /// <summary>Creates a cache backed by the given directory, creating it if needed.</summary>
    internal FileDistributedCache(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc />
    public byte[]? Get(string key)
    {
        var path = PathFor(key);
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            // A half-written or locked entry is a cache miss, never an error.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        var path = PathFor(key);
        try
        {
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        try
        {
            File.WriteAllBytes(PathFor(key), value);
        }
        catch (IOException)
        {
            // Failing to cache is not worth failing the run over.
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        try
        {
            await File.WriteAllBytesAsync(PathFor(key), value, token);
        }
        catch (IOException)
        {
        }
    }

    /// <inheritdoc />
    public void Refresh(string key)
    {
        // Entries do not expire, so there is no sliding window to extend.
    }

    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    /// <inheritdoc />
    public void Remove(string key)
    {
        try
        {
            File.Delete(PathFor(key));
        }
        catch (IOException)
        {
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }

    /// <summary>Hashes the key, because cache keys are long and contain characters paths cannot hold.</summary>
    private string PathFor(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_directory, Convert.ToHexStringLower(hash) + ".bin");
    }
}
