using GitHub.Copilot.SDK;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Tiger.Commands;

/// <summary>
/// Opens an interactive agent chat session for CI health reporting.
/// The agent has access to the tiger CI database via skills and can
/// query builds, tests, timeline issues, and helix data.
/// </summary>
public sealed class HealthCommand : AsyncCommand
{
    public async Task RunAsync(CancellationToken ct)
    {
        await ExecuteAsync(null!, ct);
    }

    protected override async Task<int> ExecuteAsync(Spectre.Console.Cli.CommandContext context, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[bold]Tiger Health Report[/]");
        AnsiConsole.MarkupLine("[dim]Interactive agent session — type your question, 'quit' to exit[/]");
        AnsiConsole.WriteLine();

        // Get the skills directory
        var skillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
        var configDir = TigerUtils.GetConfigDirectory();
        var dbPath = Path.Combine(configDir, "tiger.db");

        // Build the system message with skill content
        var systemMessage = BuildSystemMessage(skillsDir, dbPath);

        await using var client = new CopilotClient();
        await client.StartAsync();

        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = "claude-opus-4.6",
            Streaming = true,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = new SystemMessageConfig { Content = systemMessage },
        });

        AnsiConsole.MarkupLine("[green]Agent ready.[/] Ask about CI health, build failures, test trends, etc.");
        AnsiConsole.WriteLine();

        while (!ct.IsCancellationRequested)
        {
            AnsiConsole.Markup("[blue]you>[/] ");
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input))
                continue;

            if (input.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[dim]Ending session.[/]");
                break;
            }

            var done = new TaskCompletionSource();

            using var subscription = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        Console.Write(delta.Data.DeltaContent);
                        break;
                    case SessionIdleEvent:
                        Console.WriteLine();
                        Console.WriteLine();
                        done.TrySetResult();
                        break;
                    case SessionErrorEvent err:
                        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(err.Data.Message)}[/]");
                        done.TrySetResult();
                        break;
                }
            });

            await session.SendAsync(new MessageOptions { Prompt = input });

            // Wait for the response to complete
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await done.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[yellow]Response timed out or cancelled.[/]");
            }
        }

        return 0;
    }

    private static string BuildSystemMessage(string skillsDir, string dbPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"""
            You are a CI/CD health reporting assistant for the Tiger tool. 
            You have access to a SQLite database at: {dbPath} that contains data on AzDO builds, tests, timelines, and helix work items. Use the tiger-data
            skill to query this database to answer questions AzDO builds, tests failures, timeline and helix work item data. You can use this information to 
            report on CI health, identify failure trends, and provide insights into build and test performance.

            When queried about CI health consider the following:
            - Recent build failure rates and trends
            - Test failure trends and common failure points
            - Timeline issues and their impact on build health
            - Helix work item failures and their correlation with build/test issues

            Generate a report that helps give the user insight into the current state of CI health, recent failures, and potential areas of concern
            
            """);

        // Load all skill files
        if (Directory.Exists(skillsDir))
        {
            foreach (var file in Directory.GetFiles(skillsDir, "*.md", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(file);
                sb.AppendLine(content);
                sb.AppendLine();
            }
        }

        sb.AppendLine("When answering questions:");
        sb.AppendLine("- Query the SQLite database to get real data");
        sb.AppendLine("- Provide specific numbers, build IDs, and test names");
        sb.AppendLine("- Format output clearly with summaries and details");
        sb.AppendLine("- Include links where applicable (AzDO build URLs, GitHub PR URLs)");

        return sb.ToString();
    }
}
