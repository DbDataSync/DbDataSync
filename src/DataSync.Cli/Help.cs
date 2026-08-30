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

              datasync health [--url <url>]
                  Exits 0 if a running DataSync answers, 1 if it does not. What the container's
                  health check runs.

              datasync version
            """);
    }
}
