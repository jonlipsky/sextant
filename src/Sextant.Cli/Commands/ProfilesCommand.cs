using System.CommandLine;

namespace Sextant.Cli.Commands;

internal static class ProfilesCommand
{
    public static Command Build(Option<string?> profileOption)
    {
        var command = new Command("profiles", "List named index profiles");

        command.SetAction((parseResult) =>
        {
            Handlers.ProfilesHandler.Run(parseResult.GetValue(profileOption));
        });

        return command;
    }
}
