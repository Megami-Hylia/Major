# Patch Windows incorporato

Base ufficiale: [FDH2/UxPlay](https://github.com/FDH2/UxPlay/tree/15a6b87172eca38b0df4e615fbf1bba108734dde), commit `15a6b87172eca38b0df4e615fbf1bba108734dde` (sviluppo 1.74 EXPERIMENTAL). Il programma è GPL-3.0-or-later; i componenti di libreria mantengono le licenze dei rispettivi file, inclusa LGPL-2.1-or-later in `pairing.c`. Le modifiche conservano questi termini. Le licenze e le sorgenti corrispondenti sono distribuite insieme al bundle. Non applicare la patch a una versione differente senza controllare il diff.

Dalla directory dei sorgenti originali:

```powershell
git apply --check C:\percorso\Major\third-party\airplay\major-embedded.patch
git apply C:\percorso\Major\third-party\airplay\major-embedded.patch
```

La modifica collega l'opzione `-wid <uintptr decimale>` al setter di GStreamer VideoOverlay già presente in upstream, prima dell'inizializzazione delle pipeline. Il numero deve essere non nullo e rientrare in `uintptr_t`. Il wrapper verifica inoltre che l'HWND appartenga al processo desktop.

Su Windows il numero è l'handle del contenitore WPF, non il target diretto di D3D11. Il ricevitore crea una propria finestra figlia dentro quel contenitore, con thread e message pump propri. `d3d11videosink` deve poter subclassare un HWND del suo stesso processo; il tentativo iniziale di subclassare l'HWND WPF di un altro processo lasciava il video nero. La finestra figlia segue la dimensione del contenitore, ne adotta il contesto DPI quando l'API è disponibile e inoltra F11/Esc al padre. Lo stop del processo distrugge anche la finestra figlia. Questa modifica aggiunge il collegamento a `user32` in CMake e mantiene la licenza GPL del backend.

Quando viene usato `-wid`, la variabile del solo processo figlio `FILO_EVENT_NONCE` deve contenere 32 caratteri esadecimali minuscoli casuali. Ogni evento è una singola riga UTF-8 terminata da newline e immediatamente flushata:

```text
FILO_AIRPLAY<TAB>nonce<TAB>ready
FILO_AIRPLAY<TAB>nonce<TAB>pin<TAB>quattro cifre
FILO_AIRPLAY<TAB>nonce<TAB>connected
FILO_AIRPLAY<TAB>nonce<TAB>streaming
FILO_AIRPLAY<TAB>nonce<TAB>disconnected
```

Il nonce distingue i messaggi interni dai nomi arbitrari dei dispositivi registrati nei log upstream. Non è una credenziale AirPlay. La UI riceve il PIN fuori dalla diagnostica; l'immagine ASCII del PIN upstream viene soppressa in questa modalità. Anche le righe upstream che contengono il PIN vengono omesse dal wrapper. La patch non cambia il metodo di autenticazione AirPlay o le cifrature upstream.

La versione distribuita omette dall'annuncio le funzionalità che questa prima UI non gestisce, come HLS, foto e slideshow. Include inoltre controlli sugli input SETUP/Digest (deviceID, nonce, campi obbligatori e dimensioni eiv/ekey) per rifiutare richieste malformate senza dereferenziare puntatori nulli o copiare dati oltre i buffer. Questi controlli non costituiscono una revisione completa della sicurezza del progetto upstream.

`streaming` viene emesso dal primo buffer al pad di ingresso del sink D3D11, dopo la decodifica. Il contatore atomico viene riarmato ad ogni `video_renderer_start`, anche dopo la ricreazione della pipeline. Il wrapper richiede video H.264; HLS e una sessione con solo audio non sono presentati come mirroring riuscito.

All'avvio `-wid` attende `START\n` sulla pipe privata stdin, prima di inizializzare GStreamer. Il wrapper invia questo comando soltanto dopo aver assegnato il Job Object: anche i plugin scanner nati durante l'inizializzazione appartengono al job. Se il padre muore prima, EOF termina il processo senza avviare le pipeline.

Su Windows un thread legge poi la stessa pipe: `STOP\n` oppure EOF pianifica l'arresto sul main loop GLib, permettendo cleanup e ritiro degli annunci mDNS. Il fallback dopo timeout è il Job Object Windows. Senza `-wid` il comportamento CLI upstream rimane invariato.
