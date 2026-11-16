// =====================================================================
//  IT-EN Ambilight V.01 – Firmware v5.1 (Bild halten für den Ruhezustand)
//  ESP32-S3, 74HCT125, WS2812B (bis 300 LEDs), Adalight-Protokoll
//
//  v5.1: Neuer Befehl !H = das zuletzt empfangene Bild stehen lassen, bis wieder ein Bild kommt
//        (kein Ausblenden nach 5 s). Das Programm schickt ihn, bevor der PC schlafen geht, wenn für den
//        Ruhezustand eine Szene eingestellt ist. !H0 hebt das Halten auf. Die Meldungen enthalten neu h=0/1.
//        Hinweis: Das funktioniert nur, wenn der USB-Anschluss im Ruhezustand weiter Strom liefert.
//  v5.0: Das Gerät meldet Tasterdrücke an den PC und nimmt Befehle an.
//        Befehle (Textzeile, beginnt mit '!', endet mit Zeilenende), nur zwischen zwei Bildern:
//          !T    Zustand melden        !OT  Ein/Aus umschalten   !O1 / !O0  Ein / Aus
//          !BN   nächste Dimmstufe     !B0 !B1 !B2  Dimmstufe 100 % / 60 % / 25 %
//        Meldungen (Textzeile, beginnt mit '#'):
//          #ST on=1 bi=0 br=255 t=41.5 mw=6400 n=112 up=1234    (Antwort auf Befehle)
//          #EV S on=0 ...   Taster kurz        #EV L bi=1 ...   Taster lang
//        t = Chip-Temperatur in Grad, mw = geschätzte LED-Leistung in Milliwatt, up = Sekunden seit Start.
//  v4.0: LED-Anzahl ist nicht mehr fest. Die Firmware übernimmt die Anzahl
//        aus dem Bild, das der PC schickt (1 bis MAX_LEDS), und merkt sie sich.
//        Alle LEDs dahinter bleiben aus. Auf ein einzelnes '?' antwortet sie
//        mit ihrer Kennung (für die automatische Gerätesuche).
//  v3.1: USB-Empfangspuffer 4096 statt 256 Byte. Ein Bild sind 342 Byte;
//        mit nur 256 Byte gingen ab ca. Byte 250 (= LED ~83) Daten verloren
//        -> Flackern ab LED ~80. Start-Anzeige jetzt 4x blau:
//        1 = LEDs, 2 = Puffer gesetzt, 3 = USB gestartet, 4 = Einstellungen.
//  v3.0: zurück zu FastLED (lief im Oktober fehlerfrei mit HyperHDR).
//        Der Start-Hänger kam NICHT von FastLED, sondern von
//        Serial.setRxBufferSize() -> das ist hier entfernt.
//        Zusätzlich aus den Tests übernommen:
//         - Start-Anzeige (blau, blau, blau, dann grün = bereit)
//         - LEDs werden direkt nach jedem VOLLSTÄNDIGEN Bild geschrieben
//         - nicht genutzte LEDs bleiben dauerhaft AUS
//
//  Arduino IDE: Board "ESP32S3 Dev Module", USB CDC On Boot = Enabled
//  Bibliothek:  FastLED (Bibliotheksverwalter)
//  HyperHDR:    Adalight, 2000000 Baud, AWA aus, LED-Anzahl beliebig (max. 300)
//
//  Taster (GPIO14):  kurz = EIN (grün) / AUS (rot)
//                    lang = Helligkeit 100% -> 60% -> 25% (weiss)
// =====================================================================
#include <FastLED.h>
#include <Preferences.h>

#define MAX_LEDS     300         // grösste unterstützte LED-Anzahl
#define DEFAULT_LEDS 112         // Anzahl, bis der PC zum ersten Mal ein Bild schickt
#define FW_ID        "IT-EN Ambilight v5.1;max=300"
#define LED_PIN      15
#define BUTTON_PIN   14

#define MAX_MA       3000
#define MAX_LEVEL    230
#define FALL_STEP    40
#define TIMEOUT_MS   5000
#define LONG_PRESS   800
#define FLASH_MS     300

CRGB leds[MAX_LEDS];             // 0..ledCount-1 Ambilight, Rest immer schwarz
uint8_t rxBuf[MAX_LEDS * 3];
uint8_t target[MAX_LEDS * 3];
uint16_t ledCount = DEFAULT_LEDS; // aktuell genutzte LEDs (kommt vom PC)
uint16_t frameCount = 0;          // LED-Anzahl des Bildes, das gerade empfangen wird
bool newFrame = false;
bool hold = false;                // true = letztes Bild stehen lassen, bis ein neues kommt (Befehl !H)
uint32_t lastData = 0;

