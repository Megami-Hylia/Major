# API del ricevitore MajorStream 1

Namespace `Major.Streaming`, progetto Windows x64 `net8.0-windows`, nessun pacchetto NuGet esterno.

```csharp
var receiver = new ReceiverService(toolsDirectory, credentialDirectory);
receiver.FrameReceived += frame => { /* Marshal latest frame to the WPF dispatcher. */ };
receiver.StreamStopped += () => { /* Clear the displayed image immediately. */ };
receiver.StatusChanged += message => { /* Dispatch text to UI. */ };
receiver.PairingChanged += () => { /* Read PairingCode and PairingExpiresAt. */ };
await receiver.StartAsync(49977);
await receiver.RevokeAllAsync();
await receiver.StopAsync();
await receiver.DisposeAsync();
```

`PairingCode` è già raggruppato in otto gruppi da quattro caratteri. `PairingExpiresAt` è un `DateTimeOffset`; dopo l'uso del codice diventa `MinValue`. `PcName`, `CertificateSha256`, `Port`, `IsListening` sono proprietà leggibili. `RefreshPairingCode()` rinnova il segreto, non revoca le associazioni già salvate. `RevokeAllAsync()` revoca token, ruota il codice e chiude tutte le connessioni pendenti/attive prima di completare.

`StartAsync()` apre solamente il listener. È idempotente se già acceso e rifiuta il riavvio mentre uno Stop è ancora pendente. `StopAsync()` è idempotente e restituisce lo stesso task alle chiamate concorrenti; cancella anche handshake bloccati. È possibile riavviare la stessa istanza dopo lo Stop. La porta zero è accettata per i test; `Port` restituisce quella effettiva.

Gli eventi arrivano dal thread di ricezione. I consumer devono effettuare dispatch e mantenere solo l'ultimo frame disponibile: non accumulare frame WPF in coda. `VideoFrame` contiene `Width`, `Height`, `Bgra` e `PresentationTimeUs`; il buffer BGRA è compatto con stride `Width*4` e alpha 255. Non modificarlo mentre la UI lo usa. `StreamStopped` viene emesso prima di liberare lo slot per il prossimo stream, così l'immagine di una sessione precedente non cancella un nuovo frame.

`H264Decoder(string toolsDirectory)` usa le DLL `avcodec-63.dll` e `avutil-61.dll` già distribuite con scrcpy 5.0. `Reset(width,height)` alloca un nuovo decoder; `Decode(ReadOnlySpan<byte>,long)` restituisce zero, uno o al massimo due frame e richiede H.264 Annex B completo per access unit. I pacchetti CSD SPS/PPS senza slice vengono trattenuti e anteposti al prossimo pacchetto video. La riconfigurazione scarta il vecchio stato del decoder. La conversione supporta YUV420P, YUVJ420P, NV12 e NV21 8 bit; il promajor previsto dal sender è SDR BT.601 limited, senza B-frame.

Il decoder verifica i major FFmpeg 63/61 e usa soltanto i primi campi pubblici di AVPacket/AVFrame, con offset x64 controllati contro i sorgenti FFmpeg 9.0.2 allegati. Un aggiornamento major dei binari richiede aggiornare e verificare questi offset prima di distribuire. Le librerie native restano caricate per la durata del processo, per evitare unload concorrenti mentre esistono decoder.

L'identità ricevitore è salvata in `receiver-identity.dpapi` sotto `credentialDirectory`. Contiene il PFX e gli hash SHA-256 dei token, cifrati da DPAPI CurrentUser. Schannel non accetta la chiave importata con `EphemeralKeySet` in questo ambiente: il certificato viene importato in un contenitore chiave utente Windows senza `PersistKeySet`, rimosso alla disposal dell'import. Non viene scritto un PFX in chiaro. Solo la stessa identità Windows può decrittare il file. Non copiare questa cartella nel pacchetto distribuibile.
