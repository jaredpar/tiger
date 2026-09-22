using System.Diagnostics;
using System.Text;
using Spectre.Console;

namespace Tiger.Commands;

internal static class AgentTaskPage
{
    public static void Show(TigerDatabase db, string? repository, string agentName, string title,
        string context, bool requireInstructions = false)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold underline]Create Agent Task[/]");
        AnsiConsole.WriteLine();

        if (string.IsNullOrWhiteSpace(repository))
        {
            AnsiConsole.MarkupLine("[red]Could not determine repository for this agent task.[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            Console.ReadKey(true);
            return;
        }

        AnsiConsole.MarkupLine("[dim]The following context will be included in the agent task:[/]");
        AnsiConsole.WriteLine();
        MarkdownRenderer.Render(context);

        var instructions = BrowserUI.PromptPattern(
            requireInstructions ? "Instructions for the agent" : "Additional instructions (optional)",
            allowEmpty: !requireInstructions);
        if (instructions is null)
        {
            return;
        }

        var agentDir = Path.Combine(TigerUtils.GetConfigDirectory(), "agent-tasks");
        Directory.CreateDirectory(agentDir);
        var safeName = GetSafeFileName(agentName);
        var filePath = Path.Combine(agentDir, $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{safeName}.md");
        File.WriteAllText(filePath, BuildPrompt(title, context, instructions));

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Task file:[/] {Markup.Escape(filePath)}");
        AnsiConsole.MarkupLine($"[bold]Repository:[/] {Markup.Escape(repository)}");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Review the file, then press [blue]Enter[/] to submit or [blue]Esc[/] to cancel.[/]");
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Escape)
            {
                return;
            }
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }
        }

        AnsiConsole.MarkupLine("[dim]Submitting agent task...[/]");
        using var process = new Process();
        process.StartInfo.FileName = "gh";
        foreach (var argument in new[] { "agent-task", "create", "-F", filePath, "-R", repository })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();

        AnsiConsole.WriteLine();
        if (process.ExitCode == 0)
        {
            AnsiConsole.MarkupLine("[green]Agent task created![/]");
            if (!string.IsNullOrWhiteSpace(output))
            {
                AnsiConsole.WriteLine(output.Trim());
            }
            var sessionId = BrowserUI.ExtractSessionId(output);
            if (sessionId is not null)
            {
                db.InsertAgentTask(sessionId, repository, agentName, filePath);
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Failed to create agent task (exit code {process.ExitCode}):[/]");
            if (!string.IsNullOrWhiteSpace(error))
            {
                AnsiConsole.WriteLine(error.Trim());
            }
        }
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    internal static string GetSafeFileName(string agentName)
    {
        var name = agentName.Length > 40 ? agentName[..40] : agentName;
        return string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
    }

    internal static string BuildPrompt(string title, string context, string? instructions)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"# {title}");
        prompt.AppendLine();
        prompt.AppendLine(context.TrimEnd());
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            prompt.AppendLine();
            prompt.AppendLine("## Instructions");
            prompt.AppendLine();
            prompt.AppendLine(instructions.Trim());
        }
        return prompt.ToString();
    }
}
