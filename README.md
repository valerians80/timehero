# TimeHero

Post-it per Windows 11 che traccia le attività della giornata lavorativa e aiuta a compilare i timesheet.

- **Post-it** ancorato sopra la taskbar (icona nella tray, oppure `Ctrl+Alt+T`).
- **Attività con titolo e più sessioni:** scrivi un titolo (es. "Deploy VM per Contoso") e scegli il tipo per avviarla.
  Avviandone un'altra la precedente va **in pausa** e compare come **riquadro rosso**: un clic la riprende (nuova
  sessione sulla *stessa* attività). Una sola attività accumula tempo alla volta; solo **✔ Fine** chiude davvero.
  Nello storico c'è **una riga per attività e per giorno** con il tempo reale totale, comunque tu l'abbia interrotta.
- **Notifica Windows ogni 5 minuti** con l'attività in corso (pulsanti *Termina* / *Apri*).
- **Screenshot** allegati all'attività in corso (ritaglio di Windows, oppure Ctrl+V dagli appunti).
- **Storico**: per attività, giorno/settimana/mese, totali per cliente/categoria/giorno, arrotondamento (5–30 min),
  modifica manuale, *Copia riepilogo* e **export CSV** (Excel italiano).
- **Protezioni**: chiede cosa fare dopo un'assenza (inattività/blocco schermo) o se l'app viene chiusa con un'attività aperta.
- Database locale SQLite: `%LOCALAPPDATA%\TimeHero\timehero.sqlite` (screenshot in `Attachments\`).

## Struttura

| Progetto | Contenuto |
|---|---|
| `src/TimeHero.Core` | modello, SQLite, logica avvia/termina, report/CSV (multipiattaforma, testato) |
| `src/TimeHero.App` | app WPF (.NET 8, solo Windows): post-it, tray, toast, storico, impostazioni |
| `tests/TimeHero.Core.Tests` | test xUnit del Core |

## Build (su Windows)

```powershell
dotnet test tests/TimeHero.Core.Tests
dotnet run --project src/TimeHero.App
# eseguibile singolo:
dotnet publish src/TimeHero.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```
Requisiti: .NET 8 SDK, Windows 10 19041+ / Windows 11.

> Nota: il flyout dell'orologio di Windows non è estensibile; il post-it riproduce lo stesso effetto con una
> finestra senza bordi posizionata in basso a destra.
