# Ricevitore AirPlay Windows: sorgenti e compilazione

Il runtime è un eseguibile Windows x64 nativo separato: **UxPlay 1.74 EXPERIMENTAL**, base upstream `15a6b87172eca38b0df4e615fbf1bba108734dde`, con patch originali Major. Non è una release stabile 1.74 ufficiale. È stata scelta questa base perché contiene il responder mDNS interno e non richiede Bonjour, servizi o installazioni con privilegi amministrativi. La release ufficiale stabile di confronto è 1.73.7 e non pubblica binari Windows ufficiali.

## Artefatti e provenienza

- `sources/UxPlay-15a6b87172eca38b0df4e615fbf1bba108734dde.zip`: archivio upstream originale, SHA-256 `437622bc66f81c88266e7b01ba93dcd9f8637b0bb9f3be1488556eaaae0853e8`.
- `major-embedded.patch`: modifiche rispetto a quella base, con note della modifica nei file sorgente.
- `sources/Major-UxPlay-modified.zip`: sorgenti corrispondenti del ricevitore effettivamente compilato.
- `runtime-files.sha256.json`: file distribuiti in `tools/airplay`, con dimensione e SHA-256.
- `runtime-packages.json`: provenienza e metadati ufficiali dei pacchetti delle DLL selezionate.
- `corresponding-sources.json` e `sources/*.src.tar.zst`: sorgenti e ricette MSYS2 delle librerie effettivamente incluse, con tarball upstream e patch di distribuzione. Le firme detached `.sig` sono conservate e verificate; i risultati sono nel manifest.
- FFmpeg 9.0.2 è compilato localmente in configurazione minima LGPL-2.1-or-later, senza GPL/nonfree o librerie esterne, con decoder H.264, AAC e ALAC. I sorgenti completi sono già in `../sources/ffmpeg-9.0.2.tar.xz`; la ricetta è `../../scripts/airplay/build_ffmpeg.sh`. Non è il FFmpeg GPL completo del pacchetto MSYS2.

Versioni della compilazione: GCC 16.2.0, CMake 4.4.4, Ninja 1.13.2, GStreamer 1.28.8, GLib 2.90.1, OpenSSL 3.6.5, libplist 2.7.0, FFmpeg 9.0.2. Il responder mDNS e le librerie PlayFair/llhttp/SRP sono quelli nel tree UxPlay fissato. `NO_MARCH_NATIVE=ON` impedisce di richiedere le istruzioni specifiche del PC usato per compilare.

## Verificare o ricompilare

L'utente finale non deve installare MSYS2, GStreamer, OpenSSL o Bonjour. Il bundle contiene solamente l'eseguibile, i plugin selezionati e la chiusura delle DLL importate; compilatore, header e toolchain rimangono nel workspace di compilazione.

Da PowerShell nella cartella Major:

```powershell
# Verifica solamente tutti i file runtime consegnati, senza rete o installazioni.
.\scripts\bootstrap-airplay.ps1

# Ricompilazione opzionale: servono Python 3.14+, Git e una base MSYS2.
# La base MSYS2 può essere estratta localmente senza installazione globale.
.\scripts\bootstrap-airplay.ps1 -Rebuild -MsysRoot C:\percorso\msys64 -WorkDirectory C:\percorso\work\airplay-rebuild
```

La ricompilazione acquisisce gli archivi di pacchetto fissati in `build-packages.sha256.json`, verifica ogni SHA-256 ed estrae la toolchain solo nella cartella di lavoro scelta. Non esegue `pacman -S` o installazioni globali. Riparte dall'archivio UxPlay originale, applica la patch, compila il FFmpeg minimo e UxPlay, raccoglie gli import PE e rigenera il manifest runtime. Gli archivi binari di build sono strumenti locali, non fanno parte del runtime distribuito.

Gli script di compilazione sono nella cartella `scripts/airplay`; non è richiesta una libreria Python esterna. Per costruire le singole librerie MSYS2 dai sorgenti, estrarre il relativo `.src.tar.zst`, seguire il suo `PKGBUILD` e conservare le patch incluse. I sorgenti sono forniti insieme ai binari; le DLL restano sostituibili con ricompilazioni ABI compatibili e non sono fuse dentro l'eseguibile WPF.

## Layout e ambiente del solo processo figlio

```text
tools/airplay/uxplay.exe
tools/airplay/bin/*.dll
tools/airplay/bin/gst-inspect-1.0.exe
tools/airplay/bin/gst-launch-1.0.exe
tools/airplay/lib/gstreamer-1.0/*.dll
tools/airplay/libexec/gstreamer-1.0/gst-plugin-scanner.exe
```

Il wrapper mette `bin` davanti al PATH del solo figlio, fissa `GST_PLUGIN_PATH_1_0` e `GST_PLUGIN_SYSTEM_PATH_1_0` alla directory plugin del bundle e `GST_PLUGIN_SCANNER_1_0` allo scanner incluso. Il registro GStreamer è una cache per utente separata dal runtime, che può essere di sola lettura. Nessun plugin globale dell'host viene richiesto. `-nh` rimuove il suffisso `@hostname` dal nome del ricevitore; HLS e HEVC sono disattivati dalle impostazioni di default e dai bit delle capacità della modalità incorporata.

TCP 7000–7002, UDP 6000–6002 e mDNS UDP 5353 devono essere raggiungibili sulla rete locale. Il servizio AirPlay RTSP annuncia TCP 7001; TCP 7000 serve al flusso mirror. Le eventuali regole firewall sono una scelta esplicita dell'utente tramite lo script separato e non vengono applicate da questo bootstrap.

## Evidenze locali e limiti

Il log `gst-inspect-observed.txt` verifica quindici elementi, fra cui `d3d11videosink`, `d3d11h264dec`, `wasapisink`, `avdec_h264`, `avdec_aac` e `avdec_alac`, tutti con uscita 0. `uxplay-help-observed.txt` conferma avvio e opzioni del binario. I log di compilazione sono conservati: il ricevitore si compila correttamente; FFmpeg upstream produce alcuni warning del compilatore, visibili nel log integrale. Questi controlli non provano da soli frame, audio o compatibilità con un dispositivo Apple specifico: le prove reali e l'eventuale esito sono documentati separatamente nei test dell'app.
