# Componenti di terze parti

## Ricevitore AirPlay 0.3.0

Il processo separato `tools/airplay/uxplay.exe` deriva da **UxPlay 1.74 experimental**, revisione ufficiale `15a6b87172eca38b0df4e615fbf1bba108734dde`, modificata per HWND incorporato, eventi di stato/PIN e validazione degli ingressi. UxPlay e queste modifiche restano **GPL-3.0-or-later**; non sono coperti dalla licenza MIT dell'interfaccia originale. Il sorgente corrispondente, il diff, le istruzioni e le licenze sono in `third-party/airplay`. La release stabile alternativa e le ragioni della scelta sono documentate in `docs/AIRPLAY_ASSESSMENT.md`.

Il runtime GStreamer è distribuito come DLL e plugin sostituibili separati, con le proprie dipendenze. La build FFmpeg del ricevitore AirPlay è una compilazione minimale LGPL con decoder H.264/AAC/ALAC, distinta dal bundle scrcpy immutato sotto `tools/scrcpy`. L'inventario dei file distribuiti è `third-party/airplay/runtime-files.sha256.json`; provenienza, testi di licenza e sorgenti delle dipendenze sono conservati nella stessa cartella. Non limitare la sostituzione delle DLL compatibili o il reverse engineering necessario al debug delle modifiche LGPL. Conservare l'intera cartella `third-party` nelle redistribuzioni, insieme al codice originale e alle ricette.

## Percorsi Android e runtime .NET

Major esegue scrcpy e ADB come processi separati e non ne modifica il codice o i binari. Il codice originale Major è MIT. I componenti restano soggetti alle rispettive licenze, incluse quelle qui conservate. Nessuna funzione richiede un servizio a pagamento.

| Componente incluso | Versione | Licenza / avvisi | Sorgenti e istruzioni |
| --- | --- | --- | --- |
| scrcpy client e server | 5.0 | Apache-2.0; `third-party/scrcpy-LICENSE.txt` | `third-party/sources/scrcpy-v5.0.zip`; [tag upstream](https://github.com/Genymobile/scrcpy/tree/v5.0), `doc/build.md` e `release/` |
| Android Debug Bridge e DLL Windows | platform-tools 37.0.1, ADB 1.0.41 | Avvisi completi Google/AOSP in `third-party/ADB-NOTICE.txt`, comprendenti le licenze delle dipendenze; ADB sorgente Apache-2.0 | [AOSP adb](https://android.googlesource.com/platform/packages/modules/adb/); [distribuzione platform-tools](https://developer.android.com/tools/releases/platform-tools) |
| FFmpeg libavcodec, libavformat, libavutil, swresample | 9.0.2 | LGPL-2.1-or-later per la configurazione upstream senza `--enable-gpl`; `third-party/FFmpeg-COPYING.LGPLv2.1.txt` e avvisi completi nell'archivio | `third-party/sources/ffmpeg-9.0.2.tar.xz`; ricetta `app/deps/ffmpeg.sh` nel sorgente scrcpy |
| libusb DLL | 1.0.30 | LGPL-2.1-or-later; `third-party/libusb-COPYING.txt` | `third-party/sources/libusb-1.0.30.tar.gz`; ricetta `app/deps/libusb.sh` |
| SDL3 DLL | 3.4.18 | zlib; `third-party/SDL-LICENSE.txt` | `third-party/sources/sdl-3.4.18.tar.gz`; ricetta `app/deps/sdl.sh` |
| dav1d (in FFmpeg) | 1.5.4 | BSD-2-Clause; `third-party/dav1d-COPYING.txt` | `third-party/sources/dav1d-1.5.4.tar.xz`; ricetta `app/deps/dav1d.sh` |
| .NET runtime e WPF, nella build self-contained | 8.0.32 | MIT e avvisi delle relative dipendenze; `third-party/dotnet-runtime-LICENSE.txt`, `dotnet-runtime-THIRD-PARTY-NOTICES.txt`, `dotnet-wpf-LICENSE.txt`, `dotnet-wpf-THIRD-PARTY-NOTICES.txt` | [dotnet/runtime](https://github.com/dotnet/runtime/tree/v8.0.32), [dotnet/wpf](https://github.com/dotnet/wpf/tree/v8.0.32); SDK 8.0.426 |

I sorgenti delle librerie LGPL e le ricette di compilazione sono consegnati insieme ai binari, senza modifiche. Le DLL sono mantenute come file separati e possono essere sostituite con build compatibili; non cifrare, incorporare o impedire questa sostituzione nelle redistribuzioni. Le licenze LGPL permettono il reverse engineering per il debug delle modifiche alle librerie. Conservare gli avvisi, i testi di licenza e i sorgenti accompagnatori nelle redistribuzioni. Per ricostruire il bundle scrcpy, seguire `doc/build.md` e gli script `release/` del tag 5.0; servono toolchain MinGW/Linux, Java e Android SDK per ricompilare anche il server.

Il pacchetto upstream contiene inoltre risorse grafiche originali scrcpy usate solo dal suo runtime. Nome, marchio e interfaccia Major sono originali; nessun codice, asset o binario Vysor viene distribuito. L'SDK .NET usato per compilare è solo uno strumento locale e non è parte della build.

I componenti di sistema/toolchain (fra cui zlib collegata dalla toolchain di rilascio FFmpeg) mantengono gli avvisi nei rispettivi sorgenti; la provenienza dei binari è il bundle ufficiale immutato, identificato in `third-party/scrcpy-files.sha256.json`. Non è stata ricostruita indipendentemente l'intera toolchain upstream: non viene dichiarata riproducibilità bit per bit di scrcpy.
