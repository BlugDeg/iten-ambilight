// =====================================================================
//  IT-EN Ambilight V.01 – Firmware v3.1 (FastLED + grosser USB-Empfangspuffer)
//  ESP32-S3, 74HCT125, 112x WS2812B, Adalight-Protokoll
//
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
//         - LEDs 113..STRIP_TOTAL bleiben dauerhaft AUS
//
//  Arduino IDE: Board "ESP32S3 Dev Module", USB CDC On Boot = Enabled
//  Bibliothek:  FastLED (Bibliotheksverwalter)
//  HyperHDR:    Adalight, 2000000 Baud, AWA aus, 112 LEDs
//
//  Taster (GPIO14):  kurz = EIN (grün) / AUS (rot)
//                    lang = Helligkeit 100% -> 60% -> 25% (weiss)
// =====================================================================
#include <FastLED.h>
#include <Preferences.h>

#define NUM_LEDS     112         // genutzte LEDs (HyperHDR)
#define STRIP_TOTAL  300         // max. LEDs am Strip: alles über 112 bleibt AUS
#define LED_PIN      15
#define BUTTON_PIN   14

#define MAX_MA       3000
#define MAX_LEVEL    230
#define FALL_STEP    40
#define TIMEOUT_MS   5000
#define LONG_PRESS   800
#define FLASH_MS     300

CRGB leds[STRIP_TOTAL];          // 0..111 Ambilight, Rest immer schwarz
uint8_t rxBuf[NUM_LEDS * 3];
uint8_t target[NUM_LEDS * 3];
bool newFrame = false;
uint32_t lastData = 0;

const uint8_t BRIGHT[] = {255, 153, 64};
uint8_t brightIdx = 0;
bool enabled = true;
Preferences prefs;

// ---------- Adalight-Parser (Doppelpuffer) ----------
enum State { S_A, S_D, S_A2, S_HI, S_LO, S_CHK, S_DATA };
State state = S_A;
uint8_t hi, lo;
uint16_t idx;

void handleSerial() {
  while (Serial.available()) {
    uint8_t c = Serial.read();
    switch (state) {
      case S_A:   state = (c == 'A') ? S_D : S_A; break;
      case S_D:   state = (c == 'd') ? S_A2 : (c == 'A' ? S_D : S_A); break;
      case S_A2:  state = (c == 'a') ? S_HI : (c == 'A' ? S_D : S_A); break;
      case S_HI:  hi = c; state = S_LO; break;
      case S_LO:  lo = c; state = S_CHK; break;
      case S_CHK:
        if (c == (hi ^ lo ^ 0x55) && (((hi << 8) | lo) + 1) == NUM_LEDS) { idx = 0; state = S_DATA; }
        else state = S_A;
        break;
      case S_DATA:
        rxBuf[idx++] = c;
        if (idx >= NUM_LEDS * 3) {
          memcpy(target, rxBuf, sizeof(target));
          state = S_A; lastData = millis(); newFrame = true;
        }
        break;
    }
  }
}

// ---------- Anzeige ----------
void showFrame() {                 // Rest-LEDs immer schwarz halten
  for (int i = NUM_LEDS; i < STRIP_TOTAL; i++) leds[i] = CRGB::Black;
  FastLED.show();
}

void mark(int n) {
  fill_solid(leds, NUM_LEDS, CRGB::Black);
  for (int i = 0; i < n; i++) leds[i] = CRGB(0, 0, 60);
  showFrame();
  delay(400);
}

void flash(CRGB color) {
  fill_solid(leds, NUM_LEDS, CRGB::Black);
  for (int i = 0; i < NUM_LEDS; i += 4) leds[i] = color;
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
    }
  }
  if (stable == LOW && !longDone && now - pressedAt > LONG_PRESS) {
    longDone = true;
    brightIdx = (brightIdx + 1) % sizeof(BRIGHT);
    FastLED.setBrightness(BRIGHT[brightIdx]);
    saveSettings();
    flash(CRGB(150, 150, 150));
  }
}

void setup() {
  pinMode(BUTTON_PIN, INPUT_PULLUP);

  FastLED.addLeds<WS2812B, LED_PIN, GRB>(leds, STRIP_TOTAL);
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
  FastLED.setBrightness(BRIGHT[brightIdx]);
  mark(4);                                   // Einstellungen ok

  fill_solid(leds, NUM_LEDS, CRGB(0, 80, 0)); // alles grün = bereit
  showFrame();
  delay(500);
  fill_solid(leds, STRIP_TOTAL, CRGB::Black);
  showFrame();
  memset(target, 0, sizeof(target));

  Serial.println(enabled ? "IT-EN Ambilight v3.1 (FastLED) bereit (EIN)"
                         : "IT-EN Ambilight v3.1 (FastLED) bereit (AUS - Taster druecken)");
}

void loop() {
  handleSerial();
  handleButton();

  uint32_t now = millis();
  bool active = enabled && (now - lastData < TIMEOUT_MS);

  if (active) {
    if (!newFrame) return;                   // auf nächstes Bild warten
    newFrame = false;
    for (int i = 0; i < NUM_LEDS; i++) {
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
    for (int i = 0; i < NUM_LEDS; i++) {
      leds[i].fadeToBlackBy(FALL_STEP);
      if (leds[i]) any = true;
    }
    static uint32_t lastClear = 0;
    if (any || now - lastClear > 1000) { lastClear = now; showFrame(); }
  }
}
