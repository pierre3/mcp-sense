using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Validation findings are deliberately treated as non-fatal. The reader applies a strict rule set
/// that production specs routinely violate while remaining perfectly usable — LINE's published
/// messaging-api spec, for example, trips the discriminator rule. McpSense proxies whatever API the
/// user points it at, so refusing a spec the API vendor themselves ship would make the tool
/// unusable. A read fails only when no document could be produced at all.
/// </para>
/// </remarks>
public static class OpenApiSpecLoader
{
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

        var result = await OpenApiDocument.LoadAsync(source, CreateReaderSettings(), cancellationToken)
            .ConfigureAwait(false);

        return Interpret(result, source);
    }

    /// <summary>
    /// Reads a spec from a stream.
    /// </summary>
    /// <param name="stream">The stream holding the spec.</param>
    /// <param name="format">"yaml" or "json". When <c>null</c>, the reader infers it.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The document together with any parser findings.</returns>
    /// <exception cref="OpenApiSpecLoadException">No document could be produced.</exception>
    public static async Task<OpenApiSpecLoadResult> LoadAsync(
        Stream stream,
        string? format = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var result = await OpenApiDocument.LoadAsync(stream, format, CreateReaderSettings(), cancellationToken)
            .ConfigureAwait(false);

        return Interpret(result, "<stream>");
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
