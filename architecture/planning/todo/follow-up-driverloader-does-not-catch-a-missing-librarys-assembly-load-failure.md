# Follow-up: `DriverLoader.LoadDescriptorDrivers` crashes host startup for a JDBC driver whose library isn't installed

Found while writing phase 173V's `FilesApiFactory` test fixture, not while building a phase for this
specifically. Documented to fix later.

## What's true today

`DriverLoader.LoadDescriptorDrivers`'s own doc comment states the contract plainly: *"A failed descriptor
is logged... and skipped rather than failing host startup — one operator's typo in a driver they added
should not take down every other replication."* The catch clause backing that promise:

```csharp
catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException
    or YamlDotNet.Core.YamlException)
{
    onError($"Failed to load driver descriptor '{yamlPath}'", ex);
}
```

A `driver.yaml` with `base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, ...` whose `ikvm` library isn't
actually installed throws `System.IO.FileNotFoundException` — from `Assembly.Load`-style resolution deep
inside `JdbcGenericDriver`'s constructor (`JdbcProviderFactory.FromJarPaths` touching `IKVM.Java` types),
not from anything YAML- or config-shaped. `FileNotFoundException` is not in that `when` clause, so it
propagates out of `LoadDescriptorDrivers` uncaught — the exact "one operator's typo... should not take
down every other replication" promise the class's own doc comment makes, broken for this one failure mode.

Confirmed live, not inferred: writing a JDBC-backed `driver.yaml` with `base:` set into a fresh
`AuthenticatedApiFactory`-backed test repo root (no `ikvm` library installed) crashed the entire test host
at startup — every test in the fixture failed with the same `FileNotFoundException`, not just ones
touching the misconfigured driver.

## Why this matters more than an ordinary bad-descriptor typo

The ADO.NET side of this same problem is already handled correctly: `GenericDriver.FromDescriptor`
resolves its library through `libraries.GetFactory(descriptor.Library)`, which throws
`InvalidOperationException` for a library that doesn't resolve — already in the caught set. JDBC's own
path bypasses that entirely (`JdbcGenericDriver.FromDescriptor` doesn't go through `LibraryRegistry` at
all for its own `ikvm` dependency — see that class's own `RequiredLibraryId` doc comment), so it fails in
a different, uncaught way.

This is exactly the scenario `DriverLibraryCompatibility`/phase 109j exists to warn about gracefully
(`ConnectionsController.Test`, `config check`) — but that's a **runtime** connection check, not something
that runs before `LoadDescriptorDrivers` even tries to construct the driver at host startup.

## Suggested fix, for whenever this gets picked up

Broaden the `when` clause:

```csharp
catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException
    or YamlDotNet.Core.YamlException or FileNotFoundException or FileLoadException or BadImageFormatException)
```

`FileLoadException`/`BadImageFormatException` added alongside `FileNotFoundException` since
`LoadCompiledDrivers`' own catch clause already treats all three as the same class of "an assembly
couldn't be loaded" problem (see that method's own `when` clause, a few lines below in the same file) —
worth mirroring rather than adding only the one exception type this specific repro hit.

Confirm this doesn't also swallow a *different*, genuinely fatal issue the missing-`ikvm` case happens to
share a runtime exception type with, before widening the catch — a quick check, not a design question.
