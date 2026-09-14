using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McpSense.Core;
using McpSense.Server.Authentication;

namespace McpSense.Server;

/// <summary>The outcome of dispatching one tool call to the upstream API.</summary>
public sealed class ApiCallResult
{
    /// <summary>Whether the API answered with a success status code.</summary>
    public required bool IsSuccess { get; init; }

    /// <summary>A human-readable rendering of the response, or of the failure.</summary>
    public required string Content { get; init; }
}

/// <summary>
/// Thrown when a tool call cannot be turned into a request, for example because a required
/// argument is missing.
/// </summary>
public sealed class ToolInvocationException : Exception
{
    /// <summary>Creates a new instance.</summary>
    public ToolInvocationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Turns a tool call into an HTTP request, sends it and renders the response.
/// </summary>
public sealed class ApiDispatcher
{
    private readonly HttpClient _httpClient;
    private readonly McpSenseServerOptions _options;

    /// <summary>Creates a dispatcher.</summary>
    /// <param name="httpClient">The client used to send requests, typically from IHttpClientFactory.</param>
    /// <param name="options">Base address and credentials.</param>
    public ApiDispatcher(HttpClient httpClient, McpSenseServerOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Invokes the operation behind a tool.
    /// </summary>
    /// <param name="tool">The tool being called.</param>
    /// <param name="arguments">Arguments supplied by the client, if any.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The response, or a description of the failure.</returns>
    /// <exception cref="ToolInvocationException">The arguments do not satisfy the plan.</exception>
    public async Task<ApiCallResult> InvokeAsync(
        CatalogTool tool,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        // Only operation tools carry a plan; the meta-tools are answered by the handlers and never
        // reach the dispatcher.
        var plan = tool.Plan
            ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no invocation plan and cannot be dispatched.");

        using var request = BuildRequest(plan, arguments);
        await ApplyCredentialsAsync(request, cancellationToken).ConfigureAwait(false);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return new ApiCallResult
            {
                IsSuccess = true,
                Content = body.Length == 0
                    ? $"{(int)response.StatusCode} {response.ReasonPhrase} (empty response body)"
                    : body,
            };
        }

        // Pass the upstream body through: an API's own error payload is usually the most useful
        // thing the model can act on.
        var detail = body.Length == 0 ? "(empty response body)" : body;
        return new ApiCallResult
        {
            IsSuccess = false,
            Content = $"{(int)response.StatusCode} {response.ReasonPhrase} from {request.Method} {request.RequestUri}\n\n{detail}",
        };
    }

    internal HttpRequestMessage BuildRequest(
        ToolInvocationPlan plan,
        IDictionary<string, JsonElement>? arguments)
    {
        var path = plan.PathTemplate;
        var query = new List<string>();
        var headers = new List<(string Name, string Value)>();
        var cookies = new List<string>();

        foreach (var binding in plan.Arguments)
        {
            if (!TryGetArgument(arguments, binding.ArgumentName, out var value))
            {
                if (binding.Required)
                {
                    throw new ToolInvocationException($"Required argument '{binding.ArgumentName}' was not supplied.");
                }

                continue;
            }

            switch (binding.Location)
            {
                case OperationParameterLocation.Path:
                    path = path.Replace(
                        $"{{{binding.ParameterName}}}",
                        Uri.EscapeDataString(RenderScalar(value)),
                        StringComparison.Ordinal);
                    break;

                case OperationParameterLocation.Query:
                    // An array becomes repeated parameters, which is the OpenAPI default
                    // (style=form, explode=true) for query parameters.
                    foreach (var item in RenderMultiple(value))
                    {
                        query.Add($"{Uri.EscapeDataString(binding.ParameterName)}={Uri.EscapeDataString(item)}");
                    }

                    break;

                case OperationParameterLocation.Header:
                    headers.Add((binding.ParameterName, string.Join(",", RenderMultiple(value))));
                    break;

                case OperationParameterLocation.Cookie:
                    cookies.Add($"{binding.ParameterName}={RenderScalar(value)}");
                    break;

                default:
                    throw new ToolInvocationException(
                        $"Parameter '{binding.ParameterName}' has an unsupported location {binding.Location}.");
            }
        }

        var relative = query.Count == 0 ? path : $"{path}?{string.Join("&", query)}";
        var request = new HttpRequestMessage(plan.Method, BuildRequestUri(relative));

        foreach (var (name, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                throw new ToolInvocationException($"Header '{name}' could not be added to the request.");
            }
        }

        if (cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        }

        if (plan.BodyArgumentName is { } bodyArgument)
        {
            if (TryGetArgument(arguments, bodyArgument, out var bodyValue))
            {
                request.Content = new StringContent(
                    bodyValue.GetRawText(),
                    Encoding.UTF8,
                    plan.BodyContentType ?? "application/json");
            }
            else if (plan.BodyRequired)
            {
                throw new ToolInvocationException($"Required argument '{bodyArgument}' was not supplied.");
            }
        }

        return request;
    }

    /// <summary>
    /// Joins the base address to an OpenAPI path.
    /// </summary>
    /// <remarks>
    /// The two parts are concatenated rather than resolved through <see cref="Uri"/>. OpenAPI path
    /// templates always begin with a slash, and resolving a root-relative path against a base URL
    /// discards the base URL's own path — so a server of <c>https://example.com/v2</c> would silently
    /// lose its <c>/v2</c> prefix.
    /// </remarks>
    internal Uri BuildRequestUri(string relativePath)
    {
        var authority = _options.BaseAddress.GetLeftPart(UriPartial.Authority);
        var basePath = _options.BaseAddress.AbsolutePath.TrimEnd('/');
        return new Uri($"{authority}{basePath}{relativePath}");
    }

    private async Task ApplyCredentialsAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_options.DefaultHeaders is { Count: > 0 } defaults)
        {
            foreach (var (name, value) in defaults)
            {
                // A header the operation itself set wins: it came from the caller's arguments and
                // is specific to this request.
                if (!request.Headers.Contains(name))
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        // A provider is resolved per request so that an expiring OAuth token can be refreshed
        // without the rest of the server knowing; a static token is the degenerate case.
        var token = _options.AccessTokenProvider is { } provider
            ? await provider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false)
            : _options.BearerToken;

        if (token is { Length: > 0 })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (_options.ApiKeyHeaderName is { Length: > 0 } headerName &&
            _options.ApiKeyValue is { Length: > 0 } headerValue)
        {
            request.Headers.TryAddWithoutValidation(headerName, headerValue);
        }
    }

    private static bool TryGetArgument(
        IDictionary<string, JsonElement>? arguments,
        string name,
        out JsonElement value)
    {
        if (arguments is not null &&
            arguments.TryGetValue(name, out value) &&
            value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Renders a value for a URL or header. Strings are used as-is; everything else falls back to
    /// its JSON text, so numbers and booleans do not pick up quotes.
    /// </summary>
    private static string RenderScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(",", RenderMultiple(value)),
        _ => value.GetRawText(),
    };

    private static IEnumerable<string> RenderMultiple(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return [RenderScalar(value)];
        }

        return value.EnumerateArray().Select(RenderScalar).ToArray();
    }
}
