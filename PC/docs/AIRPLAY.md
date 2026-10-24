# Ricezione iPhone e iPad — Major 0.3.0

## Uso

1. Estrarre tutta la build Windows x64 e avviare `Major.exe`.
2. Premere **Ricevi da iPhone/iPad**. L'apertura della finestra avvia il ricevitore; **Attiva ricezione** lo riavvia dopo uno stop. Il nome si può cambiare a ricevitore spento.
3. Collegare il dispositivo Apple e il PC alla stessa rete locale. Sul dispositivo aprire **Centro di Controllo → Duplica schermo**, il comando con due rettangoli sovrapposti, e scegliere il nome visualizzato sul PC.
4. Inserire sul dispositivo il codice che appare nella finestra PC. Il codice non viene salvato nelle preferenze né nel registro della UI.
5. Guardare schermo e audio sul PC. Ruotare il dispositivo cambia le proporzioni del video; la finestra è ridimensionabile. **Schermo intero / F11** e **Esci / Esc** cambiano la vista del PC.
6. Fermare da **Interrompi ricezione**, chiudendo la finestra o scegliendo **Interrompi duplicazione** sul dispositivo. Dopo uno stop sul PC riattivare il ricevitore e selezionarlo nuovamente dal dispositivo.

L'icona AirPlay nel riquadro musica e il comando AirPlay di un singolo player sono percorsi diversi: non dimostrano duplicazione dello schermo. Per questa funzione usare sempre **Duplica schermo**. Non occorrono app iOS, root, cavo USB, ADB o account.

## Architettura e ciclo di vita

Il desktop preesistente usa WPF/.NET 8 x64. Il percorso Android con scrcpy/ADB conserva il suo processo e la finestra SDL; il percorso companion Android conserva TLS/MajorStream e il viewer WriteableBitmap. Il nuovo progetto `src/Major.AirPlay` gestisce UxPlay come processo dedicato con runtime GStreamer privato; non traduce il protocollo Apple in MajorStream.

`AirPlayWindow` crea un contenitore HWND tramite `AirPlayVideoHost`. UxPlay crea al suo interno una propria finestra figlia, su un thread con ciclo messaggi Windows. Il renderer `GstVideoOverlay` riceve l'handle di questa finestra, appartenente allo stesso processo del renderer: D3D11 può così installare la propria procedura di finestra. Passare direttamente l'handle del processo WPF non funzionava e produceva un riquadro nero. Il contenuto resta nel viewer WPF; non viene catturata o riposizionata una finestra desktop esterna. Un timer adegua la finestra figlia alla dimensione del contenitore. L'audio usa il sink Windows del backend e l'uscita predefinita del sistema.

L'app gestisce avvio, lettura dei messaggi, stato, codice corrente e stop del processo. Durante la chiusura attende la terminazione del backend prima di distruggere l'HWND; il backend non è un servizio residente. I registri della UI sono limitati in numero e dimensione e filtrano il codice di autenticazione. Il nome è l'unica preferenza salvata, separatamente in `%LOCALAPPDATA%/Major/airplay-ui.json`, così il salvataggio delle impostazioni Android non lo sovrascrive.

Gli stati **in ascolto**, **autenticazione**, **collegato** e **video in ricezione** sono distinti. Un annuncio mDNS o una porta aperta non certificano l'arrivo di video.

## Backend scelto e distribuzione

UxPlay ufficiale, revisione `15a6b87172eca38b0df4e615fbf1bba108734dde`, si identifica come **1.74 experimental**. Questa revisione offre mDNS interno e permette di evitare l'installazione globale di Bonjour. Non viene presentata come release stabile o come implementazione Apple certificata. La valutazione delle alternative, le fonti e i limiti sono in [AIRPLAY_ASSESSMENT.md](AIRPLAY_ASSESSMENT.md).

La distribuzione contiene il runtime privato e gli avvisi e sorgenti corrispondenti in `third-party/airplay`. Le componenti originali Major restano MIT; UxPlay mantiene GPL-3.0-or-later e GStreamer e le sue dipendenze mantengono le rispettive licenze. Non rimuovere avvisi, ricette o sorgenti accompagnatori quando si redistribuisce il pacchetto.