const uint8_t BRIGHT[] = {255, 153, 64};
uint8_t brightIdx = 0;
bool enabled = true;
Preferences prefs;

// ---------- Adalight-Parser (Doppelpuffer) ----------
enum State { S_A, S_D, S_A2, S_HI, S_LO, S_CHK, S_DATA, S_CMD };
State state = S_A;
uint8_t hi, lo;
uint16_t idx;
char cmd[12];
uint8_t cmdLen = 0;

void saveSettings();

// ---------- Meldungen an den PC ----------
// Nur senden, wenn im Sendepuffer Platz ist: liest kein Programm mit, darf das Gerät nicht hängen bleiben.
void report(const char* kind) {
  if (Serial.availableForWrite() < 100) return;
  uint32_t mw = (uint32_t)calculate_unscaled_power_mW(leds, ledCount) * FastLED.getBrightness() / 255;
  if (!enabled) mw = 0;
  if (mw > 5UL * MAX_MA) mw = 5UL * MAX_MA;            // mehr lässt die Strombegrenzung nicht zu
  Serial.printf("#%s on=%d bi=%d br=%d t=%.1f mw=%lu n=%u up=%lu h=%d\n",
                kind, enabled ? 1 : 0, brightIdx, BRIGHT[brightIdx], temperatureRead(),
                (unsigned long)mw, ledCount, (unsigned long)(millis() / 1000), hold ? 1 : 0);
}

// ---------- Befehle vom PC ----------
void runCommand() {
  cmd[cmdLen] = 0;
  if (cmdLen == 0) return;
  if (cmd[0] == 'O') {                                  // Ein / Aus
    if (cmd[1] == 'T') enabled = !enabled;
    else if (cmd[1] == '1') enabled = true;
    else if (cmd[1] == '0') enabled = false;
    saveSettings();
  } else if (cmd[0] == 'B') {                           // Dimmstufe
    if (cmd[1] == 'N') brightIdx = (brightIdx + 1) % sizeof(BRIGHT);
    else if (cmd[1] >= '0' && cmd[1] < '0' + (int)sizeof(BRIGHT)) brightIdx = cmd[1] - '0';
    FastLED.setBrightness(BRIGHT[brightIdx]);
    saveSettings();
  } else if (cmd[0] == 'H') {                           // Bild halten (wird nicht gespeichert)
    hold = (cmd[1] != '0');
  }
  report("ST");                                         // jeder Befehl (auch !T) wird mit dem Zustand beantwortet
}

void handleSerial() {
  while (Serial.available()) {
    uint8_t c = Serial.read();
    switch (state) {
      case S_A:
        if (c == '?') Serial.println(FW_ID);          // Kennung für die Gerätesuche
        if (c == '!') { cmdLen = 0; state = S_CMD; break; }
        state = (c == 'A') ? S_D : S_A;
        break;
      case S_CMD:                                     // Befehlszeile bis zum Zeilenende sammeln
        if (c == '\n' || c == '\r') { runCommand(); state = S_A; }
        else if (cmdLen < sizeof(cmd) - 1) cmd[cmdLen++] = (char)c;
        else state = S_A;                             // zu lang -> verwerfen
        break;
      case S_D:   state = (c == 'd') ? S_A2 : (c == 'A' ? S_D : S_A); break;
      case S_A2:  state = (c == 'a') ? S_HI : (c == 'A' ? S_D : S_A); break;
      case S_HI:  hi = c; state = S_LO; break;
      case S_LO:  lo = c; state = S_CHK; break;
      case S_CHK:
        frameCount = (((uint16_t)hi << 8) | lo) + 1;
        if (c == (hi ^ lo ^ 0x55) && frameCount >= 1 && frameCount <= MAX_LEDS) { idx = 0; state = S_DATA; }
        else state = S_A;
        break;
      case S_DATA:
        rxBuf[idx++] = c;
        if (idx >= frameCount * 3) {
          memcpy(target, rxBuf, frameCount * 3);
          if (frameCount != ledCount) {               // neue LED-Anzahl übernehmen und merken
            ledCount = frameCount;
            prefs.putUShort("n", ledCount);
          }
          state = S_A; lastData = millis(); newFrame = true;
          hold = false;                               // ein neues Bild beendet das Halten
        }
        break;
    }
  }
}

