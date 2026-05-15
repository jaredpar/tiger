# .NET Pipeline Triage

This is a copilot plugin that helps with pipeline triage in the github.com/dotnet organization. To use this tool you need to do the following:

1. Be connected to the VPN.
2. This tool uses Azure Identity for authenication so if you're having auth issues use the `az login` command to login to your azure account. You can also use `az account set -s <subscription name>` to set the subscription you want to use.

This tool is still in early development and may have some rough edges.

Run `./scripts/dogfood.sh -i` to build and install the plugin locally, or `./scripts/dogfood.sh -u` to uninstall local builds and reinstall from the marketplace.

Example prompts to try:

> Look at the builds of dotnet/roslyn for the last 48 hours. Tell me about any patterns in the test or helix failures.
> Look at all the builds for PR #12345 in dotnet/roslyn. Tell me about any patterns in the test or helix failures.

## References:


Helix [documentation](https://github.com/dotnet/arcade/blob/0831f9c135e88dd09993903ed5ac1a950285ac96/Documentation/AzureDevOps/SendingJobsToHelix.md)