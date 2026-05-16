using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Tiger.Commands;

public class ConfigShowCommand : Command
{
    protected override int Execute(CommandContext context, CancellationToken ct)
    {
        var configDir = TigerUtils.GetConfigDirectory();
        var configPath = TigerConfig.GetConfigPath(configDir);
        var config = TigerConfig.Load(configDir);

        AnsiConsole.MarkupLine($"[bold]Config path:[/] {configPath}");
        AnsiConsole.MarkupLine($"[bold]Poll interval:[/] {config.PollIntervalSeconds}s");
        AnsiConsole.MarkupLine($"[bold]Sources:[/] {config.Sources.Count}");
        AnsiConsole.WriteLine();

        foreach (var source in config.Sources)
        {
            AnsiConsole.MarkupLine($"  [green]{source.Organization}[/] / [green]{source.Project}[/]");
            if (source.Repositories.Count > 0)
            {
                foreach (var repo in source.Repositories)
                {
                    AnsiConsole.MarkupLine($"    - {repo}");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("    [dim](all repositories)[/]");
            }
        }

        return 0;
    }
}

public class ConfigInitCommand : Command
{
    protected override int Execute(CommandContext context, CancellationToken ct)
    {
        var configDir = TigerUtils.GetConfigDirectory();
        var configPath = TigerConfig.GetConfigPath(configDir);

        if (File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[yellow]Config already exists at {configPath}[/]");
            return 0;
        }

        var config = new TigerConfig();
        config.Save(configDir);
        AnsiConsole.MarkupLine($"[green]Created default config at {configPath}[/]");
        return 0;
    }
}

public class ConfigPathCommand : Command
{
    protected override int Execute(CommandContext context, CancellationToken ct)
    {
        var configDir = TigerUtils.GetConfigDirectory();
        Console.WriteLine(TigerConfig.GetConfigPath(configDir));
        return 0;
    }
}
