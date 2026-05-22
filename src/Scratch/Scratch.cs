using Tiger;

// Build 1430795 from dnceng-public/public
// https://dev.azure.com/dnceng-public/public/_build/results?buildId=1430795
var context = TigerUtils.CreateContext();
var client = AzdoClient.Create(context.AzureCredential, "dnceng-public", "public");

Console.WriteLine("Fetching test failures for build 1430795...");
var failures = await client.GetTestFailuresAsync(1430795);

Console.WriteLine($"Total failures: {failures.Count}");
Console.WriteLine();

foreach (var failure in failures)
{
    Console.WriteLine($"Test: {failure.TestCaseTitle}");
    Console.WriteLine($"  Outcome: {failure.Outcome}");
    Console.WriteLine($"  SubResultCount: {failure.SubResultCount}");
    Console.WriteLine($"  Error: {(failure.ErrorMessage is not null ? failure.ErrorMessage[..Math.Min(200, failure.ErrorMessage.Length)] : "(null)")}");
    Console.WriteLine($"  Stack: {(failure.StackTrace is not null ? "(present)" : "(null)")}");
    Console.WriteLine($"  Helix Job: {failure.HelixJobName ?? "(null)"}");
    Console.WriteLine($"  Helix WI: {failure.HelixWorkItemName ?? "(null)"}");
    Console.WriteLine();
}
