# Specifica funzionale di Major 0.2.0

Major 0.2.0 contiene la modalità scrcpy/ADB introdotta in 0.1.0 e il nuovo ricevitore per l'app Android autonoma. Le sezioni storiche ADB sottostanti restano valide solo per quel percorso: non descrivono il protocollo o i permessi dell'app MediaProjection. Per l'architettura nuova vedere [ANDROID_COMPANION.md](ANDROID_COMPANION.md), per l'app [ANDROID_IMPLEMENTATION.md](../../MajorAndroid/docs/ANDROID_IMPLEMENTATION.md) e per i suoi esiti [TESTS.md Android](../../MajorAndroid/docs/TESTS.md).

La specifica distingue implementazione, verifica automatica, collaudo su emulatore e prove ancora necessarie su un telefono fisico. Non dichiara equivalenza funzionale o comportamentale completa con Vysor. I fatti ricavati dall'eseguibile di riferimento appartengono al [rapporto di reverse engineering](../../reports/REVERSE_ENGINEERING.md); non vengono usati come sorgente dell'implementazione.

## Due modalità esplicite

| Aspetto | scrcpy/ADB, da 0.1.0 | App Android autonoma, da 0.2.0 |
| --- | --- | --- |
| Ingresso desktop | Seleziona dispositivo e Avvia mirroring | Ricevi da Android, oppure avvio `--receive` |
| Collegamento | USB o Debug wireless ADB | LAN TCP/TLS sulla porta PC 49977 |
| Associazione | Autorizzazione RSA o codice Debug wireless Android | Codice temporaneo Base32 del PC, pin certificato e token dedicato |
| Permessi telefono | Debug USB/wireless e privilegi ADB shell per server scrcpy | Consenso MediaProjection esplicito, foreground service e notifica; niente root o ADB |
| Video sul PC | Finestra SDL del processo scrcpy | Decoder `Major.Streaming` e immagine WPF integrata |
| Funzioni | Video, input, appunti, screenshot, registrazione, sessioni per seriale | Solo video, qualità/fps, rotazione, stop e recupero rete nella sessione valida |
| Prove | Test core e iniziali prove senza dispositivo | Test TLS/decoder reali e collaudo Android 36 emulato; telefono fisico ancora da verificare |

Le due modalità possono esistere nello stesso desktop ma non condividono token, consenso o protocollo. Screenshot, registrazione, appunti e controllo remoto del pannello ADB non vengono applicati al video ricevuto dall'app autonoma nel primo rilascio.

### Flusso della nuova app autonoma

1. Sul PC aprire Ricevi da Android; il listener resta attivo solo con quella finestra aperta. Scegliere l'indirizzo LAN da riportare sul telefono.
2. Sul telefono inserire indirizzo, porta e codice temporaneo completo. Il pin viene verificato prima del segreto; il successo memorizza credenziali protette e mostra il nome PC, senza iniziare a catturare.
3. Scegliere qualità e frame rate, premere Condividi schermo e rispondere al dialog Android. Un consenso rifiutato non deve inviare frame.
4. La cattura MediaProjection codifica H.264 e invia CONFIG/CSD/IDR e frame su TLS. La finestra PC conserva soltanto il frame più recente per il rendering. La rotazione ridimensiona la stessa sessione e invia una nuova configurazione.
5. Interrompere dal telefono, dalla notifica, dal chip di sistema o dal PC. La finestra PC cancella l'immagine terminata. Revoca telefoni associati invalida i token e richiede una nuova associazione.
6. Una rete persa può essere recuperata solo entro le regole della MediaProjection ancora valida. Dopo `onStop`, blocco schermo o terminazione dell'app non si riutilizza il vecchio consenso: una nuova cattura ripassa dal dialog di sistema.

I dettagli wire, la sicurezza e i timeout sono in [ANDROID_COMPANION.md](ANDROID_COMPANION.md) e [API del ricevitore](../src/Major.Streaming/API.md). Il collaudo osservato su Android 36 emulato include associazione, annullamento del consenso, video MediaProjection visibile in WPF e rotazione fra 590×1280 e 1280×590. Esiti e limiti completi delle sessioni Android sono nel [report Android](../../MajorAndroid/docs/TESTS.md); queste prove non misurano latenza o compatibilità con encoder OEM fisici.

