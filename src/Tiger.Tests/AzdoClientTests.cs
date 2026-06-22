using System.Net;
using Xunit;

namespace Tiger.Tests;

public class AzdoClientTests
{
    [Fact]
    public async Task GetBuildsForRepositoryAsync_DefaultsToGitHubRepositoryType()
    {
        Uri? requestUri = null;
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requestUri = request.RequestUri;
            return Task.FromResult(CreateJsonResponse("""{"count":0,"value":[]}"""));
        }));

        await client.GetBuildsForRepositoryAsync("dotnet/roslyn");

        Assert.NotNull(requestUri);
        Assert.Contains("repositoryId=dotnet%2Froslyn", requestUri.ToString());
        Assert.Contains("repositoryType=GitHub", requestUri.ToString());
    }

    [Fact]
    public async Task GetBuildsForRepositoryAsync_UsesConfiguredRepositoryType()
    {
        var requestUris = new List<Uri>();
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requestUris.Add(request.RequestUri!);
            if (request.RequestUri!.ToString().Contains("_apis/git/repositories"))
            {
                return Task.FromResult(CreateJsonResponse("""
                    {
                      "count": 1,
                      "value": [
                        {
                          "id": "11111111-1111-1111-1111-111111111111",
                          "name": "Repo"
                        }
                      ]
                    }
                    """));
            }

            return Task.FromResult(CreateJsonResponse("""
                {
                  "count": 1,
                  "value": [
                    {
                      "id": 42,
                      "buildNumber": "20250622.1",
                      "status": "completed",
                      "result": "succeeded",
                      "uri": "vstfs:///Build/Build/42",
                      "sourceBranch": "refs/heads/main",
                      "sourceVersion": "abc123",
                      "definition": { "id": 7, "name": "CI" },
                      "repository": { "id": "Repo", "name": "Repo", "type": "TfsGit" },
                      "finishTime": "2026-06-22T20:00:00Z"
                    }
                  ]
                }
                """));
        }));

        var builds = await client.GetBuildsForRepositoryAsync("Repo", repositoryType: AzdoRepositoryTypes.TfsGit);

        Assert.Equal(2, requestUris.Count);
        Assert.Contains("_apis/git/repositories", requestUris[0].ToString());
        Assert.Contains("repositoryId=11111111-1111-1111-1111-111111111111", requestUris[1].ToString());
        Assert.Contains("repositoryType=TfsGit", requestUris[1].ToString());
        var build = Assert.Single(builds);
        Assert.Equal(AzdoRepositoryTypes.TfsGit, build.RepositoryType);
    }

    [Fact]
    public async Task GetCompletedBuildsSinceAsync_UsesConfiguredRepositoryType()
    {
        var requestUris = new List<Uri>();
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requestUris.Add(request.RequestUri!);
            if (request.RequestUri!.ToString().Contains("_apis/git/repositories"))
            {
                return Task.FromResult(CreateJsonResponse("""
                    {
                      "count": 1,
                      "value": [
                        {
                          "id": "11111111-1111-1111-1111-111111111111",
                          "name": "Repo"
                        }
                      ]
                    }
                    """));
            }

            return Task.FromResult(CreateJsonResponse("""{"count":0,"value":[]}"""));
        }));

        await client.GetCompletedBuildsSinceAsync(
            new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc),
            repositoryId: "Repo",
            repositoryType: AzdoRepositoryTypes.TfsGit);

        Assert.Equal(2, requestUris.Count);
        Assert.Contains("_apis/git/repositories", requestUris[0].ToString());
        Assert.Contains("repositoryId=11111111-1111-1111-1111-111111111111", requestUris[1].ToString());
        Assert.Contains("repositoryType=TfsGit", requestUris[1].ToString());
    }

    [Fact]
    public async Task GetBuildsForRepositoryAsync_TfsGitRepositoryId_DoesNotResolveName()
    {
        Uri? requestUri = null;
        var repositoryId = "11111111-1111-1111-1111-111111111111";
        var client = AzdoClient.Create(new DelegateHandler((request, ct) =>
        {
            requestUri = request.RequestUri;
            return Task.FromResult(CreateJsonResponse("""{"count":0,"value":[]}"""));
        }));

        await client.GetBuildsForRepositoryAsync(repositoryId, repositoryType: AzdoRepositoryTypes.TfsGit);

        Assert.NotNull(requestUri);
        Assert.DoesNotContain("_apis/git/repositories", requestUri.ToString());
        Assert.Contains($"repositoryId={repositoryId}", requestUri.ToString());
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => handler(request, ct);
    }

    private static HttpResponseMessage CreateJsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
