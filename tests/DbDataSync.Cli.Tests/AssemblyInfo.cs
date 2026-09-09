using Xunit;

// One test at a time in this assembly.
//
// Several test classes here redirect Console.Out/Console.Error to capture a CLI command's output
// (SecretCommandTests, ReadinessChecksTests, ConfigCommandTests, InviteCommandTests) — both are
// process-wide statics, so two test classes doing this at once corrupt each other's captured output
// (one test sees another's text, or an empty string, or a JSON parse failure on interleaved bytes).
// A shared secrets file (see CliTestSecretsFile) has the same problem from the write side: concurrent
// writers racing FileSecretProvider's read-modify-write cycle can lose an update or trip a transient
// FileNotFoundException on the temp-file swap. xUnit runs test classes in parallel by default, so
// phase 115's ConfigCommandTests — a third Console-redirecting class added alongside the existing
// two — was what first made these collide often enough to fail a run.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
