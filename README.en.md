# Zapqio Runner (.NET)

[Polski](./README.md) | **English**

The runner executes .NET module methods on a Windows computer or server.
It connects to a Zapq instance over WebSocket, receives tasks, and sends back results and logs.
It runs as a Windows service.

## Installation

The latest instructions, including screenshots of the web panel, are available in the Zapq documentation:

- [External runner installation in English](https://docs.zapq.io/guides/runners)
- [Instalacja runnera zewnętrznego po polsku](https://docs.zapq.io/pl/guides/runners)
- [Installation des externen Runners auf Deutsch](https://docs.zapq.io/de/guides/runners)

The guide covers .NET requirements, adding a runner in the web panel, the token,
the instance URL, the Windows installer, and checking the connection. It also explains
how to configure concurrent tasks, update the runner, and uninstall it.

The installer and program files remain available in this repository:

- [Windows installer: install.ps1](./install.ps1)
- [Releases and downloads](https://github.com/zapqio/dotnet-runner/releases)

## Modules

Connecting the runner does not make any methods available until a module is deployed.
The [external runner modules guide (in Polish)](https://docs.zapq.io/pl/guides/external-runner-modules)
covers deployment from source code and CI, local approval, restarting the runner,
and checking the result.

## Technical documentation

The technical guides below are in Polish:

- [Configuration, Windows service, logs, and diagnostics](./docs/szczegoly.md)
- [Module approval and installation process](./docs/wdrozenia.md)
- [Writing .NET modules](./docs/moduly.md)

A [.NET module template](https://github.com/zapqio/dotnet_runner_template) is also available.
