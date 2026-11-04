# Zapqio Runner (.NET)

The runner executes .NET module methods on a Windows computer or server.
It runs as a Windows service, connects to a Zapq instance and returns task
results and logs. Install it where its modules can reach the systems they use.

## Documentation

Installation, operation, troubleshooting and module development are maintained
in the central Zapq documentation:

| Topic | English | Polski | Deutsch |
| --- | --- | --- | --- |
| Install and operate a runner | [Guide](https://docs.zapq.io/guides/runners) | [Poradnik](https://docs.zapq.io/pl/guides/runners) | [Anleitung](https://docs.zapq.io/de/guides/runners) |
| Deploy and update modules | [Guide](https://docs.zapq.io/guides/external-runner-modules) | [Poradnik](https://docs.zapq.io/pl/guides/external-runner-modules) | [Anleitung](https://docs.zapq.io/de/guides/external-runner-modules) |
| Create .NET modules and use the NuGet contract | [Technical guide](https://docs.zapq.io/technical/dotnet-modules) | [Dokumentacja techniczna](https://docs.zapq.io/pl/technical/dotnet-modules) | [Technische Anleitung](https://docs.zapq.io/de/technical/dotnet-modules) |

## Downloads and requirements

- [Releases and downloads](https://github.com/zapqio/dotnet-runner/releases)
- [Windows installer](https://github.com/zapqio/dotnet-runner/blob/main/install.ps1)
- [.NET module template](https://github.com/zapqio/dotnet_runner_template)

The Windows installation needs administrator access, a compatible x64 .NET
Desktop Runtime and the address and runner token of your Zapq instance.
Building modules additionally needs a compatible .NET SDK. Follow the guide
for the runtime version, installation commands and connection checks.

## Module contract

`Zapqio.Runner.Module.Core` provides `IRunnerMethod`, `IRunnerInjection`,
`JobContext` and `RunnerLog` in the `Zapqio.Runner.Core` namespace. The runner
supplies the contract at runtime. Reference it with `ExcludeAssets="runtime"`
in deployed modules; a standalone test host must supply its own runtime reference.
The technical guide above includes a complete project and packaging example.

## Working on the runner

Clone with `git clone --recurse-submodules https://github.com/zapqio/dotnet-runner.git`.
Use the .NET SDKs and Windows targeting requirements from the project files.
Run `dotnet test -c Release` from the repository root. The release workflow in
`.github/workflows/release.yml` contains the supported publish commands.
The shared protocol is maintained in [its own repository](https://github.com/zapqio/protocol).

The module contract package is licensed under Apache-2.0; its license text is
in [LICENSE](https://github.com/zapqio/dotnet-runner/blob/main/Zapqio.Runner.Module.Core/LICENSE)
and is included in the NuGet package.
