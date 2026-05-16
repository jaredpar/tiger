using System.ComponentModel;
using Spectre.Console.Cli;

namespace Tiger.Commands;

/// <summary>
/// Shared settings for all AzDO commands.
/// </summary>
public class AzdoSettings : CommandSettings
{
    [CommandOption("--org")]
    [Description("AzDO organization (default: dnceng-public)")]
    public string Organization { get; set; } = AzdoClient.DefaultOrganization;

    [CommandOption("--project")]
    [Description("AzDO project (default: public)")]
    public string Project { get; set; } = AzdoClient.DefaultProject;

    public AzdoClient CreateClient()
    {
        var credential = TigerUtils.CreateCredential();
        return AzdoClient.Create(credential, Organization, Project);
    }
}

/// <summary>
/// Settings for commands that require a build ID.
/// </summary>
public class AzdoBuildSettings : AzdoSettings
{
    [CommandArgument(0, "<build-id>")]
    [Description("The AzDO build ID")]
    public int BuildId { get; set; }
}
