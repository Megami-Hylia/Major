# Logo e palette viola

Il PNG approvato si trova in `src/Major/Assets/Major.png`: viene usato nella testata dell'app e come fonte delle icone. La copia installata conserva esattamente i byte del PNG approvato, verificati tramite SHA-256. `Major.ico` contiene 16, 24, 32, 48, 64, 128 e 256 px ed è incorporato nell'eseguibile e nelle finestre. Per rigenerarlo: `scripts/Update-BrandAssets.ps1`. Lo stesso script produce il PNG Android da 512 px; la notifica Android usa una variante monocromatica semplificata.

Il lilla campionato dal logo è `#BF9BFC`, con inchiostro `#260B43` sui pulsanti principali. Accento del tema scuro `#BF9BFC`; per testo e contorni sul tema chiaro si usa `#7544B4`. Selezioni e superfici hanno toni viola coordinati. Questa palette sostituisce il precedente accento azzurro, compresa la companion Android. Il canvas video conserva il nero delle bande del formato video.

Verifiche: build Windows, estrazione dell'icona effettivamente incorporata nell'eseguibile, identità del PNG installato rispetto a quello approvato, smoke test WPF e controllo visivo dei due temi, build Android con 9 test JVM e verifica della firma APK. Non sono state ripetute prove con dispositivi fisici per questo aggiornamento grafico.
