# Major Core

Implementazione indipendente in C#/.NET 8. Non contiene codice, risorse o protocolli recuperati da Vysor. ADB e scrcpy sono eseguibili separati, selezionati con percorsi assoluti e avviati senza shell.

## Contratto

- `ToolPaths(baseDirectory)` cerca `tools/scrcpy/adb.exe`, `scrcpy.exe` e `scrcpy-server` rispetto alla cartella portable.
- `DeviceService.GetDevicesAsync()` esegue `adb devices -l`, distingue `device`, `unauthorized`, `offline`, recovery/bootloader e trasporti USB, Wi-Fi, emulatore. Il modello mancante è `Android`.
- `PairAsync(endpoint, code)` usa `adb pair endpoint`, fornisce il codice tramite stdin e lo oscura nell'output restituito. Accetta esattamente sei cifre. Timeout 45 s.
- `ConnectAsync(endpoint)` controlla anche il testo della risposta: ADB può restituire exit code 0 per una connessione fallita. Timeout 20 s.
- `DisconnectAsync(serial)` è per connessioni Wi-Fi con seriale `host:porta`; non termina il server ADB né scollega tutti i dispositivi.
- `ScreenshotAsync(serial, path)` usa `adb -s serial exec-out screencap -p`; copia i byte direttamente, verifica la firma PNG, limita a 128 MiB, rinomina il temporaneo solo dopo exit code 0. Timeout 25 s. Richiede `.png` e rifiuta file esistenti.
- `MirrorService.StartAsync(device, options)` avvia una finestra scrcpy per seriale. Il token copre l'avvio; la sessione continua finché chiusa dall'utente o con `StopAsync`/`StopAllAsync`. `IsRunning` include l'attesa di riconnessione.
- `Log(serial, text)` è chiamato da thread di lavoro: la UI deve fare dispatch. Ogni riga è limitata a 2000 caratteri. La UI deve limitare anche il numero di righe conservate. `Exited(serial, code)` segue la rimozione della sessione.
- `StopAsync` prova la chiusura nativa della finestra (così scrcpy finalizza la registrazione), attende al massimo 6 s e poi termina solo l'albero del proprio processo. Nessun `kill-server` ADB.

## Opzioni e comportamento

`MaxSize=0` conserva la dimensione originale; altrimenti il lato maggiore è limitato tra 128 e 8192 px. Bitrate 1–100 Mbps e frame rate massimo 1–240 fps. Il frame rate effettivo dipende dal contenuto e dal dispositivo. Default: 1920 px, 8 Mbps, 60 fps, `StayAwake=true`, `Reconnect=true`, tastiera SDK. `UseUhidKeyboard=true` passa `--keyboard=uhid` per layout hardware/Unicode, dove supportato.

Mouse, trascinamento, scroll, tastiera, appunti, ridimensionamento, fullscreen e rotazione sono gestiti dalla finestra scrcpy. Non viene ricreato il protocollo proprietario Vysor. L'audio è disabilitato esplicitamente (`--no-audio`); non rientra nel percorso minimo richiesto.

La registrazione è scelta prima dell'avvio (`RecordPath` `.mkv` o `.mp4`, file nuovo). Una riconnessione crea un segmento con suffisso `-reconnect-N` per preservare il precedente. La qualità cambiata viene applicata alla prossima sessione.

Solo exit code 2 di scrcpy (dispositivo scollegato) attiva la riconnessione: massimo 10 tentativi, intervallo minimo 3 s, comandi ADB con timeout, annullabile immediatamente con Stop. Errori di avvio (exit 1) e chiusura volontaria (exit 0) non aprono loop. Per Wi-Fi con endpoint esplicito viene ritentato `adb connect`; i seriali mDNS vengono attesi dalla lista ADB. L'attesa totale può superare 30 s perché comprende i timeout ADB.

## Limiti verificabili

I test con processi finti validano il ciclo dei processi e i byte, non attestano un encoder Android, input touch reale, latenza, clipboard OEM o finalizzazione video su perdita del cavo. Le registrazioni possono essere incomplete su disconnessione o terminazione forzata. L'output screenshot viene controllato tramite firma PNG, non con un decoder completo.

Flag verificati contro l'help locale `docs/scrcpy-help-5.0.txt` del bundle ufficiale scrcpy 5.0 e documentazione upstream: [video](https://github.com/Genymobile/scrcpy/blob/v5.0/doc/video.md), [control](https://github.com/Genymobile/scrcpy/blob/v5.0/doc/control.md), [connection](https://github.com/Genymobile/scrcpy/blob/v5.0/doc/connection.md).
