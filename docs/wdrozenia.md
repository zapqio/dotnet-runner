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
