using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

public static class GetSourceContextTool
{
    [McpServerTool(Name = "get_source_context"),
     Description("Get source code lines from a file with optional context around a target line.")]
    public static string GetSourceContext(
        [Description("Absolute path to the source file")] string file_path,
        [Description("Target line number (1-indexed)")] int line,
        [Description("Number of context lines before and after the target line")] int context_lines = 5)
    {
        var context = SourceReader.ReadContext(file_path, line, context_lines);
        if (context == null)
            return ResponseBuilder.BuildEmpty("File not found.");

        var result = new
        {
            file_path,
            target_line = line,
            source = context
        };

        return ResponseBuilder.Build(new List<object> { result }, null);
    }
}
