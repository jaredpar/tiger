using System.Net;
using System.Text.Json;
using Azure.Core;
using Spectre.Console.Cli;
using Tiger.Commands;
using Xunit;

namespace Tiger.Tests;

public class AzdoRetryTests
{
    private const string OriginalBuild = """
        {
          "id": 42,
          "buildNumber": "20261008.1",
          "status": "completed",
          "result": "failed",
          "uri": "vstfs:///Build/Build/42",
          "sourceBranch": "refs/pull/42/merge",
          "sourceVersion": "abc123",
          "definition": { "id": 7, "name": "CI" },
          "repository": { "id": "dotnet/roslyn", "type": "GitHub" },
          "parameters": "{\"configuration\":\"Debug\"}",
          "templateParameters": { "runTests": true, "configurations": ["Debug", "Release"] }
        }
        """;

    private const string RetriedBuild = """
        {
          "id": 43,
          "buildNumber": "20261008.2",
          "status": "notStarted",
          "uri": "vstfs:///Build/Build/43",
          "sourceBranch": "refs/pull/42/merge",
          "sourceVersion": "abc123",
          "definition": { "id": 7, "name": "CI" },
          "repository": { "id": "dotnet/roslyn", "type": "GitHub" }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrRetry_LooksUpLatestQueuedBuildAndRetriesIt(bool failedOnly)
    {
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler(async (request, ct) =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            requests.Add($"{request.Method} {request.RequestUri}\n{body}");
            if (requests.Count == 1)
            {
                // The newest build need not be completed or failed.
                return JsonResponse($$"""{"value":[{{RetriedBuild}}]}""");
            }
            return JsonResponse(RetriedBuild);
        }), "test-org", "test-project");
        var settings = new AzdoRetryCommand.Settings
        {
            PrNumber = 42,
            Repository = "dotnet/roslyn",
            Organization = "test-org",
            Project = "test-project",
            FailedOnly = failedOnly,
        };

        var build = await AzdoRetryCommand.RetryAsync(client, settings);

        var expected = failedOnly
            ? """
                GET https://dev.azure.com/test-org/test-project/_apis/build/builds?api-version=7.1&$top=1&branchName=refs%2Fpull%2F42%2Fmerge&repositoryId=dotnet%2Froslyn&repositoryType=GitHub&queryOrder=queueTimeDescending

                PATCH https://dev.azure.com/test-org/test-project/_apis/build/builds/43?retry=true&api-version=7.1
                {"status":"inProgress"}
                """
            : """
                GET https://dev.azure.com/test-org/test-project/_apis/build/builds?api-version=7.1&$top=1&branchName=refs%2Fpull%2F42%2Fmerge&repositoryId=dotnet%2Froslyn&repositoryType=GitHub&queryOrder=queueTimeDescending

                GET https://dev.azure.com/test-org/test-project/_apis/build/builds/43?api-version=7.1

                POST https://dev.azure.com/test-org/test-project/_apis/build/builds?sourceBuildId=43&api-version=7.1

