namespace DataSync.Cli;

public static class Help
{
    public static void Print()
    {
        Console.WriteLine($"""
            datasync — cross-database replication.

              datasync serve [--repo <path>] [--state-db <path>] [--url <url>]
                  Starts the API, the scheduler and the web console in one process.
                  Defaults: --repo {CliOptions.DefaultRoot}, --url http://localhost:5080

              datasync service install|uninstall|status [--repo <path>] [--url <url>] [--account <account>]
                  Registers this tool as a Windows service. Windows only, and install needs an
                  elevated prompt.

              datasync invite [--role Admin|Viewer] [--repo <path>] [--url <url>]
                  Prints a fresh single-use invitation URL. For when the first-run one has scrolled
                  away, or the process is a service with nowhere to print it.

              datasync health [--url <url>]
                  Exits 0 if a running DataSync answers, 1 if it does not. What the container's
                  health check runs.

              datasync secret set <ref> <value>|list [<ref> ...]|remove <ref>
                  Stores, checks, or removes a secret in the OS credential store — e.g. the state
                  database's password: datasync secret set datasync:config:stateConnectionString "Password=..."

              datasync version
            """);
    }
}
