using System.Net;
using System.Text.Json;
using Spectre.Console.Cli;
using Tiger.Commands;
using Xunit;

namespace Tiger.Tests;

public class AzdoWaitTests
{
    private const string JobId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    [InlineData("canceled")]
    [InlineData("partiallySucceeded")]
    public async Task WholeBuild_PollsUntilCompleted(string outcome)
    {
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requests.Add($"{request.Method} {request.RequestUri}");
            return Task.FromResult(JsonResponse(requests.Count == 1
                ? Build("inProgress")
                : Build("completed", outcome)));
        }), "test-org", "test-project");
        var delays = new List<double>();
        using var progress = new StringWriter();

        var result = await AzdoWaitCommand.WaitAsync(client, new() { BuildId = 42, PollIntervalSeconds = 7 },
            progress, delay: (interval, ct) =>
            {
                delays.Add(interval.TotalSeconds);
                return Task.CompletedTask;
            });

        Assert.Equal("""
            GET https://dev.azure.com/test-org/test-project/_apis/build/builds/42?api-version=7.1
            GET https://dev.azure.com/test-org/test-project/_apis/build/builds/42?api-version=7.1
            """, string.Join("\n", requests), ignoreLineEndingDifferences: true);
        Assert.Equal("7", string.Join("\n", delays));
        Assert.Equal($$"""
            {
              "build": {
                "id": 42,
                "buildNumber": "42",
                "status": "completed",
                "result": "{{outcome}}",
                "uri": "https://dev.azure.com/test-org/test-project/_build/results?buildId=42",
                "sourceBranch": "refs/heads/main",
                "definitionName": "CI",
                "definitionId": 7,
                "sourceVersion": null,
                "repositoryName": null,
                "repositoryType": null,
                "prNumber": null,
                "finishTime": null
              },
              "jobs": []
            }
            """, JsonSerializer.Serialize(result, JsonOptions.Indented), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public async Task JobName_WaitsForAllSubstringMatchesAndPrintsChanges()
    {
        var checks = 0;
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/timeline"))
            {
                return Task.FromResult(JsonResponse(Build("inProgress")));
            }
            checks++;
            return Task.FromResult(JsonResponse($$"""
                {"records":[
                  {"id":"stage","name":"Test stage","type":"Stage","state":"inProgress"},
                  {"id":"other","name":"Build","type":"Job","parentId":"stage","state":"inProgress"},
                  {"id":"task","name":"Test task","type":"Task","parentId":"b","state":"inProgress"},
                  {"id":"a","name":"TEST Linux","type":"Job","parentId":"stage","order":1,"state":"completed","result":"failed"},
                  {"id":"b","name":"Windows test","type":"Job","parentId":"stage","order":2,"state":"{{(checks < 3 ? "inProgress" : "completed")}}","result":{{(checks < 3 ? "null" : "\"skipped\"")}}}
                ]}
                """));
        }));
        using var progress = new StringWriter();

        var result = await AzdoWaitCommand.WaitAsync(client, new() { BuildId = 42, JobName = "test" },
            progress, delay: NoDelay);

        Assert.Equal(3, checks);
        Assert.Equal("inProgress", result.Build.Status);
        Assert.Equal("""
            a: TEST Linux = completed/failed
            b: Windows test = completed/skipped
            """, string.Join("\n", result.Jobs.Select(j => $"{j.Id}: {j.Name} = {j.State}/{j.Result}")), ignoreLineEndingDifferences: true);
        Assert.Equal("""
            {
              "buildId": 42,
              "status": "inProgress",
              "result": null,
              "jobs": [
                {
                  "id": "a",
                  "name": "TEST Linux",
                  "state": "completed",
                  "result": "failed"
                },
                {
                  "id": "b",
                  "name": "Windows test",
                  "state": "inProgress",
                  "result": null
                }
              ],
              "pendingJobSources": []
            }
            {
              "buildId": 42,
              "status": "inProgress",
              "result": null,
              "jobs": [
                {
                  "id": "a",
                  "name": "TEST Linux",
                  "state": "completed",
                  "result": "failed"
                },
                {
                  "id": "b",
                  "name": "Windows test",
                  "state": "completed",
                  "result": "skipped"
                }
              ],
              "pendingJobSources": []
            }
            """, progress.ToString().TrimEnd(), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public async Task JobName_WaitsForMatchesInPhasesThatHaveNotStarted()
    {
        var checks = 0;
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/timeline"))
            {
                return Task.FromResult(JsonResponse(Build("inProgress")));
            }
            checks++;
            // The second stage's Job records only appear once that stage starts running.
            var later = checks switch
            {
                1 => """{"id":"p2","name":"Phase2","type":"Phase","parentId":"s2","order":1,"state":"pending"}""",
                2 => """
                    {"id":"p2","name":"Phase2","type":"Phase","parentId":"s2","order":1,"state":"inProgress"},
                    {"id":"b","name":"Test Windows","type":"Job","parentId":"p2","order":1,"state":"inProgress"}
                    """,
                _ => """
                    {"id":"p2","name":"Phase2","type":"Phase","parentId":"s2","order":1,"state":"completed"},
                    {"id":"b","name":"Test Windows","type":"Job","parentId":"p2","order":1,"state":"completed","result":"succeeded"}
                    """,
            };
            return Task.FromResult(JsonResponse($$"""
                {"records":[
                  {"id":"s1","name":"Stage1","type":"Stage","order":1,"state":"completed"},
                  {"id":"p1","name":"Phase1","type":"Phase","parentId":"s1","order":1,"state":"completed"},
                  {"id":"a","name":"Test Linux","type":"Job","parentId":"p1","order":0,"state":"completed","result":"succeeded"},
                  {"id":"s2","name":"Stage2","type":"Stage","order":2,"state":"{{(checks < 3 ? "inProgress" : "completed")}}"},
                  {{later}}
                ]}
                """));
        }));
        using var progress = new StringWriter();

        var result = await AzdoWaitCommand.WaitAsync(client, new() { BuildId = 42, JobName = "test" },
            progress, delay: NoDelay);

        Assert.Equal(3, checks);
        Assert.Equal("""
            a: Test Linux = completed/succeeded
            b: Test Windows = completed/succeeded
            """, string.Join("\n", result.Jobs.Select(j => $"{j.Id}: {j.Name} = {j.State}/{j.Result}")), ignoreLineEndingDifferences: true);
        Assert.Equal("""
            {
              "buildId": 42,
              "status": "inProgress",
              "result": null,
              "jobs": [
                {
                  "id": "a",
                  "name": "Test Linux",
                  "state": "completed",
                  "result": "succeeded"
                }
              ],
              "pendingJobSources": [
                {
                  "id": "p2",
                  "name": "Phase2",
                  "type": "Phase",
                  "state": "pending"
                },
                {
                  "id": "s2",
                  "name": "Stage2",
                  "type": "Stage",
                  "state": "inProgress"
                }
              ]
            }
            {
              "buildId": 42,
              "status": "inProgress",
              "result": null,
              "jobs": [
                {
                  "id": "a",
                  "name": "Test Linux",
                  "state": "completed",
                  "result": "succeeded"
                },
                {
                  "id": "b",
                  "name": "Test Windows",
                  "state": "inProgress",
                  "result": null
                }
              ],
              "pendingJobSources": []
            }
            {
              "buildId": 42,
              "status": "inProgress",
              "result": null,
              "jobs": [
                {
                  "id": "a",
                  "name": "Test Linux",
                  "state": "completed",
                  "result": "succeeded"
                },
                {
                  "id": "b",
                  "name": "Test Windows",
                  "state": "completed",
                  "result": "succeeded"
                }
              ],
              "pendingJobSources": []
            }
            """, progress.ToString().TrimEnd(), ignoreLineEndingDifferences: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("test")]
    public async Task JobId_SelectsExactGuidAndCombinesWithName(string? name)
    {
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
            Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("/timeline")
                ? $$"""
                    {"records":[
                      {"id":"{{JobId}}","name":"Test Linux","type":"Job","state":"completed","result":"succeeded"},
                      {"id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","name":"Test Windows","type":"Job","state":"inProgress"}
                    ]}
                    """
                : Build("inProgress")))));
        using var progress = new StringWriter();

        var result = await AzdoWaitCommand.WaitAsync(client,
            new() { BuildId = 42, JobId = JobId.ToUpperInvariant(), JobName = name }, progress);

        Assert.Equal($"{JobId}: Test Linux", string.Join("\n", result.Jobs.Select(j => $"{j.Id}: {j.Name}")));
    }

    [Fact]
    public async Task Jobs_AreRediscoveredWhileWaitingForMatches()
    {
        var checks = 0;
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/timeline"))
            {
                return Task.FromResult(JsonResponse(Build("inProgress")));
            }
            checks++;
            return Task.FromResult(JsonResponse(checks == 1 ? """{"records":[]}""" : """
                {"records":[{"id":"a","name":"Test","type":"Job","state":"completed"}]}
                """));
        }));
        using var progress = new StringWriter();

        var result = await AzdoWaitCommand.WaitAsync(client, new() { BuildId = 42, JobName = "test" },
            progress, delay: NoDelay);

        Assert.Equal(2, checks);
        Assert.Equal("a", string.Join("\n", result.Jobs.Select(j => j.Id)));
    }

    [Fact]
    public async Task QueuedBuild_WaitsForTimelineToBecomeAvailable()
    {
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse(requests.Count switch
            {
                1 => Build("notStarted"),
                2 => Build("inProgress"),
                3 => """{"records":[{"id":"a","name":"Test","type":"Job","state":"completed"}]}""",
                _ => throw new InvalidOperationException("Unexpected request"),
            }));
        }));
        using var progress = new StringWriter();

        var result = await AzdoWaitCommand.WaitAsync(client, new() { BuildId = 42, JobName = "test" },
            progress, delay: NoDelay);

        Assert.Equal("""
            /dnceng-public/public/_apis/build/builds/42
            /dnceng-public/public/_apis/build/builds/42
            /dnceng-public/public/_apis/build/builds/42/timeline
            """, string.Join("\n", requests), ignoreLineEndingDifferences: true);
        Assert.Equal("a", string.Join("\n", result.Jobs.Select(j => j.Id)));
    }

    [Theory]
    [InlineData(null, "missing", "completed", "Build 42 completed without any jobs matching the supplied filters.")]
    [InlineData(JobId, "missing", "completed", "Build 42 completed without any jobs matching the supplied filters.")]
    [InlineData(null, "test", "inProgress", "Build 42 completed but matching jobs are not marked completed.")]
    public async Task CompletedBuild_RejectsMissingOrUnfinishedMatches(string? id, string name, string state, string error)
    {
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
            Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("/timeline")
                ? $$"""{"records":[{"id":"{{JobId}}","name":"Test","type":"Job","state":"{{state}}"}]}"""
                : Build("completed", "canceled")))));
        using var progress = new StringWriter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => AzdoWaitCommand.WaitAsync(
            client, new() { BuildId = 42, JobId = id, JobName = name }, progress));

        Assert.Equal(error, exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_StopsRequestsAndDelay(bool duringRequest)
    {
        using var cancellation = new CancellationTokenSource();
        var requests = 0;
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requests++;
            if (duringRequest)
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return Task.FromResult(JsonResponse(Build("inProgress")));
        }));
        using var progress = new StringWriter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AzdoWaitCommand.WaitAsync(
            client, new() { BuildId = 42 }, progress, cancellation.Token, (interval, ct) =>
            {
                cancellation.Cancel();
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }));

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Timeout_StopsWaiting()
    {
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
            Task.FromResult(JsonResponse(Build("inProgress")))));
        using var progress = new StringWriter();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => AzdoWaitCommand.WaitAsync(
            client, new() { BuildId = 42, TimeoutSeconds = 1 }, progress,
            delay: (interval, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct)));

        Assert.Equal("Timed out after 1 seconds waiting for build 42.", exception.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task HttpErrors_AreNotTreatedAsCompletion(HttpStatusCode status)
    {
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
            Task.FromResult(new HttpResponseMessage(status))));
        using var progress = new StringWriter();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            AzdoWaitCommand.WaitAsync(client, new() { BuildId = 42 }, progress));

        Assert.Equal(status, exception.StatusCode);
        Assert.Equal("", progress.ToString());
    }

    [Theory]
    [InlineData(0, null, null, 10, null, "The build ID must be positive.")]
    [InlineData(42, " ", null, 10, null, "--job-name must not be empty or whitespace.")]
    [InlineData(42, null, "bad-id", 10, null, "--job-id must be a timeline record GUID.")]
    [InlineData(42, null, null, 0, null, "--poll-interval must be positive.")]
    [InlineData(42, null, null, 10, 0, "--timeout must be positive.")]
    public void Settings_RejectInvalidOptions(int buildId, string? name, string? id, int interval, int? timeout, string error)
    {
        var settings = new AzdoWaitCommand.Settings
        {
            BuildId = buildId, JobName = name, JobId = id,
            PollIntervalSeconds = interval, TimeoutSeconds = timeout,
        };

        Assert.Equal(error, settings.Validate().Message);
    }

    [Fact]
    public async Task Command_ParsesFiltersAndWaitOptions()
    {
        var app = new CommandApp<ParseWaitCommand>();

        var exitCode = await app.RunAsync(["42", "--job-name", "test", "--job-id", JobId,
            "--poll-interval", "15", "--timeout", "3600", "--org", "test-org", "--project", "test-project"]);

        Assert.Equal(0, exitCode);
    }

    public sealed class ParseWaitCommand : AsyncCommand<AzdoWaitCommand.Settings>
    {
        public override Task<int> ExecuteAsync(CommandContext context, AzdoWaitCommand.Settings settings, CancellationToken ct)
        {
            Assert.Equal("""
                {"BuildId":42,"JobName":"test","JobId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","PollIntervalSeconds":15,"TimeoutSeconds":3600,"Organization":"test-org","Project":"test-project"}
                """, JsonSerializer.Serialize(new
                {
                    settings.BuildId, settings.JobName, settings.JobId, settings.PollIntervalSeconds,
                    settings.TimeoutSeconds, settings.Organization, settings.Project,
                }));
            return Task.FromResult(0);
        }
    }

    private static string Build(string status, string? result = null) => $$"""
        {"id":42,"buildNumber":"42","status":"{{status}}","result":{{JsonSerializer.Serialize(result)}},
         "uri":"vstfs:///Build/Build/42","sourceBranch":"refs/heads/main","definition":{"id":7,"name":"CI"}
        }
        """;

    private static Task NoDelay(TimeSpan interval, CancellationToken ct) => Task.CompletedTask;

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
