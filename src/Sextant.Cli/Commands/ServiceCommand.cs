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

        command.Subcommands.Add(BuildBackup());
        command.Subcommands.Add(BuildRestore());

        return command;
    }

    private static Command BuildBackup()
    {
        var dirArgument = new Argument<string>("directory")
        {
            Description = "Destination directory for the consistent catalog + artifact backup."
        };
        var backup = new Command(
            "backup",
            "Write a consistent backup of the service catalog + immutable artifact volume (criterion 6). " +
            "Credentials are never included; the manifest lists the environment variables to re-provide on restore.")
        {
            dirArgument
        };
        backup.SetAction(async (parseResult, cancellationToken) =>
            await Handlers.ServiceHandler.BackupAsync(parseResult.GetValue(dirArgument)!, cancellationToken));
        return backup;
    }

    private static Command BuildRestore()
    {
        var dirArgument = new Argument<string>("directory")
        {
            Description = "Backup directory (containing manifest.json) to restore the catalog + artifacts from."
        };
        var restore = new Command(
            "restore",
            "Restore a service backup's catalog + artifact volume, then start `sextant service` to run " +
            "migrations, recover, and re-enforce authorization on the restored catalog (criterion 6).")
        {
            dirArgument
        };
        restore.SetAction(async (parseResult, cancellationToken) =>
            await Handlers.ServiceHandler.RestoreAsync(parseResult.GetValue(dirArgument)!, cancellationToken));
        return restore;
    }
}
