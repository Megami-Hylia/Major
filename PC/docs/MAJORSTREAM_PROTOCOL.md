# MajorStream 1

Questa specifica descrive esclusivamente il nuovo protocollo della companion app. Non è il protocollo scrcpy o Vysor.

## Trasporto e autenticazione

TCP, porta predefinita **49977**, TLS 1.2/1.3. Il certificato self-signed del PC è persistente e non è accettato genericamente da Android. Ogni intero multibyte è big-endian. Limiti verificati prima di allocare memoria.

Codice di prima associazione: 32 caratteri Base32 RFC4648 senza padding (`A-Z2-7`). Sono accettati spazi e trattini come separatori. I primi 16 caratteri sono Base32 dei primi 10 byte di SHA-256 del certificato DER, gli altri 16 un segreto casuale da 10 byte. Valido 120 secondi, singolo uso. Il pin viene verificato durante l'handshake TLS, prima di inviare qualsiasi credenziale. Il resume usa il digest SHA-256 completo del certificato associato.

Prima richiesta e risposta: `uint32 length` seguito da `length` byte di JSON UTF-8; massimo 16384 byte. Richiesta:

```json
{"version":1,"kind":"pair","secret":"<ultimi16delcodice>","token":"","device":"<nome telefono>"}
```

Oppure `kind=resume`, `secret` vuoto, token precedentemente ricevuto. Risposta positiva:

```json
{"ok":true,"pcName":"<nome PC>","token":"<token opaco base64url>","error":null}
```

Risposta negativa: `ok=false`, `error` comprensibile. Non eseguire il contenuto dei nomi o degli errori. Il token è casuale a 256 bit e non va scritto nei log. Dopo un'associazione effettuata dalla schermata di collegamento il telefono può chiudere il socket senza trasmettere video. Per condividere apre una nuova connessione autenticata con `resume` dopo il consenso MediaProjection.

## Messaggi video

Ogni messaggio: `uint32 length` (include il byte tipo), `uint8 type`, payload. Limite totale 8 MiB; lunghezza minima 1. Non esistono pacchetti video prima di AUTH riuscita.

| Tipo | Payload |
| --- | --- |
| 1 CONFIG | `int32 width`, `int32 height`, `int32 fps`, esattamente 12 byte |
| 2 VIDEO | `int64 presentationTimeUs`, `int32 flags`, access unit H.264 Annex B |
| 3 STOP | Vuoto; termina la sessione |
| 4 PING/PONG | Vuoto; il PC risponde con lo stesso tipo |

Width/height 128..4096, massimo 8,5 milioni di pixel, fps 1..120. L'app propone un sottoinsieme di qualità supportato. Flags: bit 0 keyframe, bit 1 configurazione codec; gli altri riservati. Ogni CONFIG inizializza nuovamente il decoder; seguono SPS/PPS con start code Annex B, poi un IDR. Non viene inviato un MP4 o un MPEG-TS. I timestamp sono monotoni del codec in microsecondi, non orari di sistema.

Frame H.264 a 8 bit, SDR, YUV420; colorimetria BT.601 limited richiesta al codec Android e conversione coerente nel decoder. Nessun audio o canale input. Un cambio di risoluzione/rotazione conserva la connessione e invia una nuova CONFIG; un ricollegamento ripete autenticazione, CONFIG, parametri codec e IDR.

Timeout: handshake/autenticazione 10 s, assenza di messaggi 15 s. Il telefono invia heartbeat in assenza di frame. Le scritture sono limitate nel tempo tramite chiusura del socket da watchdog. Le code restano limitate; una connessione troppo lenta viene interrotta invece di accumulare memoria senza limiti.

## Interruzione e riconnessione

Stop chiude immediatamente la cattura sul telefono e il socket; l'invio del messaggio STOP è best-effort e non deve ritardare la liberazione delle risorse. Il ricevitore rilascia il decoder e toglie il vecchio frame dalla UI alla fine della stream. Può accettare una nuova connessione finché la finestra è aperta.

Dopo perdita di rete l'app può tentare una riconnessione limitata soltanto se la stessa MediaProjection è ancora valida. Non riutilizza il consenso dopo `onStop`, process death o chiusura della sessione. In tali casi richiede nuovamente Condividi schermo e il consenso di Android.
