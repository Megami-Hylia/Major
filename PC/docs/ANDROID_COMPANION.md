# App Android e ricevitore LAN — Major 0.2.0

## Architettura desktop esaminata

Il desktop 0.1.0 avvia `scrcpy.exe` con un seriale ADB selezionato (`src/Major.Core/MirrorService.cs`, `StartProcess/BuildArguments`). Il client scrcpy 5.0 decodifica H.264 attraverso FFmpeg e mostra il video nella propria finestra SDL. Il server viene trasferito sul telefono e avviato via ADB shell; non è un'app MediaProjection installabile e autorizzata da un utente normale. Il pannello WPF non aveva un socket di ricezione video.

La connessione USB o Wi-Fi esistente è gestita da ADB: server locale TCP 5037, autorizzazione RSA USB oppure `adb pair` e `adb connect` con le porte di Debug wireless mostrate da Android. Le porte di associazione e connessione sono assegnate dal telefono. Le chiavi ADB e il protocollo scrcpy sono diversi dal consenso MediaProjection e non vengono riutilizzati per l'app autonoma.

Per questo motivo l'app Android introduce **MajorStream 1**, un percorso aggiuntivo per il solo video LAN. Le modifiche desktop sono: libreria `Major.Streaming`, finestra `ReceiverWindow`, pulsante “Ricevi da Android”, flag `--receive` per aprirla direttamente e riferimento al nuovo progetto. Il percorso scrcpy e le sue funzioni rimangono utilizzabili. La modalità Android autonoma non offre audio, controllo remoto, appunti, screenshot o registrazione dalla UI nel primo rilascio.

## Percorso video

Android: consenso di sistema → foreground service `mediaProjection` → singolo VirtualDisplay → Surface di MediaCodec H.264 → messaggi MajorStream su TLS. Il desktop autentica il telefono, decodifica con le DLL FFmpeg già distribuite (`avcodec-63.dll`, `avutil-61.dll`) e visualizza i pixel BGRA in una WriteableBitmap WPF. Il rendering conserva solo il frame più recente; non accumula una coda video illimitata. Non vengono distribuiti nuovi eseguibili codec.

Il primo rilascio trasmette H.264 AVC a 8 bit SDR. La risoluzione configurata limita il lato massimo e mantiene le proporzioni; rotazione e cambio della regione acquisita inviano una nuova configurazione e un nuovo keyframe. La decodifica desktop è software, mentre Android preferisce un encoder hardware disponibile. Non viene dichiarata una latenza misurata senza riportare il relativo test.

## Associazione e sicurezza

L'utente apre la finestra di ricezione sul PC e inserisce sul telefono indirizzo LAN, porta 49977 e codice temporaneo mostrato. Il codice di 32 caratteri Base32, raggruppato per la lettura, combina un pin di certificato da 80 bit e un segreto casuale da 80 bit. Vale 120 secondi ed è monouso. I caratteri sono più numerosi di un PIN di sei cifre per autenticare il ricevitore senza un certificato emesso da un servizio esterno o un passaggio di fiducia implicita.

L'app verifica il pin prima di inviare il segreto. Dopo la prima associazione memorizza il pin SHA-256 completo e un token casuale da 256 bit. I successivi collegamenti verificano il certificato completo e il token. Il trasporto usa TLS, non esistono modalità trust-all o video in chiaro. I dati di associazione Android sono cifrati con una chiave Android Keystore; il PC protegge certificato privato e credenziali con Windows DPAPI. La revoca invalida i telefoni salvati sul PC. Non condividere il codice al di fuori del dispositivo da associare.

La finestra deve restare aperta per ricevere. Il listener è locale al PC e non configura router, port forwarding o regole firewall; per una connessione Wi-Fi reale può essere necessario consentire Major sulla rete privata di Windows. Non esporre il listener su Internet. Indirizzo e nome del PC sono identificativi visibili nella UI, non una prova di autenticità: la verifica crittografica è il pin.

## Requisiti Android verificati nella documentazione ufficiale

La cattura richiede consenso per ogni nuova sessione. Da Android 14 il token e la chiamata `createVirtualDisplay` non sono riutilizzabili; la rotazione deve ridimensionare il display/surface esistenti. Il callback `onStop` chiude le risorse e impedisce la riattivazione automatica di una proiezione terminata. La notifica mantiene visibile la condivisione e include Stop. Il servizio non riparte automaticamente dopo terminazione o riavvio. [MediaProjection](https://developer.android.com/media/grow/media-projection).

Il manifest dichiara il servizio non esportato di tipo `mediaProjection`, `FOREGROUND_SERVICE` e `FOREGROUND_SERVICE_MEDIA_PROJECTION`. Si richiede il consenso prima dell'avvio del servizio e si porta il servizio in foreground prima di ottenere la MediaProjection. [Tipi foreground service](https://developer.android.com/develop/background-work/services/fgs/service-types#media-projection).

L'app target API 36 usa `INTERNET` per il collegamento LAN. La documentazione corrente richiede `ACCESS_LOCAL_NETWORK` solo con target API 37 o superiore; alzando il target occorrerà aggiornare il flusso di permessi. [Permesso rete locale](https://developer.android.com/privacy-and-security/local-network-permission).

Le istruzioni Android, le versioni della toolchain e i risultati delle prove sono consegnati nel progetto `MajorAndroid`. Le prove effettuate su emulatori e con frame sintetici sono distinte da quelle su un telefono fisico.
