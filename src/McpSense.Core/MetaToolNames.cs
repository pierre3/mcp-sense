namespace McpSense.Core;

/// <summary>
/// The names of the tools advertised in meta-tool mode.
/// </summary>
/// <remarks>
/// These three replace the full operation list once a spec grows past the threshold, so a spec with
/// hundreds of operations costs the model three tool definitions instead of hundreds.
/// </remarks>
public static class MetaToolNames
{
    /// <summary>Finds operations matching a natural-language query.</summary>
    public const string SearchOperations = "search_operations";

    /// <summary>Returns one operation's full definition, including its argument schema.</summary>
    public const string DescribeOperation = "describe_operation";

    /// <summary>Calls an operation named at call time.</summary>
    public const string CallOperation = "call_operation";

    /// <summary>The argument naming the operation, used by describe and call.</summary>
    /// <remarks>
    /// Deliberately not called <c>operationId</c>: the value is the catalog's tool name, which is
    /// the spec's <c>operationId</c> only when the spec has one and it needed no normalisation.
    /// </remarks>
    public const string OperationArgument = "operation";

    /// <summary>The argument carrying the wrapped operation's own arguments in a call.</summary>
    public const string ArgumentsArgument = "arguments";
}
