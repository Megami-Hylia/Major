# Dipendenze, identità e requisiti

Acquisizione e verifiche: 21 ottobre 2026, Windows x64. Nessuna installazione globale. I download sono da Microsoft, Google e dai repository/release ufficiali, con URL e digest fissati negli script. Gli artefatti della tabella provengono dalla consegna desktop 0.1.0 e restano utilizzati in 0.2.0. I risultati ADB con lista vuota sottostanti sono uno snapshot iniziale, precedente al successivo avvio di un emulatore per il collaudo dell'app complementare.

La versione 0.2.0 aggiunge **Ricevi da Android**, descritto in [ANDROID_COMPANION.md](ANDROID_COMPANION.md). Quel percorso non usa ADB: riusa solo le DLL FFmpeg già presenti. Toolchain, librerie e permessi della nuova app sono documentati in [DEPENDENCIES.md Android](../../MajorAndroid/docs/DEPENDENCIES.md) e [test Android](../../MajorAndroid/docs/TESTS.md).

| Artefatto | Origine ufficiale | Digest verificato |
| --- | --- | --- |
| scrcpy-win64-v5.0.zip | [release 5.0](https://github.com/Genymobile/scrcpy/releases/tag/v5.0) e [digest pubblicato](https://github.com/Genymobile/scrcpy/blob/v5.0/doc/windows.md) | SHA-256 `44c10d9e82f20ea67227d14d37bf9fbe3603117c5736df3f514544a02ba20a73` |
| platform-tools_r37.0.1-win.zip | [Google](https://dl.google.com/android/repository/platform-tools_r37.0.1-win.zip), [ricetta upstream](https://github.com/Genymobile/scrcpy/blob/v5.0/app/deps/adb_windows.sh) | SHA-256 `45f4d63113e895ebde0c90f194099a4676b6ac653bd28d54314a9e022bbc1a99` |
| dotnet-sdk-8.0.426-win-x64.zip | [Microsoft .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0), [manifest ufficiale](https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json) | SHA-512 `10261657109f1c62126f106a9ac966739b025b5f6b4a9f88ab7c6e73cacce9aec771895daa3267b8da933a83c32ee91bf458dc6704e1ddcd9bcf0d5edd036abb` |

Il manifest Microsoft acquisito è conservato in `third-party/dotnet8-releases-acquired.json`; identifica SDK 8.0.426 e runtime 8.0.32. L'hash del ZIP verifica integrità rispetto al manifest acquisito via HTTPS, non costituisce una verifica separata della firma del rilascio. I digest dei file installati scrcpy sono in `third-party/scrcpy-files.sha256.json`; quelli dei sorgenti e le loro URL in `third-party/sources.json`.

Il bundle scrcpy include ADB, FFmpeg, SDL3, libusb, server Android e DLL. L'ADB incluso ha SHA-256 `b4a6b455702684652cccf7b46258b29e653538904359a58fd4931cf3ef286b3f` e coincide byte per byte con quello dello ZIP Google verificato. Il NOTICE originale Google è stato recuperato da tale ZIP e conservato: il bundle scrcpy non lo includeva. Le licenze, le relative attribuzioni e i sorgenti LGPL sono in `third-party/` e `THIRD_PARTY_NOTICES.md`.

Verifiche eseguite sui componenti acquisiti:

- `scrcpy --version`: 5.0, SDL 3.4.18, libavcodec/libavformat 63.1.102, libavutil 61.1.102, libusb 1.0.30. Output conservato in `scrcpy-version-observed.txt`.
- `scrcpy --help`: acquisito integralmente in `scrcpy-help-5.0.txt` per confrontare le opzioni passate dal programma.
- `adb version`: 1.0.41, build 37.0.1-15733141. Output in `adb-version-observed.txt`.
- `adb devices -l`, snapshot iniziale della modalità 0.1.0: lista vuota. Il daemon locale ADB è stato avviato automaticamente su TCP 5037. Output in `adb-devices-observed.txt`. Non descrive il collaudo successivo con emulatore e non prova l'assenza fisica di un telefono.
- SDK `dotnet --version`: 8.0.426 dopo estrazione locale e verifica SHA-512.
- Script PowerShell: parsing di entrambi senza errori; bootstrap ripetuto con cache esistente e digest file integri; test negativo con archivio alterato rifiutato prima dell'estrazione; tutti i cinque archivi sorgente allegati ricontrollati rispetto al manifest.

Per la **modalità scrcpy/ADB** servono Windows x64, Android 5.0/API 21 o successivo, debug USB abilitato e un cavo dati per il primo collegamento USB. Su Windows può servire il driver USB ufficiale del produttore. Per alcune funzioni di input su Xiaomi serve anche «Debug USB (impostazioni di sicurezza)»; [requisiti scrcpy](https://github.com/Genymobile/scrcpy/blob/v5.0/README.md#prerequisites). La tastiera SDK ha limiti di caratteri; UHID richiede supporto kernel/device e layout configurato sul telefono. Gli appunti Android richiedono Android 7 o successivo; esistono ulteriori restrizioni del produttore e delle app. Le schermate protette da `FLAG_SECURE` possono risultare nere.

La connessione Wi-Fi guidata **ADB** usa «Debug wireless» di Android 11 o successivo e richiede la stessa rete raggiungibile, indirizzo/porta di pairing, codice e indirizzo/porta di connessione distinti. [Documentazione Google ADB](https://developer.android.com/tools/adb#connect-to-a-device-over-wi-fi). Il flusso via USB `adb tcpip` è una modalità alternativa, da usare solo sulla propria rete fidata; può restare attivo fino al riavvio o `adb usb`.

Nel percorso scrcpy/ADB il mirroring è fornito dal client/server scrcpy, che trasferisce lo stream tramite ADB e gestisce gli eventi di input. Le impostazioni passate da Major, il ciclo di vita dei processi, la selezione dispositivi, i file screenshot e i flussi guidati sono codice originale Major. Non è una riimplementazione del protocollo proprietario Vysor. La prova iniziale senza dispositivo non dimostrava il funzionamento di queste funzioni su un telefono fisico; il successivo collaudo dell'app autonoma è un percorso distinto.

## Requisiti aggiuntivi della modalità Android autonoma 0.2.0

Sul telefono servono l'APK Major, Android 8.0/API 26 o successivo e un encoder H.264 utilizzabile da MediaCodec. Non serve root né Debug USB. Sono necessari consenso MediaProjection e servizio foreground con notifica; il target della build è API 36. Il PC riceve il solo video su TCP/TLS 49977 e usa `Major.Streaming` per decodificare con `avcodec-63.dll` e `avutil-61.dll`, seguite da conversione BGRA e visualizzazione WPF. Non viene distribuito un nuovo encoder/decoder eseguibile e non viene usato `ffmpeg.exe` a runtime.

TLS e conservazione delle credenziali sono forniti da Schannel/Windows DPAPI e Android Keystore. Il codice temporaneo Base32 da 32 caratteri della nuova modalità verifica il certificato e autorizza il pairing; è indipendente dalle chiavi ADB e dal codice Debug wireless. Il listener non configura regole firewall o port forwarding. Per il collegamento LAN reale può servire una regola consentita dall'utente sulla rete privata. Per protocollo, limiti dei codec e sicurezza vedere [ANDROID_COMPANION.md](ANDROID_COMPANION.md) e [API Streaming](../src/Major.Streaming/API.md).

Le stesse licenze e sorgenti FFmpeg accompagnano anche questo uso diretto delle DLL, che restano sostituibili come file separati. La pipeline di prova ha usato un `ffmpeg.exe` già installato solo per generare fixture sintetiche originali; quell'eseguibile non è una dipendenza distribuita dell'app. I risultati su Android emulato e l'assenza di collaudo su telefono fisico sono separati in [STREAM_RECEIVER_TESTS.md](STREAM_RECEIVER_TESTS.md) e nel [report Android](../../MajorAndroid/docs/TESTS.md).
