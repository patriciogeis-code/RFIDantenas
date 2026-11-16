# Zyntra RFID Lab — Antenas USB y Portales / Arcos UHF (`BRD-DC06` / `BRD-DC01` / `BRD-2204`)

Consola de ingeniería, calibración visual de intensidad RF y servidor local para toda la línea de antenas y lectores fijos UHF Broadradio / Bowei de Zyntra:

1. **Solapa 1 — `BRD-DC06` (Lector/Grabador Compacto USB HID):**
   - Dibujo interactivo del domo de radiación RF ($0\text{ a }27\text{ dBm}$) con cotas de alcance en centímetros ($8\text{ cm}$, $30\text{ cm}$, $80\text{ cm}$).
   - Monitor de etiquetas en tiempo real (Código ASCII, EPC Hex, RSSI, Frecuencia $915.25\text{ MHz}$) y grabador `Write EPC`.
2. **Solapa 2 — `BRD-DC01` (Pad de Mostrador / Caja $330\times 330\text{ mm}$ *Near-Field*):**
   - Plano interactivo de zona segura sobre el vidrio templado ($0\text{ a }25\text{ dBm}$) para calibrar cero desborde lateral en el mostrador.
3. **Solapa 3 — `Arco RFID Completo` (Lector Fijo `BRD-2204` + 4 Antenas Circulares $8\text{ dBiC}$):**
   - Esquema técnico interactivo con las 4 antenas clicables (`ANT 1..4` con Popup individual), conexión `RJ-45` al Router, barreras infrarrojas (`IN1/IN2`), salidas GPIO de LEDs/Sirena (`O1..O3`) y **Auto-Calibración Inteligente**.
4. **Auto-Actualización OTA (`AutoUpdater`):**
   - `Zyntra RFID Lab.exe` verifica automáticamente `version.json` en `patriciogeis-code/RFIDantenas` y actualiza tanto la interfaz web como el binario `.exe` en caliente.
