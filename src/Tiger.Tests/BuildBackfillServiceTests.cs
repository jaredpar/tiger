using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Tiger.Tests;

public class BuildBackfillServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TigerDatabase _db;
    private static readonly DateTime s_since = new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public BuildBackfillServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tiger-test-{Guid.NewGuid()}.db");
        _db = TigerDatabase.Open(_dbPath);
        _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                INSERT INTO poll_watermarks (organization, project, last_build_id, last_poll_time)
                VALUES ('org', 'proj', 0, @since)
                """;
            cmd.Parameters.AddWithValue("@since", s_since.ToString("o"));
            cmd.ExecuteNonQuery();
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task Backfill_LogsRegistrationBatchesAndOverallTotal()
    {
        var source = new AzdoSource { Organization = "org", Project = "proj" };
        var config = new TigerConfig { Sources = [source] };
        var log = new ServiceLog();
        using var ingestion = new BuildIngestionService(_db);
        using var handler = new BuildsHandler(11);
        var factory = new AzdoClientFactory((org, proj) => AzdoClient.Create(handler, org, proj));
        using var backfill = new BuildBackfillService(config, _db, ingestion, factory, log);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successCount = 0;
        log.EntryAdded += () =>
        {
            var entry = log.GetRecent(1)[0];
            // Synchronize with source completion and then overall completion, not elapsed time.
            if ((entry.Level == ServiceLogLevel.Success && ++successCount == 2) ||
                entry.Level == ServiceLogLevel.Error)
            {
                finished.TrySetResult();
            }
        };

        backfill.Start();
        try
        {
            // A watchdog only: passing does not depend on any timing threshold.
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await backfill.StopAsync();
        }

        Assert.Equal($"""
            Info Backfill: Starting backfill...
            Info Backfill: org/proj — fetching builds since {s_since.ToLocalTime():yyyy-MM-dd h:mm tt}
            Info Backfill: org/proj — registering 11 new builds
            Info Backfill: org/proj — 10/11 builds registered
            Info Backfill: org/proj — 11/11 builds registered
            Success Backfill: org/proj — done (11 builds registered)
            Success Backfill: Complete — 11 builds registered
            """,
            string.Join("\n", log.GetRecent().Select(entry => $"{entry.Level} {entry.Service}: {entry.Message}")),
            ignoreLineEndingDifferences: true);
        _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT ingestion_status, COUNT(*) FROM builds GROUP BY ingestion_status
                """;
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("pending", reader.GetString(0));
            Assert.Equal(11, reader.GetInt32(1));
            Assert.False(reader.Read());
        });
    }

    [Fact]
    public async Task BackfillSourceAsync_NoNewBuildsDoesNotReportRegistration()
    {
        var source = new AzdoSource { Organization = "org", Project = "proj" };
        var config = new TigerConfig { Sources = [source] };
        var log = new ServiceLog();
        using var ingestion = new BuildIngestionService(_db);
        using var handler = new BuildsHandler(0);
        var factory = new AzdoClientFactory((org, proj) => AzdoClient.Create(handler, org, proj));
        using var backfill = new BuildBackfillService(config, _db, ingestion, factory, log);

        var count = await backfill.BackfillSourceAsync(source, forceFullWindow: false, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Equal($"""
            Info Backfill: org/proj — fetching builds since {s_since.ToLocalTime():yyyy-MM-dd h:mm tt}
            Info Backfill: org/proj — no new builds
            """,
            string.Join("\n", log.GetRecent().Select(entry => $"{entry.Level} {entry.Service}: {entry.Message}")),
            ignoreLineEndingDifferences: true);
    }

    private sealed class BuildsHandler(int buildCount) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(new
            {
                count = buildCount,
                value = Enumerable.Range(1, buildCount).Select(id => new
                {
                    id,
                    buildNumber = $"20250101.{id}",
                    status = "completed",
                    result = "succeeded",
                    uri = $"vstfs:///Build/Build/{id}",
                    sourceBranch = "refs/heads/main",
                    definition = new { id = 7, name = "CI" },
                }),
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
