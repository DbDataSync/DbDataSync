namespace DbDataSync.Cli;

public static class Help
{
    public static void Print()
    {
        Console.WriteLine($"""
            dbdatasync — cross-database replication.

              dbdatasync setup [--repo <path>]
                  Interactive walk-through for a fresh install, or a review screen for an existing
                  one. Refuses to run when stdin/stdout aren't a real console.

              dbdatasync serve [--repo <path>] [--state-db <path>] [--url <url>]
                  Starts the API, the scheduler and the web console in one process.
                  Defaults: --repo {CliOptions.DefaultRoot}, --url http://localhost:5080

              dbdatasync service install|uninstall|status [--repo <path>] [--url <url>] [--account <account>]
                  On Windows: registers this tool as a Windows service. Needs an elevated prompt.
              dbdatasync service install|uninstall|status [--repo <path>] [--url <url>] [--user <user>]
                  On Linux: registers a systemd unit (default user: dbdatasync). Needs root
                  (`sudo`); enables but does not start it — run `systemctl start dbdatasync` next.

              dbdatasync invite [--role Admin|Viewer] [--repo <path>] [--url <url>]
                  Prints a fresh single-use invitation URL. For when the first-run one has scrolled
                  away, or the process is a service with nowhere to print it.

              dbdatasync health [--url <url>]
                  Exits 0 if a running DbDataSync answers, 1 if it does not. What the container's
                  health check runs.

              dbdatasync config check|cert|secret|library|driver
                  Every configuration-related command, grouped under one root:

                    check     [--repo <path>] [--json]
                        Non-interactive readiness check — repo, state store, libraries/drivers,
                        auth, binding, first admin. Exits 1 if anything fails; what `setup`'s
                        review screen runs.

                    cert      use-pem --cert <path> --key <path> | use-pfx --pfx <path>
                        Points Kestrel at a certificate file — unencrypted PEM key or
                        unprotected PFX. Any platform.
                    cert      status|list|new-self-signed|enroll|renew|retrieve|templates|bind
                        Issues, installs, binds and renews a certificate from the Windows
                        certificate store or an AD CS CA. `status` also works on any platform
                        (reports whichever of the two above is configured); the rest are
                        Windows only. Run `dbdatasync config cert` with no subcommand to see
                        every subcommand's flags.

                    secret    set <ref> <value>|list [<ref> ...]|remove <ref>
                        Stores, checks, or removes a secret in the OS credential store — e.g. the
                        state database's password:
                        dbdatasync config secret set dbdatasync:config:stateConnectionString "Password=..."

                    library   install <packageId>[ <packageId>...] [--as <id>] --version <v> [--factory-type type] [--source feed]
                    library   sync [<id>] | list | uninstall <id>
                        Restores an ADO.NET library package (Microsoft.Data.SqlClient, Npgsql,
                        MySqlConnector, ...) into <repo>/libraries/<id>/, so it can be swapped by
                        installing a new version rather than by rebuilding DbDataSync. Runs
                        `dotnet publish` under the hood — no network access from the running host
                        itself.

                    driver    install <id> --library <name> --version <v> [--factory-type type] [--from mysql] [--display-name name]
                    driver    list | uninstall <id>
                        Adds a whole new SQL engine — a driver.yaml descriptor plus its restored
                        library — without a DbDataSync rebuild. Watermark and batch-reload
                        replication only; see architecture/planning/todo/nuget-loaded-drivers.md
                        for what a descriptor can and can't do.

              dbdatasync version
            """);
    }
}
