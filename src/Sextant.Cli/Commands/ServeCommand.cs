using System.CommandLine;

namespace Sextant.Cli.Commands;

internal static class ServeCommand
{
    public static Command Build(Option<string?> dbOption, Option<string?> profileOption)
    {
        var stdioOption = new Option<bool>("--stdio") { Description = "Use stdio transport instead of HTTP" };
        var portOption = new Option<int?>("--port") { Description = "HTTP port to listen on (default: 3001)" };

        var command = new Command("serve", "Start the MCP server") { stdioOption, portOption };

        command.SetAction(async (parseResult) =>
        {
            return await Handlers.ServeHandler.RunAsync(
                parseResult.GetValue(stdioOption),
                parseResult.GetValue(portOption),
                parseResult.GetValue(dbOption),
                parseResult.GetValue(profileOption));
        });

        return command;
    }
}
