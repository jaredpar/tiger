// See https://aka.ms/new-console-template for more information


using Pipeline.Core;

var credentials = PipelineUtils.CreateCredential();
var client = AzdoClient.Create(credentials);

var builds = await client.GetBuildsForRepositoryAsync("dotnet/roslyn", top: 30);

foreach (var build in builds)
{
    if (build.Result == "succeeded")
    {
        continue;
    }

    var tests = await client.GetTestFailuresAsync(build.Id);
    foreach (var test in tests)
    {
        if (test.Comment is not null && test.Comment.Contains("HelixJobId"))
        {
            var attachments = await client.GetTestResultAttachmentsAsync(test.TestRunId, test.Id);
            foreach (var attachment in attachments)
            {
                Console.WriteLine($"""
                    Name: {attachment.FileName}
                    Content: {attachment.Url}
                """);
            }
        }
    }
}