## Ambito e architettura della modalità scrcpy/ADB

Obiettivo principale: selezionare un Android autorizzato via ADB su Windows, aprire il suo schermo in una finestra dedicata e controllarlo con mouse e tastiera. Nessuna funzione implementata richiede account, abbonamenti o API a pagamento.

Lo stack scelto è C#/.NET 8 con WPF per il pannello Windows, una libreria `Major.Core` per validazione/comandi/processi e gli eseguibili upstream scrcpy 5.0 e ADB per il collegamento Android. La build portable self-contained include il runtime .NET; non richiede di installare l'SDK per l'uso. Client scrcpy, server Android e DLL vengono distribuiti insieme senza alterarne il codice. Licenze, avvisi e sorgenti delle dipendenze sono documentati in [DEPENDENCIES.md](DEPENDENCIES.md) e [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).

L'interfaccia Major è una finestra WPF originale con marchio vettoriale originale. Nel percorso ADB lo schermo Android si apre nella finestra nativa scrcpy: può essere ridimensionato, ruotato o portato a schermo intero. La modalità aggiunta in 0.2.0 usa invece il renderer nella finestra WPF ReceiverWindow.

Evidenze dell'implementazione: [MainWindow.xaml](../src/Major/MainWindow.xaml), [MainWindow.xaml.cs](../src/Major/MainWindow.xaml.cs), [MirrorService.cs](../src/Major.Core/MirrorService.cs), [DeviceService.cs](../src/Major.Core/DeviceService.cs), [ToolPaths.cs](../src/Major.Core/ToolPaths.cs).

## Protocollo e responsabilità della modalità scrcpy/ADB

Major passa argomenti validati a scrcpy e ADB tramite `ProcessStartInfo.ArgumentList`, senza shell. Imposta i percorsi assoluti `ADB` e `SCRCPY_SERVER_PATH`. Il protocollo scrcpy rimane responsabilità del client/server upstream compatibile della stessa versione.