                """;
        Assert.Equal(expected, string.Join("\n", requests), ignoreLineEndingDifferences: true);
        Assert.Equal("""
            {
              "id": 43,
              "buildNumber": "20261008.2",
              "status": "notStarted",
              "result": null,
              "uri": "https://dev.azure.com/test-org/test-project/_build/results?buildId=43",
              "sourceBranch": "refs/pull/42/merge",
              "definitionName": "CI",
              "definitionId": 7,
              "sourceVersion": "abc123",
              "repositoryName": "dotnet/roslyn",
              "repositoryType": "GitHub",
              "prNumber": 42,
              "finishTime": null
            }
            """, JsonSerializer.Serialize(build, JsonOptions.Indented), ignoreLineEndingDifferences: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrRetry_NoMatchesDoesNotRetry(bool failedOnly)
    {
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requests.Add($"{request.Method} {request.RequestUri}");
            return Task.FromResult(JsonResponse("""{"value":[]}"""));
        }), "test-org", "test-project");
        var settings = new AzdoRetryCommand.Settings
        {
            PrNumber = 42,
            Repository = "dotnet/roslyn",
            Organization = "test-org",
            Project = "test-project",
            FailedOnly = failedOnly,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => AzdoRetryCommand.RetryAsync(client, settings));

        Assert.Equal("No builds found for pull request dotnet/roslyn#42 in test-org/test-project.", exception.Message);
        Assert.Equal("""
            GET https://dev.azure.com/test-org/test-project/_apis/build/builds?api-version=7.1&$top=1&branchName=refs%2Fpull%2F42%2Fmerge&repositoryId=dotnet%2Froslyn&repositoryType=GitHub&queryOrder=queueTimeDescending
            """, string.Join("\n", requests));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.NotFound)]
    public async Task PrRetry_LookupFailureDoesNotRetry(bool failedOnly, HttpStatusCode status)
    {
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requests.Add(request.Method.Method);
            return Task.FromResult(new HttpResponseMessage(status));
        }));
        var settings = new AzdoRetryCommand.Settings { PrNumber = 42, Repository = "dotnet/roslyn", FailedOnly = failedOnly };

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => AzdoRetryCommand.RetryAsync(client, settings));

        Assert.Equal(status, exception.StatusCode);
        Assert.Equal("GET", string.Join("\n", requests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrRetry_CancellationDuringLookupDoesNotRetry(bool failedOnly)
    {
        using var cancellation = new CancellationTokenSource();
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requests.Add(request.Method.Method);
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(JsonResponse("""{"value":[]}"""));
        }));
        var settings = new AzdoRetryCommand.Settings { PrNumber = 42, Repository = "dotnet/roslyn", FailedOnly = failedOnly };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AzdoRetryCommand.RetryAsync(client, settings, cancellation.Token));

        Assert.Equal("GET", string.Join("\n", requests));
    }

    [Theory]
    [InlineData(null, null, null, "Specify a build ID or --pr with --repo.")]
    [InlineData(42, 42, "dotnet/roslyn", "The build ID and --pr are mutually exclusive.")]
    [InlineData(0, null, null, "The build ID must be positive.")]
    [InlineData(-1, null, null, "The build ID must be positive.")]
    [InlineData(null, 0, "dotnet/roslyn", "The pull request number must be positive.")]
    [InlineData(null, -1, "dotnet/roslyn", "The pull request number must be positive.")]
    [InlineData(null, 42, null, "--repo is required with --pr.")]
    [InlineData(null, 42, "", "--repo is required with --pr.")]
    [InlineData(null, 42, " ", "--repo is required with --pr.")]
    [InlineData(42, null, "dotnet/roslyn", "--repo requires --pr.")]
    public async Task Retry_InvalidTargetDoesNotSendRequests(int? buildId, int? prNumber, string? repository, string message)
    {
        var count = 0;
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            count++;
            return Task.FromResult(JsonResponse(RetriedBuild));
        }));
        var settings = new AzdoRetryCommand.Settings { BuildId = buildId, PrNumber = prNumber, Repository = repository };

        var validation = settings.Validate();
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => AzdoRetryCommand.RetryAsync(client, settings));

        Assert.False(validation.Successful);
        Assert.Equal(message, validation.Message);
        Assert.Equal($"{message} (Parameter 'settings')", exception.Message);
        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryCommand_ParsesEitherTargetWithoutNetworkRequests(bool usePr)
    {
        var app = new CommandApp<ParseRetryCommand>();
        var target = usePr ? new[] { "--pr", "42", "--repo", "dotnet/roslyn" } : new[] { "42" };

        var result = await app.RunAsync([.. target, "--org", "test-org", "--project", "test-project", "--failed-only"]);

        Assert.Equal(0, result);
    }

    public sealed class ParseRetryCommand : AsyncCommand<AzdoRetryCommand.Settings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, AzdoRetryCommand.Settings settings, CancellationToken ct)
        {
            Assert.Equal(settings.BuildId is null ? """
                {"buildId":null,"prNumber":42,"repository":"dotnet/roslyn","organization":"test-org","project":"test-project","failedOnly":true}
                """ : """
                {"buildId":42,"prNumber":null,"repository":null,"organization":"test-org","project":"test-project","failedOnly":true}
                """, JsonSerializer.Serialize(new
                {
                    buildId = settings.BuildId,
                    prNumber = settings.PrNumber,
                    repository = settings.Repository,
                    organization = settings.Organization,
                    project = settings.Project,
                    failedOnly = settings.FailedOnly,
                }));
            return Task.FromResult(0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildIdRetry_DoesNotLookUpPrBuilds(bool failedOnly)
    {
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requests.Add($"{request.Method} {request.RequestUri}");
            return Task.FromResult(JsonResponse(request.Method == HttpMethod.Get ? OriginalBuild : RetriedBuild));
        }), "test-org", "test-project");

        await AzdoRetryCommand.RetryAsync(client, new AzdoRetryCommand.Settings { BuildId = 42, FailedOnly = failedOnly });

        Assert.Equal(failedOnly ? """
            PATCH https://dev.azure.com/test-org/test-project/_apis/build/builds/42?retry=true&api-version=7.1
            """ : """
            GET https://dev.azure.com/test-org/test-project/_apis/build/builds/42?api-version=7.1
            POST https://dev.azure.com/test-org/test-project/_apis/build/builds?sourceBuildId=42&api-version=7.1
            """, string.Join("\n", requests), ignoreLineEndingDifferences: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrRetry_RetryFailureDoesNotFallBack(bool failedOnly)
    {
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requests.Add(request.Method.Method);
            return Task.FromResult(requests.Count == 1
                ? JsonResponse($$"""{"value":[{{OriginalBuild}}]}""")
                : request.Method == HttpMethod.Get
                    ? JsonResponse(OriginalBuild)
                    : new HttpResponseMessage(HttpStatusCode.Conflict));
        }));
        var settings = new AzdoRetryCommand.Settings { PrNumber = 42, Repository = "dotnet/roslyn", FailedOnly = failedOnly };

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => AzdoRetryCommand.RetryAsync(client, settings));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal(failedOnly ? """
            GET
            PATCH
            """ : """
            GET
            GET
            POST
            """, string.Join("\n", requests), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public async Task FullRetry_UsesSourceBuildIdWithEmptyBodyAndReturnsNewBuild()
    {
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler(async (request, ct) =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            requests.Add($"{request.Method} {request.RequestUri}\n{body}");
            return JsonResponse(request.Method == HttpMethod.Get ? OriginalBuild : RetriedBuild);
        }), "test-org", "test-project");

        var build = await client.RetryBuildAsync(42);

        Assert.Equal("""
            GET https://dev.azure.com/test-org/test-project/_apis/build/builds/42?api-version=7.1

