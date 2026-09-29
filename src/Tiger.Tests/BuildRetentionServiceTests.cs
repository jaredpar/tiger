using Xunit;

namespace Tiger.Tests;

public class BuildRetentionServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TigerDatabase _db;

    public BuildRetentionServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tiger-retention-test-{Guid.NewGuid()}.db");
        _db = TigerDatabase.Open(_dbPath);
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
    public void CleanupExpiredBuilds_DeletesBuildsPastRetentionAcrossOrganizations()
    {
        var oldFinishTime = DateTime.UtcNow.AddDays(-15).ToString("o");
        var recentFinishTime = DateTime.UtcNow.AddDays(-10).ToString("o");
        _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                INSERT INTO builds
                    (organization, project, build_id, build_number, definition_name, definition_id, status, source_branch, finish_time)
                VALUES
                    ('org1', 'proj', 1, 'old-1', 'pipeline', 1, 'completed', 'main', @old),
                    ('org2', 'proj', 1, 'old-2', 'pipeline', 1, 'completed', 'main', @old),
                    ('org1', 'proj', 2, 'recent', 'pipeline', 1, 'completed', 'main', @recent),
                    ('org1', 'proj', 3, 'unknown-time', 'pipeline', 1, 'completed', 'main', NULL)
                """;
            cmd.Parameters.AddWithValue("@old", oldFinishTime);
            cmd.Parameters.AddWithValue("@recent", recentFinishTime);
            cmd.ExecuteNonQuery();
        });

        var service = new BuildRetentionService(new TigerConfig { BackfillDays = 14 }, _db);
        var deleted = service.CleanupExpiredBuilds();

        Assert.Equal(2, deleted);
        var remainingBuilds = _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT organization || ':' || build_id FROM builds ORDER BY organization, build_id";
            using var reader = cmd.ExecuteReader();
            var builds = new List<string>();
            while (reader.Read())
            {
                builds.Add(reader.GetString(0));
            }
            return string.Join("\n", builds);
        });
        Assert.Equal("""
            org1:2
            org1:3
            """.ReplaceLineEndings("\n"), remainingBuilds);
    }
}