Secondo la [documentazione di sviluppo upstream](https://github.com/Genymobile/scrcpy/blob/v5.0/doc/develop.md), scrcpy trasferisce il server sul dispositivo e lo esegue con i privilegi ADB shell tramite `app_process`. I canali separano video e controllo; il video è codificato da Android tramite MediaCodec, decodificato sul PC con FFmpeg e mostrato da SDL. Il canale di controllo trasporta input e messaggi appunti in entrambe le direzioni. Il tunnel usa ADB reverse con fallback forward. Questa versione Major disabilita l'audio. La documentazione del protocollo upstream conserva esempi riferiti a scrcpy 4.0: non vengono assunti come specifica wire verificata della 5.0.

Screenshot: percorso separato `adb -s serial exec-out screencap -p`; i byte PNG sono copiati direttamente sul disco. Pairing e connessione Wi-Fi: comandi ADB `pair` e `connect`, con endpoint e codice validati. Nessun protocollo di rete Vysor viene implementato.

## Matrice dei requisiti della modalità scrcpy/ADB

“Implementato” significa che il percorso esiste nei sorgenti. “Simulato” indica test con processi di prova. “Reale senza dispositivo” usa i veri eseguibili upstream e conferma il relativo errore/assenza dispositivo. La tabella seguente riguarda esclusivamente il percorso ADB e la sua verifica iniziale 0.1.0; il collaudo dell'app autonoma non attesta queste funzioni su hardware fisico.

| Requisito | Stato dell'implementazione | Evidenza e verifica | Ancora da verificare su hardware |
| --- | --- | --- | --- |
| Rilevamento USB/ADB | Implementato, polling ogni 3 s, modello/seriale/stato/trasporto | `RefreshDevicesAsync`, `DeviceService.ParseDevices`; test simulati e query ADB reale con lista vuota | Driver OEM, apparizione/rimozione e tempi |
| Autorizzazione ADB | Stato `unauthorized` visibile, avvio disabilitato e guida RSA | `UpdateSelection`, `StartAsync` rifiuta dispositivo non autorizzato | Prompt RSA e passaggio a pronto |
| Mirroring USB | Avvio scrcpy con selezione esplicita seriale | `StartProcess`, `BuildArguments`; scrcpy reale avviato con seriale inesistente, exit 1 atteso | Primo frame, codec, qualità e stabilità |
| Latenza contenuta | Usa pipeline scrcpy senza buffer video aggiuntivo | Argomenti e default scrcpy; nessun numero di latenza misurato | Misura sul telefono e carico reale |
| Clic, drag, scroll | Gestiti nella finestra scrcpy con input SDK di default | Documentazione/helpline upstream e argomenti senza `--no-control` | Coordinate, gesture, permessi OEM |
| Tastiera | SDK di default, UHID opzionale nella UI | `UseUhidKeyboard`, checkbox UHID, help locale | Unicode, layout italiano, tasti speciali |
| Rotazione | Shortcut vista PC e richiesta Android documentati | `HelpWindow.xaml`, `scrcpy-help-5.0.txt` | Orientamento app e touch dopo rotazione |
| Ridimensionamento/fullscreen | Finestra scrcpy nativa, checkbox fullscreen e shortcut | `Fullscreen`, Alt sinistro+F/F11 nell'help | Scaling, DPI, più monitor |
| Risoluzione/bitrate/fps | Selezione UI, validazione core, applicazione al nuovo avvio | `MirrorOptions.Validate`, `ReadOptions`, test argomenti | Rispetto dei limiti da encoder e fps effettivi |
| Screenshot | PNG su file nuovo, controllo firma e rinomina atomica | `ScreenshotAsync`, test binari simulati ed errore ADB reale pulito | Acquisizione e orientamento immagine reale |
| Registrazione | MP4 selezionato prima dell'avvio; API supporta anche MKV | `RecordCheck`, `RecordPath`, `--record`; test argomenti e no sovrascrittura | File riproducibile e finalizzazione normale/interrotta |
| Wi-Fi guidato | Finestra con pairing a codice e connessione separata Android 11+ | `WifiWindow`, `PairAsync`, `ConnectAsync`; test simulati input/redazione/failure | Pairing reale, firewall, porte, cambio rete |
| Più dispositivi | Selezione indipendente e una sessione per seriale | `activeDevices`, dictionary core; test 12 processi concorrenti | Due Android reali e indipendenza input/file |
| Riconnessione | Exit scrcpy 2: fino a 10 tentativi, annullabili con Stop | `SuperviseAsync`; test attesa/Stop e nessun loop su exit 1 | Stacco/riattacco USB e perdita Wi-Fi |
| Appunti condivisi | Funzione scrcpy predefinita, istruzioni Ctrl+V/Alt+C | `HelpWindow`, nessuna disabilitazione autosync | Restrizioni Android/app/OEM; SDK/UHID |
| Impostazioni persistenti | Promajor globale qualità/comportamento/cartella | `UserSettings.Load/Save`, JSON locale atomico | Percorsi Windows/permessi account reali |

Non sono esposte in questa versione audio, webcam, fotocamera, virtual display, gamepad, OTG, condivisione remota via cloud, accesso al browser o installazione driver automatica. La guida Wi-Fi copre il pairing Android 11+; la conversione automatica USB → `adb tcpip` per telefoni meno recenti non è implementata nella UI.

## Schermate e controlli della modalità scrcpy/ADB

**Pannello principale.** Barra laterale con elenco dispositivi, aggiornamento manuale, accesso Wi-Fi e cartella acquisizioni. Ogni riga mostra modello, seriale e pronto/autorizzazione richiesta/non in linea/sessione/riconnessione. Il riquadro del dispositivo mostra stato e istruzione contestuale. Avvio, Termina sessione e Screenshot dipendono dalla selezione e dallo stato.

**Qualità e comportamento.** Lato massimo originale/960/1280/1920/2560 px; bitrate 2/4/8/16/32 Mbps; limite 30/60/90/120 fps. Default: 1920 px, 8 Mbps, 60 fps. Checkbox telefono attivo, schermo spento, fullscreen, riconnessione, tastiera UHID. Modifiche applicate alla prossima sessione. Il core accetta range più ampi, ma la UI propone solo questi valori.

**Acquisizioni.** Checkbox registrazione prima dell'avvio, cartella visibile e modificabile, pulsante apertura cartella. La registrazione non può essere attivata su una sessione già avviata: occorre terminarla e riavviarla. Screenshot disponibile per un dispositivo autorizzato anche prima di iniziare il mirroring.

**Wi-Fi.** Prima sezione: indirizzo di associazione e codice mascherato di sei cifre; seconda: indirizzo di connessione. La guida esplicita che le due porte sono normalmente diverse. È possibile saltare pairing se già associati. Il codice è cancellato dal campo subito dopo il clic, inviato via stdin e mai persistito.

**Guida e comandi.** Preparazione USB, tasti nativi scrcpy, permessi input OEM, limiti di tastiera/appunti e finalizzazione video. Tutti gli shortcut richiedono il focus nella finestra del telefono. `Alt sinistro+R` richiede rotazione dispositivo; `Alt sinistro+Shift+R` resetta la cattura video e non è un comando di rotazione.

**Registro sessioni.** Espandibile, sola lettura, conserva al massimo 160 righe da 1800 caratteri nella UI. Mostra avvio, output del motore, errore e riconnessione; i diagnostici sono trattati come testo. Lo stato operativo è distinto dall'elenco dispositivi.

## Flussi e stati della modalità scrcpy/ADB

### USB

1. Aprire Major con l'intera cartella portable estratta.
2. Abilitare opzioni sviluppatore e Debug USB sul telefono; collegare un cavo dati.
3. Se appare “Autorizzazione richiesta”, sbloccare Android e accettare l'impronta RSA. Il polling aggiorna lo stato.
4. Selezionare il telefono pronto, impostare la qualità e l'eventuale registrazione, premere Avvia mirroring.
5. Usare la finestra dedicata con mouse/tastiera. Avvio richiesto indica che il processo è stato avviato: l'eventuale errore successivo appare nel registro.
6. Termina sessione oppure chiusura della finestra Android chiudono la sessione. L'app non termina globalmente il server ADB.

### Disconnessione e riconnessione

Su exit 2 del motore e opzione attiva, la sessione resta elencata in attesa. Il core attende almeno 3 s tra tentativi, interroga ADB e riavvia lo stesso seriale quando autorizzato. Per endpoint Wi-Fi espliciti prova anche `adb connect`; i seriali mDNS vengono attesi nella lista. Massimo 10 tentativi complessivi per sessione. I timeout ADB si aggiungono all'intervallo, quindi l'attesa può superare 30 s. Termina sessione cancella l'attesa. Exit 0 (chiusura normale) e 1 (errore avvio) non avviano loop.

Se si stava registrando, la riconnessione usa un nuovo nome `-reconnect-N`; non riprende sul precedente file. La precedente registrazione può risultare incompleta dopo una perdita improvvisa.

### Wi-Fi con codice

1. Android 11+: stessa rete raggiungibile, Debug wireless → Associa dispositivo con codice.
2. Copiare indirizzo e porta di associazione, inserire codice; mantenere aperto il dialog Android fino all'esito.
3. Tornare alla schermata principale Debug wireless, riportare indirizzo e porta di connessione e premere Connetti.
4. Chiudere la finestra guida e selezionare il dispositivo Wi-Fi comparso nell'elenco.
5. Per revocare l'associazione usare i controlli Android. Chiudere la finestra video termina il mirroring, ma non revoca le chiavi di pairing né disabilita Debug wireless.

### File e preferenze

Default acquisizioni: cartella Video dell'utente, sottocartella `Major`. I nomi includono data/ora e suffisso casuale, senza utilizzare input shell. Un file esistente viene rifiutato dal core. Preferenze globali in `%LOCALAPPDATA%/Major/settings.json`; `RecordPath` è stato di sessione e non viene ripristinato come preferenza. Nessuna credenziale pairing viene scritta nel JSON.

## Errori e recupero della modalità scrcpy/ADB

| Condizione | Risposta implementata | Azione possibile |
| --- | --- | --- |
| Bundle tools mancante/incompleto | Errore di percorso o `ToolPaths.Validate`, avvio rifiutato | Estrarre tutto il pacchetto o eseguire bootstrap |
| Nessun dispositivo | Lista vuota e guida USB/Wi-Fi | Verificare cavo dati, driver e Debug USB |
| `unauthorized` | Istruzione RSA e Avvio disabilitato | Sbloccare Android e autorizzare |
| `offline`/recovery/bootloader | Stato non pronto, Avvio disabilitato | Ripristinare normale Android, cavo o autorizzazione |
| Endpoint/codice non valido | Validazione prima del comando; messaggio nel dialog | Correggere host:porta e sei cifre |
| Pairing/connessione rifiutati | Errore ADB, inclusi fallimenti connect con exit 0 | Ricontrollare porta giusta, codice, rete e firewall |
| ADB bloccato | Timeout e terminazione del comando figlio | Controllare telefono e riprovare |
| Encoder/server/input non supportati | Output scrcpy nel registro e codice uscita | Ridurre qualità, disattivare UHID, verificare permessi OEM |
| Cartella non scrivibile/file esistente | Eccezione mostrata, niente sovrascrittura core | Scegliere cartella o nome nuovo |
| Screenshot fallito/non PNG | Temporaneo eliminato, nessun file parziale pubblicato | Ricollegare e riprovare |
| Chiusura scrcpy non conclusa | Chiusura nativa, poi kill del solo albero dopo 6 s | Controllare il video potenzialmente incompleto |
| Preferenze illeggibili | Default e diagnostico nel registro | Correggere/rimuovere il JSON o salvare nuove impostazioni |

## Checklist manuale della modalità scrcpy/ADB su dispositivo fisico

Questa checklist è **da eseguire**: nessuna casella qui sotto è attestata dal test automatico. Registrare modello, versione Android, driver, tipo cavo, risoluzione, bitrate, fps, risultato e log per ciascuna prova.

- [ ] USB con telefono non autorizzato: compare istruzione RSA, Avvio disabilitato; accettare e verificare stato pronto senza riavvio app.
- [ ] Avvio USB: compare primo frame leggibile; provare 1280/4/30 e 1920/8/60, registrare tempi e latenza misurata.
- [ ] Input: clic agli angoli e al centro, trascinamento lungo, scroll, Home/Indietro/Recenti; ripetere dopo rotazione.
- [ ] Tastiera SDK e UHID: testo italiano, accenti, simboli, tasti modificatori, layout hardware Android, app diverse.
- [ ] Ridimensionare, fullscreen F11/Alt+F, ruotare vista Alt+frecce e dispositivo Alt+R; spostare su monitor con DPI diversi.
- [ ] Screenshot prima/durante mirroring: PNG decodificabile, dimensioni/orientamento corretti, file preesistente preservato.
- [ ] Registrazione MP4: avviare con checkbox, usare Android per almeno 30 s, terminare normalmente; aprire il file e cercare l'ultimo secondo per attestare finalizzazione.
- [ ] Staccare cavo durante uso e durante recording; ricollegare stesso seriale, verificare ripresa e nuovo segmento, controllare separatamente leggibilità di entrambi i file.
- [ ] Interrompere l'attesa di riconnessione con Termina; verificare assenza riaperture del processo. Chiudere volontariamente la finestra video e verificare che non riparta.
- [ ] Due Android: aprire due finestre, verificare input isolato, screenshot del seriale corretto e stop di uno senza interruzione dell'altro.
- [ ] Appunti: copia Android → PC e PC → Android con Ctrl+V e Alt+C/V, testo Unicode; annotare app/versioni dove la funzione è limitata.
- [ ] Pairing Wi-Fi Android 11+: codice errato/corretto, porta di pairing scambiata con quella di connessione, associazione già esistente, rete non raggiungibile.
- [ ] Wi-Fi: mirroring e input senza USB, perdita/restituzione rete, tentativi limitati e Stop durante attesa; provare contemporaneamente USB di un altro dispositivo.
- [ ] Chiudere Major con due sessioni e recording attivi; verificare processi scrcpy conclusi, nessun kill globale ADB, file finalizzati quando possibile.
- [ ] Utenza Windows senza permessi amministratore, percorso con spazi/Unicode e cartella output non scrivibile: messaggi comprensibili e nessuna perdita dati.

Le verifiche iniziali del percorso ADB sono in [TESTS_CORE.md](TESTS_CORE.md). Per la modalità app autonoma usare la checklist [ANDROID_E2E_HARNESS.md](ANDROID_E2E_HARNESS.md), i [test del ricevitore](STREAM_RECEIVER_TESTS.md) e il [report Android](../../MajorAndroid/docs/TESTS.md). Il supporto documentato upstream di una funzione non equivale al suo collaudo sul telefono dell'utente.
