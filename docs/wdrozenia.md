# Wdrożenia modułów z platformy

Moduły można wdrażać na runnera z platformy Zapqio, zamiast wgrywać je ręcznie do `Modules\`.
Każde wdrożenie wymaga zgody administratora maszyny.

Obsługa runnera jest w [szczegoly.md](./szczegoly.md), pisanie modułów w [moduly.md](./moduly.md).

## Polecenia

W terminalu administratora, w katalogu instalacji. Zamiast `deploy` można pisać `deployments`.

```powershell
.\Zapqio.Runner.exe deploy list                  # lista wdrożeń i ich stan (alias: pending)
.\Zapqio.Runner.exe deploy show <id>             # szczegóły: commit, autor, zmienione pliki
.\Zapqio.Runner.exe deploy approve <id> [<id>…]  # zatwierdzenie jednego lub kilku wdrożeń
.\Zapqio.Runner.exe deploy approve --all         # zatwierdzenie wszystkich oczekujących
.\Zapqio.Runner.exe deploy reject <id>           # odrzucenie
```

`list`, `show` i `reject` niczego nie uruchamiają.

## Co się dzieje po `approve`

1. Runner przekazuje zgodę do Web i czeka na potwierdzenie (do 60 s). Bez potwierdzenia nic się
   nie instaluje, ale zgoda zostaje i może zostać zastosowana później — stan sprawdzisz przez `list`.
2. Usługa się restartuje — **trwające zadania są przerywane**.
3. Runner instaluje cały zatwierdzony zestaw. Jeśli którykolwiek moduł się nie załaduje, przywraca
   poprzedni zestaw.

## Źródła modułów

- **Runner** — kod budowany lokalnie przez `dotnet publish` (limit 10 min). Wymaga SDK .NET na
  maszynie. Projekt może tworzyć zip jak w [moduly.md](./moduly.md#budowanie-paczki).
- **CiZip** — gotowy artefakt z CI (GitHub/GitLab, nazwa `zapqio-module`, w środku `module.zip`).
  SDK nie jest potrzebny.

## Dobrze wiedzieć

- Nowsza wysyłka z tego samego repozytorium zastępuje starszą niezatwierdzoną.
- Wycofanie wersji i usunięcie modułu to również wdrożenia wymagające zgody. `Config\` zostaje.
- Moduły wgrane ręcznie do `Modules\` nie są przenoszone do platformy automatycznie.
- Błędy restartu: `Deployments\restart.log`; pozostałe — log runnera i historia wdrożeń w Web.

## Diagnostyka wdrożeń i blokady dziennika

Przy `Logger:LogLevel = Information` log runnera zawiera PID, start i zatrzymanie hosta,
etapy pobierania, instalacji i rollbacku oraz przyczynę restartu. Nie trzeba włączać `Debug`,
żeby zobaczyć długie oczekiwanie na `Deployments\state.lock` i błędy hosta.

- `Deployment journal lock wait` — oczekiwanie przekroczyło sekundę. Wpis podaje operację,
  jej identyfikator, PID i wątek. Jeśli blokadę trzyma inna operacja tego samego procesu,
  `owner` zawiera jej nazwę, etap i czas trzymania blokady. `unknown` oznacza, że właściciela
  nie ustalono; nie dowodzi, że blokadę trzyma inny proces.
- `Deployment journal lock still held` — co 5 sekund informuje o nadal trzymanej blokadzie,
  np. podczas `SavingBundle`, `ValidatingApprovalBundle` lub `WritingJournal`.
- `Deployment journal operation failed` — pełny wyjątek, etap, czas i właściciel blokady.
  Limit oczekiwania nadal wynosi 10 sekund; diagnostyka nie zmienia obsługi błędów ani restartu.
- `Deployment worker ... failed` i `Deployment coordinator failed` — wskazują pętlę,
  z której wyjątek dotarł do hosta. Kolejne wpisy pokazują przebieg zatrzymywania połączenia.

CLI zapisuje diagnostykę do `deployments-cli-YYYYMMDD.log` w katalogu `Logger:PathDirectory`
(zwykle `Logs`). Jest to osobny plik, aby nie zmieniać wyjścia polecenia `deploy show` ani
nie kolidować z plikiem usługi. Pusty `Logger:PathDirectory` wyłącza również plik CLI.
`Deployments\restart.log` zawiera PID helpera, PID rodzica, kolejne kroki stop/start,
limity oczekiwania i pełny wyjątek przy niepowodzeniu. Jest niezależny od konfiguracji loggera.

Do analizy zbierz log runnera, log CLI i `restart.log` z tego samego przedziału czasu.
Brak pliku CLI lub restartu może oznaczać, że dany proces nie był uruchamiany.
Przy `Debug` dochodzą wszystkie szybkie operacje dziennika i liczniki jego rozmiaru;
na co dzień `Information` ogranicza liczbę wpisów z pętli wykonywanej co 2 sekundy.
