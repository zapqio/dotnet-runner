# Zapqio Runner (.NET)

**Polski** | [English](./README.en.md)

Runner wykonuje metody modułów .NET na komputerze lub serwerze Windows.
Łączy się z instancją Zapq przez WebSocket, odbiera zadania i odsyła wyniki oraz logi.
Działa jako usługa Windows.

## Instalacja

Aktualna instrukcja wraz ze zrzutami panelu znajduje się w dokumentacji Zapq:

- [Instalacja runnera zewnętrznego po polsku](https://docs.zapq.io/pl/guides/runners)
- [External runner installation in English](https://docs.zapq.io/guides/runners)
- [Installation des externen Runners auf Deutsch](https://docs.zapq.io/de/guides/runners)

Poradnik prowadzi przez wymagania .NET, dodanie runnera w panelu, token, adres
instancji, instalator Windows i sprawdzenie połączenia. Obejmuje też konfigurację
równoczesnych zadań, aktualizację i odinstalowanie.

Instalator i pliki programu pozostają w tym repozytorium:

- [Instalator Windows: install.ps1](./install.ps1)
- [Wydania i paczki do pobrania](https://github.com/zapqio/dotnet-runner/releases)

## Moduły

Połączenie runnera nie udostępnia metod, dopóki nie zostanie wgrany moduł.
Wgrywanie ze źródeł i CI, lokalne zatwierdzenie, restart oraz sprawdzenie wyniku
opisuje [poradnik modułów runnera zewnętrznego](https://docs.zapq.io/pl/guides/external-runner-modules).

## Dokumentacja techniczna

- [Konfiguracja, usługa, logi i diagnostyka](./docs/szczegoly.md)
- [Mechanizm zatwierdzania i instalowania modułów](./docs/wdrozenia.md)
- [Pisanie modułów .NET](./docs/moduly.md)
- [Szablon modułu .NET](https://github.com/zapqio/dotnet_runner_template)