            POST https://dev.azure.com/test-org/test-project/_apis/build/builds?sourceBuildId=42&api-version=7.1

            """, string.Join("\n", requests), ignoreLineEndingDifferences: true);
        Assert.Equal("""
            {
              "id": 43,
              "buildNumber": "20261008.2",
              "status": "notStarted",
              "result": null,
              "uri": "https://dev.azure.com/test-org/test-project/_build/results?buildId=43",
              "sourceBranch": "refs/pull/42/merge",
              "definitionName": "CI",
              "definitionId": 7,
              "sourceVersion": "abc123",
              "repositoryName": "dotnet/roslyn",
              "repositoryType": "GitHub",
              "prNumber": 42,
              "finishTime": null
            }
            """, JsonSerializer.Serialize(build, JsonOptions.Indented), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public async Task FullRetry_HandlesBuildWithoutParameters()
    {
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler(async (request, ct) =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            requests.Add($"{request.Method} {request.RequestUri}\n{body}");
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(RetriedBuild);
            }

            return JsonResponse(RetriedBuild);
        }));

        await client.RetryBuildAsync(43);

        Assert.Equal("""
            GET https://dev.azure.com/dnceng-public/public/_apis/build/builds/43?api-version=7.1

            POST https://dev.azure.com/dnceng-public/public/_apis/build/builds?sourceBuildId=43&api-version=7.1

