using System.CommandLine;

namespace Sextant.Cli.Commands;

internal static class ServiceCommand
{
    public static Command Build()
    {
        var command = new Command(
            "service",
            "Run the standalone Sextant index service (durable snapshot catalog + HTTP control/query/MCP). " +
            "Configuration comes from SEXTANT_SERVICE_* environment variables; this is additive and never " +
            "required for local indexing or `sextant serve`.");

        command.SetAction(async (parseResult, cancellationToken) =>
            await Handlers.ServiceHandler.RunAsync(cancellationToken));

        return command;
    }
}
