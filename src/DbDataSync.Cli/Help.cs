namespace DbDataSync.Cli;

public static class Help
{
    public static void Print()
    {
        Console.WriteLine($"""
            dbdatasync — cross-database replication.

              dbdatasync serve [--repo <path>] [--state-db <path>] [--url <url>]
                  Starts the API, the scheduler and the web console in one process.
                  Defaults: --repo {CliOptions.DefaultRoot}, --url http://localhost:5080

              dbdatasync service install|uninstall|status [--repo <path>] [--url <url>] [--account <account>]
                  Registers this tool as a Windows service. Windows only, and install needs an
                  elevated prompt.

              dbdatasync cert status|list|new-self-signed|enroll|renew|retrieve|templates|bind
                  Issues, installs, binds and renews the certificate Kestrel serves TLS with. Windows
                  only; run `dbdatasync cert` with no subcommand to see every subcommand's flags.

              dbdatasync invite [--role Admin|Viewer] [--repo <path>] [--url <url>]
                  Prints a fresh single-use invitation URL. For when the first-run one has scrolled
                  away, or the process is a service with nowhere to print it.

              dbdatasync health [--url <url>]
                  Exits 0 if a running DbDataSync answers, 1 if it does not. What the container's
                  health check runs.

              dbdatasync secret set <ref> <value>|list [<ref> ...]|remove <ref>
                  Stores, checks, or removes a secret in the OS credential store — e.g. the state
                  database's password: dbdatasync secret set dbdatasync:config:stateConnectionString "Password=..."

              dbdatasync version
            """);
    }
}
