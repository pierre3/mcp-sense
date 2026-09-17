using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace McpSense.Core;

/// <summary>
/// Thrown when a spec cannot be read at all. Details are carried in <see cref="Problems"/>.
/// </summary>
public sealed class OpenApiSpecLoadException : Exception
{
    /// <summary>What the parser reported.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Creates a new instance.</summary>
    public OpenApiSpecLoadException(string message, IReadOnlyList<string> problems)
        : base(message)
    {
        Problems = problems;
    }
}

/// <summary>The outcome of reading a spec.</summary>
public sealed class OpenApiSpecLoadResult
{
    /// <summary>The document that was read.</summary>
    public required OpenApiDocument Document { get; init; }

    /// <summary>
    /// Validation findings the parser classified as errors. These are non-fatal: the document was
    /// still built and is usable. See the remarks on <see cref="OpenApiSpecLoader"/> for why.
    /// </summary>
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>Warnings reported by the parser.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>The OpenAPI version detected in the document.</summary>
    public required OpenApiSpecVersion SpecVersion { get; init; }
}

/// <summary>
/// Reads an OpenAPI document from a local file path, a URL or a stream.
/// </summary>
/// <remarks>
/// <para>
/// This is the single place that configures OpenAPI readers, so every caller gets the same
/// behaviour. That matters because Microsoft.OpenApi reads only JSON out of the box: YAML support
/// comes from the separate Microsoft.OpenApi.YamlReader package and has to be registered
/// explicitly. Without that registration a YAML spec fails with "Format 'yaml' is not supported".
/// </para>
/// <para>
/// Downloads happen here rather than inside the reader, which takes the format from the response's
/// Content-Type alone. Public specs are routinely served under a type that says nothing about their
/// syntax — raw.githubusercontent.com labels every file text/plain — and the reader then rejects a
/// perfectly good document with "Format 'plain' is not supported". The format is taken from the URL
/// or file extension first, from the media type second, and from the first character of the content
/// last, so a spec is read on its merits rather than on its labelling.
/// </para>
/// <para>
/// Validation findings are deliberately treated as non-fatal. The reader applies a strict rule set
/// that production specs routinely violate while remaining perfectly usable — LINE's published
/// messaging-api spec, for example, trips the discriminator rule. McpSense proxies whatever API the
/// user points it at, so refusing a spec the API vendor themselves ship would make the tool
/// unusable. A read fails only when no document could be produced at all.
/// </para>
/// <para>
/// Every way of failing to produce a document — a missing file, a refused connection, a 404, an
/// unreadable payload — surfaces as <see cref="OpenApiSpecLoadException"/>, so callers have one
/// thing to catch and the CLI can report the cause instead of a stack trace.
/// </para>
/// </remarks>
public static class OpenApiSpecLoader
{
    private const string JsonFormat = "json";
    private const string YamlFormat = "yaml";

    /// <summary>
    /// Reads a spec from a file path or URL.
    /// </summary>
    /// <param name="source">A local file path or the URL of the spec.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The document together with any parser findings.</returns>
    /// <exception cref="OpenApiSpecLoadException">No document could be produced.</exception>
    public static async Task<OpenApiSpecLoadResult> LoadAsync(string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var isUrl = Uri.TryCreate(source, UriKind.Absolute, out var url)
            && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);

