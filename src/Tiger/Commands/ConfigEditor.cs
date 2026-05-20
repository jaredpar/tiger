using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Interactive configuration display and editor.
/// Shows current config and allows adding/removing orgs and repos.
/// </summary>
public sealed class ConfigEditor
{
    private readonly TigerConfig _config;
    private readonly string _configDirectory;

    /// <summary>
    /// True if any configuration changes were made during this session.
    /// </summary>
    public bool Changed { get; private set; }

    public ConfigEditor(TigerConfig config, string configDirectory)
    {
        _config = config;
        _configDirectory = configDirectory;
    }

    public void Show()
    {
        while (true)
        {
            AnsiConsole.Clear();
            RenderConfig();

            AnsiConsole.MarkupLine("[bold]Actions:[/]");
            AnsiConsole.MarkupLine("  [blue]A[/] Add source (org/project)");
            AnsiConsole.MarkupLine("  [blue]D[/] Delete source");
            AnsiConsole.MarkupLine("  [blue]R[/] Add repository to a source");
            AnsiConsole.MarkupLine("  [blue]X[/] Remove repository from a source");
            AnsiConsole.MarkupLine("  [blue]Esc[/] Back to menu");

            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.A:
                    AddSource();
                    break;
                case ConsoleKey.D:
                    DeleteSource();
                    break;
                case ConsoleKey.R:
                    AddRepository();
                    break;
                case ConsoleKey.X:
                    RemoveRepository();
                    break;
                case ConsoleKey.Escape:
                    return;
            }
        }
    }

    private void RenderConfig()
    {
        AnsiConsole.MarkupLine("[bold underline]Configuration[/]");
        AnsiConsole.MarkupLine($"[dim]{TigerConfig.GetConfigPath(_configDirectory)}[/]");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Setting");
        table.AddColumn("Value");
        table.AddRow("Poll interval", $"{_config.PollIntervalSeconds}s");
        table.AddRow("Backfill days", _config.BackfillDays.ToString());
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (_config.Sources.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No sources configured.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[bold]Sources:[/]");
            for (var i = 0; i < _config.Sources.Count; i++)
            {
                var source = _config.Sources[i];
                AnsiConsole.MarkupLine($"  [green]{i + 1}.[/] [bold]{Markup.Escape(source.Organization)}[/] / [bold]{Markup.Escape(source.Project)}[/]");
                if (source.Repositories.Count > 0)
                {
                    foreach (var repo in source.Repositories)
                        AnsiConsole.MarkupLine($"       - {Markup.Escape(repo)}");
                }
                else
                {
                    AnsiConsole.MarkupLine("       [dim](all repositories)[/]");
                }
            }
        }
        AnsiConsole.WriteLine();
    }

    private void AddSource()
    {
        AnsiConsole.WriteLine();
        var org = BrowserUI.PromptPattern("Organization (e.g. dnceng-public):");
        if (org is null) return;

        var project = BrowserUI.PromptPattern("Project (e.g. public):");
        if (project is null) return;

        // Check for duplicate
        if (_config.Sources.Any(s =>
            s.Organization.Equals(org, StringComparison.OrdinalIgnoreCase) &&
            s.Project.Equals(project, StringComparison.OrdinalIgnoreCase)))
        {
            AnsiConsole.MarkupLine($"[yellow]Source {Markup.Escape(org)}/{Markup.Escape(project)} already exists.[/]");
            Console.ReadKey(true);
            return;
        }

        _config.Sources.Add(new AzdoSource
        {
            Organization = org,
            Project = project,
            Repositories = [],
        });
        _config.Save(_configDirectory);
        Changed = true;
        AnsiConsole.MarkupLine($"[green]Added source {Markup.Escape(org)}/{Markup.Escape(project)}[/]");
        Console.ReadKey(true);
    }

    private void DeleteSource()
    {
        if (_config.Sources.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No sources to delete.[/]");
            Console.ReadKey(true);
            return;
        }

        AnsiConsole.WriteLine();
        var choices = _config.Sources.Select(s =>
            $"{s.Organization}/{s.Project} ({s.Repositories.Count} repos)").ToList();

        var selected = BrowserUI.SelectWithEscape("Select source to delete:", choices);
        if (selected < 0) return;

        var source = _config.Sources[selected];
        _config.Sources.RemoveAt(selected);
        _config.Save(_configDirectory);
        Changed = true;
        AnsiConsole.MarkupLine($"[green]Deleted source {Markup.Escape(source.Organization)}/{Markup.Escape(source.Project)}[/]");
        Console.ReadKey(true);
    }

    private void AddRepository()
    {
        var source = SelectSource("Select source to add repository to:");
        if (source is null) return;

        var repo = BrowserUI.PromptPattern("Repository (e.g. dotnet/roslyn):");
        if (repo is null) return;

        if (source.Repositories.Contains(repo, StringComparer.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(repo)} already exists in this source.[/]");
            Console.ReadKey(true);
            return;
        }

        source.Repositories.Add(repo);
        _config.Save(_configDirectory);
        Changed = true;
        AnsiConsole.MarkupLine($"[green]Added {Markup.Escape(repo)} to {Markup.Escape(source.Organization)}/{Markup.Escape(source.Project)}[/]");
        Console.ReadKey(true);
    }

    private void RemoveRepository()
    {
        var source = SelectSource("Select source to remove repository from:");
        if (source is null) return;

        if (source.Repositories.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]This source monitors all repositories (none to remove).[/]");
            Console.ReadKey(true);
            return;
        }

        AnsiConsole.WriteLine();
        var selected = BrowserUI.SelectWithEscape("Select repository to remove:", source.Repositories);
        if (selected < 0) return;

        var repo = source.Repositories[selected];
        source.Repositories.RemoveAt(selected);
        _config.Save(_configDirectory);
        Changed = true;
        AnsiConsole.MarkupLine($"[green]Removed {Markup.Escape(repo)} from {Markup.Escape(source.Organization)}/{Markup.Escape(source.Project)}[/]");
        Console.ReadKey(true);
    }

    private AzdoSource? SelectSource(string title)
    {
        if (_config.Sources.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No sources configured.[/]");
            Console.ReadKey(true);
            return null;
        }

        if (_config.Sources.Count == 1)
            return _config.Sources[0];

        AnsiConsole.WriteLine();
        var choices = _config.Sources.Select(s =>
            $"{s.Organization}/{s.Project}").ToList();

        var selected = BrowserUI.SelectWithEscape(title, choices);
        return selected >= 0 ? _config.Sources[selected] : null;
    }
}
