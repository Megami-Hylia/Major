# Impostazioni — Major 0.5.0

Aprire Impostazioni nella barra laterale, anche nella versione compatta.

- Tema: Chiaro, Scuro o Segui Windows. Si applica immediatamente a finestre, pannelli, testo e controlli. Segui Windows legge il tema delle applicazioni e ascolta le modifiche alle preferenze di Windows. Il canvas video rimane nero per le bande del rapporto d’aspetto.
- Barra laterale compatta: aggiorna anche il comando della freccia nella testata.
- Pagina iniziale: Android USB/ADB, ricezione iPhone/iPad o ricezione Android Wi-Fi. Una pagina di ricezione avvia il relativo servizio. Gli argomenti --airplay, --receive e --settings hanno precedenza sulla preferenza.
- Apri la finestra massimizzata: applicato al successivo avvio.
- Cartella screenshot e registrazioni: condivisa con il percorso ADB, conserva la cartella già scelta.
- Apri cartella diagnostica: apre i dati locali dell’app in Esplora file. I log possono contenere nomi e indirizzi locali dei dispositivi.

Le preferenze sono locali, in %LOCALAPPDATA%/Major/settings.json, scritte atomicamente. Valori di tema o pagina iniziale non riconosciuti tornano ai valori predefiniti. Errori di scrittura sono mostrati nella pagina. Non occorre riavviare Major per cambiare tema; navigare nelle impostazioni non interrompe le ricezioni. La companion Android non cambia aspetto con questa preferenza desktop.

## Verifica

Build senza errori e avvisi; test software del motore e smoke test WPF. Lo smoke test apre i ricevitori, naviga nella stessa finestra, riduce/espande la barra, entra/esce dal pieno schermo, applica entrambi i temi dalla ComboBox, rilegge la preferenza dal disco e genera screenshot WPF per controllo visivo. Dopo il test la preferenza originale viene ripristinata. Il sistema operativo non è stato riconfigurato per forzare una notifica di cambio tema. Non è stata ripetuta la prova su iPad o Android fisico per questa modifica dell’interfaccia.

Barra compatta: restano tutti e sei i comandi (USB, iOS, Android, ADB Wi-Fi, Cartella, Imp.) con le stesse coordinate verticali della barra estesa. Aggiorna dispositivi viene nascosto senza spostare i controlli. Verificato dal confronto delle coordinate effettive dei pulsanti nello smoke test WPF.
