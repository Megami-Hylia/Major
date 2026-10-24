# API del ricevitore AirPlay per Windows

`AirPlayService(string toolsDirectory, string sessionDirectory)` usa il bundle nativo `tools/airplay`, senza installazioni globali. Il secondo percorso deve essere scrivibile: contiene soltanto una configurazione vuota gestita dal programma e la cache dei plugin GStreamer; non contiene PIN, password o database di associazione.

```csharp
await using var receiver = new AirPlayService(bundlePath, cachePath);
receiver.StatusChanged += status => Dispatcher.BeginInvoke(() => Show(status));
receiver.PinChanged += pin => Dispatcher.BeginInvoke(() => ShowPin(pin));
receiver.DiagnosticLog += line => Dispatcher.BeginInvoke(() => AddDiagnostic(line));
await receiver.StartAsync(videoHostHandle, "Major", lifetimeToken);
await receiver.StopAsync();
```

Il parametro video è un HWND valido appartenente al processo chiamante, predisposto prima di `StartAsync` e mantenuto in vita fino al completamento di `StopAsync`. Il controllo WPF `HwndHost` gestisce ridimensionamento, rapporto d'aspetto e schermo intero attraverso il sink D3D11. Il chiamante cancella il vecchio contenuto del suo HWND dopo stop/disconnessione. Il nome ha massimo 50 byte UTF-8 e non può contenere controlli o iniziare con `-`.

La firma pubblica comprende `State`, `IsRunning`, `CurrentPin`, `StartAsync(IntPtr, string, CancellationToken)`, `StopAsync()` e `DisposeAsync()`. Gli eventi sono `Action<AirPlayStatus> StatusChanged`, `Action<string?> PinChanged` e `Action<string> DiagnosticLog`. Le callback possono arrivare da thread di lavoro: devono essere brevi, fare dispatch sulla UI e non attendere sincronicamente il servizio. Gli errori nei subscriber vengono isolati per non lasciare il server nativo acceso. Chiusure simultanee condividono l'operazione di stop; dispose impedisce ulteriori avvii.

`Ready` significa che il processo ha inizializzato i renderer e registrato i servizi mDNS locali, non che un altro dispositivo li abbia già scoperti. `Authenticating` accompagna il PIN a quattro cifre richiesto dall'accesso AirPlay. `Connected` indica negoziazione del codec; `Streaming` deriva dal primo buffer decodificato arrivato al pad del sink video H.264. Questo segnale non certifica che il monitor mostri un contenuto particolare né che gli altoparlanti emettano audio. Video e audio sono entrambi abilitati; non viene usato un ripiego con solo audio.

Il bundle deriva da UxPlay `15a6b87172eca38b0df4e615fbf1bba108734dde`, con patch pubblicata in `third-party/airplay/major-embedded.patch`. La modalità incorporata usa eventi su stdout con nonce casuale per processo, separati dalla diagnostica. I PIN passano solo dall'evento dedicato e non vengono salvati. Le righe diagnostiche hanno limite 2048 caratteri, 40 righe/secondo, controlli rimossi e messaggi sensibili omessi; l'interfaccia deve anche limitare il proprio storico.

Il processo parte senza shell, con argomenti separati, configurazione propria e percorsi GStreamer del bundle. Riceve `-pw` senza password fissa, `-vs d3d11videosink`, `-as wasapisink`, `-nofreeze`, TCP 7000–7002 e UDP 6000–6002. La scoperta usa multicast UDP 5353. Il firewall deve consentire il programma sulla rete locale privata; il servizio non modifica il firewall. Il PIN usa l'autenticazione AirPlay nativa di UxPlay, non il protocollo TLS dell'app Android.

Stop invia `STOP` sulla pipe privata: UxPlay interrompe il main loop e rimuove gli annunci mDNS. Dopo due secondi il wrapper termina l'albero dei processi. Un Windows Job Object con `KILL_ON_JOB_CLOSE` protegge anche dalla chiusura improvvisa del processo desktop; la chiusura del job elimina gli eventuali plugin scanner. Il job è assegnato subito dopo l'avvio del processo nativo; il wrapper invia poi `START`, indispensabile per inizializzare GStreamer e i suoi processi figli. Se il padre si chiude prima dell'assegnazione, EOF fa terminare il helper ancora in attesa. L'API non registra automaticamente privilegi elevati, servizi Windows o avvii automatici.