            """, string.Join("\n", requests), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public async Task FailedOnlyRetry_UpdatesExistingBuildWithoutQueuingAnother()
    {
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler(async (request, ct) =>
        {
            requests.Add($"{request.Method} {request.RequestUri}\n{await request.Content!.ReadAsStringAsync(ct)}");
            return JsonResponse("""
                {
                  "id": 42,
                  "buildNumber": "20261008.1",
                  "status": "inProgress",
                  "uri": "vstfs:///Build/Build/42",
                  "sourceBranch": "refs/heads/main",
                  "sourceVersion": "abc123",
                  "definition": { "id": 7, "name": "CI" },
                  "repository": { "id": "repo-id", "type": "TfsGit" }
                }
                """);
        }), "test-org", "test-project");

        var build = await client.RetryBuildAsync(42, failedOnly: true);

        Assert.Equal("""
            PATCH https://dev.azure.com/test-org/test-project/_apis/build/builds/42?retry=true&api-version=7.1
            {"status":"inProgress"}
            """, string.Join("\n", requests), ignoreLineEndingDifferences: true);
        Assert.Equal("""
            {
              "id": 42,
              "buildNumber": "20261008.1",
              "status": "inProgress",
              "result": null,
              "uri": "https://dev.azure.com/test-org/test-project/_build/results?buildId=42",
              "sourceBranch": "refs/heads/main",
              "definitionName": "CI",
              "definitionId": 7,
              "sourceVersion": "abc123",
              "repositoryName": "repo-id",
              "repositoryType": "TfsGit",
              "prNumber": null,
              "finishTime": null
            }
            """, JsonSerializer.Serialize(build, JsonOptions.Indented), ignoreLineEndingDifferences: true);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.BadRequest)]
    [InlineData(true, HttpStatusCode.Conflict)]
    public async Task Retry_PropagatesFailureWithoutFallback(bool failedOnly, HttpStatusCode status)
    {
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requests.Add(request.Method.Method);
            return Task.FromResult(request.Method == HttpMethod.Get
                ? JsonResponse(OriginalBuild)
                : new HttpResponseMessage(status));
        }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.RetryBuildAsync(42, failedOnly));

        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(failedOnly ? "PATCH" : "GET\nPOST", string.Join("\n", requests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_FailureIncludesServerDetails(bool failedOnly)
    {
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
            Task.FromResult(request.Method == HttpMethod.Get
                ? JsonResponse(OriginalBuild)
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"message":"The request body must be empty if queueing a build with sourceBuildId."}"""),
                })));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.RetryBuildAsync(42, failedOnly));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("""
            Build retry failed with HTTP 400 (Bad Request): {"message":"The request body must be empty if queueing a build with sourceBuildId."}
            """, exception.Message);
    }

    [Fact]
    public async Task FullRetry_DoesNotQueueWhenOriginalBuildCannotBeFetched()
    {
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requests.Add(request.Method.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.RetryBuildAsync(42));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal("GET", string.Join("\n", requests));
    }

    [Theory]
    [InlineData("definition", "null")]
    [InlineData("sourceBranch", "\"\"")]
    [InlineData("sourceVersion", "null")]
    public async Task FullRetry_RejectsMissingSourceContext(string property, string value)
    {
        var original = System.Text.Json.Nodes.JsonNode.Parse(OriginalBuild)!;
        original[property] = System.Text.Json.Nodes.JsonNode.Parse(value);
        var requests = new List<string>();
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requests.Add(request.Method.Method);
            return Task.FromResult(JsonResponse(original.ToJsonString()));
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RetryBuildAsync(42));

        Assert.Equal("The original build must have a pipeline definition, source branch, and source version to retry.", exception.Message);
        Assert.Equal("GET", string.Join("\n", requests));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    public async Task Retry_RejectsInvalidBuildIdBeforeSending(int buildId, bool failedOnly)
    {
        var count = 0;
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            count++;
            return Task.FromResult(JsonResponse(RetriedBuild));
        }));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.RetryBuildAsync(buildId, failedOnly));

        Assert.Equal(0, count);
        Assert.Equal("The build ID must be positive.", new AzdoRetryCommand.Settings { BuildId = buildId }.Validate().Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_PropagatesCancellation(bool failedOnly)
    {
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            count++;
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(JsonResponse(RetriedBuild));
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RetryBuildAsync(42, failedOnly, cancellation.Token));

        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("POST", """{"definition":{"id":7},"sourceVersion":"abc123"}""")]
    [InlineData("PATCH", """{"status":"inProgress"}""")]
    [InlineData("GET", null)]
    public async Task RateLimitRetry_PreservesMethodHeadersAndBody(string method, string? body)
    {
        var requests = new List<string>();
        var transport = new DelegateHandler(async (request, ct) =>
        {
            var content = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            requests.Add($"{request.Method} {request.RequestUri}\n{request.Headers.Authorization}\n{request.Headers.GetValues("X-Test").Single()}\n{request.Content?.Headers.ContentType}\n{content}");
            return new HttpResponseMessage(requests.Count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
        });
        using var client = new HttpClient(new AzdoClient.BearerTokenHandler(new TestCredential(), new AzdoRateLimitState(), transport));
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://dev.azure.com/org/project/_apis/build/builds");
        request.Headers.Add("X-Test", "value");
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);

        var expected = method == "GET"
            ? """
                GET https://dev.azure.com/org/project/_apis/build/builds
                Bearer test-token
                value


                """
            : $"{method} https://dev.azure.com/org/project/_apis/build/builds\nBearer test-token\nvalue\napplication/json; charset=utf-8\n{body}";
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, requests.Count);
        Assert.Equal(expected, requests[0], ignoreLineEndingDifferences: true);
        Assert.Equal(expected, requests[1], ignoreLineEndingDifferences: true);
    }

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            handler(request, ct);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };
}