Lo sviluppatore necessita della toolchain locale MinGW/CMake e degli header GStreamer per ricompilare il backend; l'utente della build pronta necessita solo di Windows x64, dei file distribuiti, di un'uscita audio e della rete locale. Non servono MSYS2, compilatore o Bonjour installati sul PC dell'utente. Dettagli riproducibili di acquisizione e compilazione sono nello script `scripts/bootstrap-airplay.ps1` e nella documentazione delle dipendenze AirPlay.

## Rete e diagnostica

La scoperta usa mDNS/DNS-SD (UDP 5353); il backend usa TCP 7000–7002 e UDP 6000–6002. L'ascolto esiste soltanto mentre il ricevitore è attivo. Non aprire queste porte sul router e non esporre il ricevitore a Internet.

Se il nome non compare, controllare stessa LAN, rete ospiti, isolamento dei client, VLAN, VPN e filtri multicast. Il nome in **Duplica schermo** viene da DNS-SD; digitare l'indirizzo IP nel browser non lo sostituisce. Un router può permettere Internet a entrambi i dispositivi e impedire comunque loro di comunicare.

Windows deve consentire il programma `tools/airplay/uxplay.exe` sulla rete usata. Lo script facoltativo `scripts/Configure-AirPlayFirewall.ps1`, da PowerShell amministratore, aggiunge regole per quel solo eseguibile, per le porte indicate e la sottorete locale. Il promajor predefinito è **Private**; `-Profile Public` va scelto esplicitamente solo se il promajor pubblico è intenzionale per quella LAN. Lo script non cambia il promajor della rete, non disabilita il firewall e non viene eseguito automaticamente. `-WhatIf` mostra le operazioni. Spostando la build cambia il percorso del programma e occorre aggiornare le regole.

Per avviare direttamente il ricevitore:

```powershell
.\Major.exe --airplay
```

Aprire **Diagnostica AirPlay** per errori di DLL/plugin mancanti, avvio, porte occupate o processo terminato. Non interpretare **in ascolto** come prova di raggiungibilità dal dispositivo. Il successo va verificato sul dispositivo Apple e nel viewer.

L'ultimo stato viene sovrascritto in `%LOCALAPPDATA%/Major/airplay-session/status.json`, con nome del ricevitore, PID della UI, presenza del codice come booleano e orario UTC. Non contiene il codice AirPlay, token o immagini. Dopo una chiusura forzata il file può essere rimasto vecchio: verificare che il PID sia ancora attivo e che l'orario sia recente. Il registro limitato della finestra è copiato in `diagnostics.txt` nella stessa cartella, con PIN/password filtrati; può contenere indirizzi, nomi e percorsi locali. Non viene inviato a un servizio remoto.

Il codice viene mostrato anche nella barra inferiore a schermo intero. Durante l'autenticazione la chiusura di un socket temporaneo non lo cancella: resta disponibile finché il backend lo sostituisce, il dispositivo si autentica oppure si interrompe la ricezione. Se il nome resta brevemente nell'elenco dopo aver chiuso il PC, può essere un annuncio DNS-SD ancora nella cache: mantenere aperta la finestra con ricevitore attivo prima di selezionarlo.

## Limiti e prove

Questa funzione riceve schermo e audio quando inviato dal dispositivo. Non offre controllo remoto con mouse e tastiera. Non si dichiara supporto di HLS, invio del singolo video, audio multiroom, collegamento fuori LAN o contenuti protetti DRM. App protette possono mostrare video nero o silenzio. Decodifica e uscita hardware dipendono da plugin, GPU e driver disponibili; la selezione effettiva deve risultare dal runtime, non dall'annuncio delle capacità.

La matrice di esecuzione reale, inclusa la prova concordata su **iPad di 9ª generazione / iPadOS 26.4**, è in [AIRPLAY_TESTS.md](AIRPLAY_TESTS.md). Un dispositivo dichiarato disponibile non è ancora un dispositivo collaudato. I percorsi Android restano separati e richiedono le rispettive verifiche di regressione.
