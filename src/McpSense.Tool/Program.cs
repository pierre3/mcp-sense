using Cocona;

// M0 スキャフォールド時点の CLI 骨格。
// M1 で `dump-operations`（spec 解析結果のダンプ）、M2 で `mcp`（MCP サーバー起動）を追加する。
var builder = CoconaApp.CreateBuilder();
var app = builder.Build();

app.AddCommand("version", () =>
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
    Console.WriteLine($"mcpsense {version}");
})
.WithDescription("Print the McpSense CLI version.");

app.Run();
