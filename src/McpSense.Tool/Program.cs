using Cocona;
using McpSense.Tool.Commands;

var builder = CoconaApp.CreateBuilder();
var app = builder.Build();

app.AddCommands<DumpOperationsCommand>();
app.AddCommands<DumpToolsCommand>();
app.AddCommands<McpCommand>();
app.AddCommands<LoginCommand>();

app.AddCommand("version", () =>
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
    Console.WriteLine($"mcpsense {version}");
})
.WithDescription("Print the McpSense CLI version.");

app.Run();