        return isUrl
            ? await LoadFromUrlAsync(url!, cancellationToken).ConfigureAwait(false)
            : await LoadFromFileAsync(source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a spec from a stream.
    /// </summary>
    /// <param name="stream">The stream holding the spec.</param>
    /// <param name="format">"yaml" or "json". When <c>null</c>, it is inferred from the content.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The document together with any parser findings.</returns>
    /// <exception cref="OpenApiSpecLoadException">No document could be produced.</exception>
    public static async Task<OpenApiSpecLoadResult> LoadAsync(
        Stream stream,
        string? format = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return await ReadAsync(stream, format, baseUrl: null, "<stream>", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Downloads a spec and reads it.</summary>
    /// <exception cref="OpenApiSpecLoadException">The download failed or produced no document.</exception>
    private static async Task<OpenApiSpecLoadResult> LoadFromUrlAsync(Uri url, CancellationToken cancellationToken)
    {
        // A spec is downloaded once per process, at startup, so a client per call costs nothing and
        // keeps this type free of shared state.
        using var client = new HttpClient();

        HttpResponseMessage response;
        try
        {
            response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OpenApiSpecLoadException($"Failed to download an OpenAPI document from '{url}'.", [ex.Message]);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OpenApiSpecLoadException(
                $"Failed to download an OpenAPI document from '{url}'.",
                ["The request timed out."]);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The reader ignores the status code, so without this check an error page would be
                // parsed as if it were the spec.
                throw new OpenApiSpecLoadException(
                    $"Failed to download an OpenAPI document from '{url}'.",
                    [$"The server responded {(int)response.StatusCode} {response.ReasonPhrase}."]);
            }

            var format = InferFormatFromPath(url.AbsolutePath)
                ?? InferFormatFromMediaType(response.Content.Headers.ContentType?.MediaType);

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ReadAsync(stream, format, url, url.ToString(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Opens a spec file and reads it.</summary>
    /// <exception cref="OpenApiSpecLoadException">The file could not be opened or produced no document.</exception>
    private static async Task<OpenApiSpecLoadResult> LoadFromFileAsync(string path, CancellationToken cancellationToken)
    {
        string fullPath;
        FileStream file;
        try
        {
            fullPath = Path.GetFullPath(path);
            file = File.OpenRead(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new OpenApiSpecLoadException($"Failed to read an OpenAPI document from '{path}'.", [ex.Message]);
        }

        using (file)
        {
            return await ReadAsync(file, InferFormatFromPath(fullPath), new Uri(fullPath), path, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Buffers the content, settles on a format if one is not known yet, and parses it.
    /// </summary>
    /// <exception cref="OpenApiSpecLoadException">No document could be produced.</exception>
    private static async Task<OpenApiSpecLoadResult> ReadAsync(
        Stream content,
        string? format,
        Uri? baseUrl,
        string source,
        CancellationToken cancellationToken)
    {
        // Buffering is what lets an unknown format be settled by looking at the content, and costs
        // nothing in practice: the reader materialises the whole document anyway.
        using var buffer = new MemoryStream();
        try
        {
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new OpenApiSpecLoadException($"Failed to read an OpenAPI document from '{source}'.", [ex.Message]);
        }

        buffer.Position = 0;
        format ??= SniffFormat(buffer);

        var settings = CreateReaderSettings();
        if (baseUrl is not null)
        {
            settings.BaseUrl = baseUrl;
        }

        ReadResult result;
        try
        {
            result = await OpenApiDocument.LoadAsync(buffer, format, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing the reader throws is worth a stack trace to someone who pointed the tool at
            // the wrong file.
            throw new OpenApiSpecLoadException($"Failed to read an OpenAPI document from '{source}'.", [ex.Message]);
        }

        return Interpret(result, source);
    }

    /// <summary>Maps a file or URL extension to a reader format, or <c>null</c> when it says nothing.</summary>
    private static string? InferFormatFromPath(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => JsonFormat,
            ".yaml" or ".yml" => YamlFormat,
            _ => null,
        };

    /// <summary>
    /// Maps a media type to a reader format, or <c>null</c> when it says nothing about the syntax.
    /// </summary>
    /// <remarks>
    /// Matching on a substring covers the structured suffixes too: application/vnd.oai.openapi+json
    /// and application/openapi+yaml are both in use.
    /// </remarks>
    private static string? InferFormatFromMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        var value = mediaType.ToLowerInvariant();

        return value.Contains("yaml") ? YamlFormat
            : value.Contains("json") ? JsonFormat
            : null;
    }

    /// <summary>
    /// Settles the format from the content: a JSON document opens with an object or an array, and
    /// anything else is read as YAML. The stream position is left where it was found.
    /// </summary>
    private static string SniffFormat(MemoryStream buffer)
    {
        var position = buffer.Position;
        try
        {
            int value;
            while ((value = buffer.ReadByte()) >= 0)
            {
                // Skip a UTF-8 byte order mark and any leading whitespace.
                if (value is 0xEF or 0xBB or 0xBF or 0x20 or 0x09 or 0x0D or 0x0A)
                {
                    continue;
                }

                return value is '{' or '[' ? JsonFormat : YamlFormat;
            }

            return YamlFormat;
        }
        finally
        {
            buffer.Position = position;
        }
    }

    /// <summary>Builds reader settings with every format McpSense supports registered.</summary>
    private static OpenApiReaderSettings CreateReaderSettings()
    {
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        return settings;
    }

    private static OpenApiSpecLoadResult Interpret(ReadResult result, string source)
    {
        var problems = result.Diagnostic?.Errors?.Select(static e => e.ToString()).ToArray() ?? Array.Empty<string>();

        if (result.Document is null)
        {
            throw new OpenApiSpecLoadException($"Failed to read an OpenAPI document from '{source}'.", problems);
        }

        return new OpenApiSpecLoadResult
        {
            Document = result.Document,
            Problems = problems,
            Warnings = result.Diagnostic?.Warnings?.Select(static w => w.ToString()).ToArray() ?? Array.Empty<string>(),
            SpecVersion = result.Diagnostic?.SpecificationVersion ?? OpenApiSpecVersion.OpenApi3_0,
        };
    }
}
