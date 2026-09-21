namespace Sextant.Cli.Handlers;

internal static class ServiceHandler
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // MSBuildLocator is already registered by the CLI entry point, so the service runner can start the
        // in-process local worker without re-registering it.
        return await Sextant.Service.Host.ServiceHostRunner.RunAsync([], cancellationToken);
    }
}
