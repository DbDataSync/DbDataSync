using DataSync.Cli;

if (args.Length == 0 || CliOptions.Has(args, "--help") || CliOptions.Has(args, "-h"))
{
    Help.Print();
    return args.Length == 0 ? 1 : 0;
}

var command = args[0].ToLowerInvariant();
var rest = args.Skip(1).ToArray();

return command switch
{
    "serve" => await ServeCommand.RunAsync(rest),
    "service" => ServiceCommand.Run(rest),
    "cert" => CertCommand.Run(rest),
    "health" => await HealthCommand.RunAsync(rest),
    "invite" => InviteCommand.Run(rest),
    "secret" => SecretCommand.Run(rest),
    "version" => Version(),
    _ => Unknown(command),
};

static int Version()
{
    Console.WriteLine(typeof(Help).Assembly.GetName().Version?.ToString() ?? "unknown");
    return 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    Help.Print();
    return 1;
}