// ---------- Anzeige ----------
void showFrame() {                 // Rest-LEDs immer schwarz halten
  for (int i = ledCount; i < MAX_LEDS; i++) leds[i] = CRGB::Black;
  FastLED.show();
}

void mark(int n) {
  fill_solid(leds, ledCount, CRGB::Black);
  for (int i = 0; i < n; i++) leds[i] = CRGB(0, 0, 60);
  showFrame();
  delay(400);
}

void flash(CRGB color) {
  fill_solid(leds, ledCount, CRGB::Black);
  for (int i = 0; i < ledCount; i += 4) leds[i] = color;
  showFrame();
  uint32_t t = millis();
  while (millis() - t < FLASH_MS) handleSerial();
}

// ---------- Taster ----------
void saveSettings() {
  prefs.putBool("on", enabled);
  prefs.putUChar("br", brightIdx);
}

void handleButton() {
  static bool stable = HIGH, lastRead = HIGH, longDone = false;
  static uint32_t lastChange = 0, pressedAt = 0;
  uint32_t now = millis();
  bool r = digitalRead(BUTTON_PIN);

  if (r != lastRead) { lastRead = r; lastChange = now; }
  if (now - lastChange > 30 && r != stable) {
    stable = r;
    if (stable == LOW) { pressedAt = now; longDone = false; }
    else if (!longDone) {
      enabled = !enabled;
      saveSettings();
      flash(enabled ? CRGB(0, 120, 0) : CRGB(120, 0, 0));
      report("EV S");
    }
  }
  if (stable == LOW && !longDone && now - pressedAt > LONG_PRESS) {
    longDone = true;
    brightIdx = (brightIdx + 1) % sizeof(BRIGHT);
    FastLED.setBrightness(BRIGHT[brightIdx]);
    saveSettings();
    flash(CRGB(150, 150, 150));
    report("EV L");
  }
}

void setup() {
  pinMode(BUTTON_PIN, INPUT_PULLUP);

  FastLED.addLeds<WS2812B, LED_PIN, GRB>(leds, MAX_LEDS);
  FastLED.setMaxPowerInVoltsAndMilliamps(5, MAX_MA);
  FastLED.setDither(0);
  FastLED.setBrightness(255);
  mark(1);                                   // LED-Treiber läuft

  Serial.setRxBufferSize(4096);              // VOR begin(): ganzes Bild passt rein
  mark(2);                                   // Puffer gesetzt

  Serial.begin(115200);                      // USB: Baudrate egal
  mark(3);                                   // USB ok

  prefs.begin("ambilight", false);
  enabled   = prefs.getBool("on", true);
  brightIdx = prefs.getUChar("br", 0) % sizeof(BRIGHT);
  ledCount  = constrain(prefs.getUShort("n", DEFAULT_LEDS), 1, MAX_LEDS);
  FastLED.setBrightness(BRIGHT[brightIdx]);
  mark(4);                                   // Einstellungen ok

  fill_solid(leds, ledCount, CRGB(0, 80, 0)); // alles grün = bereit
  showFrame();
  delay(500);
  fill_solid(leds, MAX_LEDS, CRGB::Black);
  showFrame();
  memset(target, 0, sizeof(target));

  Serial.println(enabled ? "IT-EN Ambilight v5.1 bereit (EIN)"
                         : "IT-EN Ambilight v5.1 bereit (AUS - Taster druecken)");
}

void loop() {
  handleSerial();
  handleButton();

  uint32_t now = millis();
  bool active = enabled && (hold || now - lastData < TIMEOUT_MS);

  if (active) {
    if (!newFrame) return;                   // auf nächstes Bild warten
    newFrame = false;
    for (int i = 0; i < ledCount; i++) {
      uint8_t r = target[i * 3], g = target[i * 3 + 1], b = target[i * 3 + 2];
      leds[i].setRGB(min(r, (uint8_t)MAX_LEVEL), min(g, (uint8_t)MAX_LEVEL), min(b, (uint8_t)MAX_LEVEL));
    }
    showFrame();                             // direkt nach dem Empfang
  } else {
    newFrame = false;
    static uint32_t lastIdle = 0;
    if (now - lastIdle < 20) return;         // aus/Timeout: sanft ausblenden
    lastIdle = now;
    bool any = false;
    for (int i = 0; i < ledCount; i++) {
      leds[i].fadeToBlackBy(FALL_STEP);
      if (leds[i]) any = true;
    }
    static uint32_t lastClear = 0;
    if (any || now - lastClear > 1000) { lastClear = now; showFrame(); }
  }
}
