# Zapqio.Runner.Module.Core

Contracts for .NET modules hosted by [Zapqio Runner](https://github.com/zapqio/dotnet-runner).
The package targets .NET 8 and .NET 10.

## Reference from a module

```xml
<PackageReference Include="Zapqio.Runner.Module.Core" Version="1.2.0" ExcludeAssets="runtime" />
```

The runner supplies this assembly at runtime. `ExcludeAssets="runtime"` keeps a duplicate
copy out of the deployed module. A standalone test host must provide its own runtime
reference to this package.

Implement `IRunnerMethod` from `Zapqio.Runner.Core`. The `Run(string data)` method returns
`Task<string>`; serialize JSON input and output explicitly. `InData()` and `OutData()`
describe the input and output types. `IRunnerInjection` marks services shared through
the runner's dependency injection container.

Use `RunnerLog` for job logging and `JobContext.Current` for the job ID, attempt ID
and method name during execution. Treat method instances and injected services as
shared: concurrent jobs may call them at the same time.

See the [module documentation](https://github.com/zapqio/dotnet-runner/blob/main/docs/szczegoly.md)
for packaging and deployment.

## License

Apache-2.0. The license text is included in `LICENSE`.
