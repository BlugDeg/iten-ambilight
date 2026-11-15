// =====================================================================
//  IT-EN Ambilight – DIAGNOSE
//  - Meldet JEDE SEKUNDE im seriellen Monitor (115200): Laufzeit, Taster,
//    empfangene Bytes, LED-Treiber-Status
//  - Strip wechselt alle 2 s: ROT -> GRÜN -> BLAU -> AUS (gedimmt)
//  - Taster gedrückt = ganzer Strip WEISS (gedimmt)
//  Board: "ESP32S3 Dev Module", USB CDC On Boot = Enabled
//  HyperHDR dabei AUS (LED-Gerät deaktivieren)!
// =====================================================================
#include "esp32-hal-rmt.h"

#define NUM_LEDS    112
#define LED_PIN     15
#define BUTTON_PIN  14
#define LEVEL       30          // gedimmt, schont Netzteil

rmt_data_t buf[NUM_LEDS * 24];
bool rmtOk = false, lastWriteOk = false;
uint32_t rxBytes = 0, writes = 0, writeFails = 0;

void showAll(uint8_t r, uint8_t g, uint8_t b) {
  if (!rmtOk) return;
  int k = 0;
  for (int i = 0; i < NUM_LEDS; i++) {
    uint8_t grb[3] = {g, r, b};
    for (int c = 0; c < 3; c++)
      for (int bit = 7; bit >= 0; bit--) {
        bool one = grb[c] & (1 << bit);
        buf[k].level0 = 1; buf[k].duration0 = one ? 8 : 4;
        buf[k].level1 = 0; buf[k].duration1 = one ? 4 : 8;
        k++;
      }
  }
  lastWriteOk = rmtWrite(LED_PIN, buf, NUM_LEDS * 24, 100);
  writes++;
  if (!lastWriteOk) writeFails++;
}

void setup() {
  pinMode(BUTTON_PIN, INPUT_PULLUP);
  Serial.begin(115200);
  rmtOk = rmtInit(LED_PIN, RMT_TX_MODE, RMT_MEM_NUM_BLOCKS_1, 10000000);
}

void loop() {
  while (Serial.available()) { Serial.read(); rxBytes++; }

  bool pressed = digitalRead(BUTTON_PIN) == LOW;
  uint8_t phase = (millis() / 2000) % 4;
  static const char *names[] = {"ROT", "GRUEN", "BLAU", "AUS"};

  if (pressed)          showAll(LEVEL, LEVEL, LEVEL);
  else if (phase == 0)  showAll(LEVEL, 0, 0);
  else if (phase == 1)  showAll(0, LEVEL, 0);
  else if (phase == 2)  showAll(0, 0, LEVEL);
  else                  showAll(0, 0, 0);

  static uint32_t last = 0;
  if (millis() - last >= 1000) {
    last = millis();
    Serial.printf("[%4lus] Taster=%s | Strip=%s | RMT-Init=%s | Writes=%lu Fehler=%lu | RX-Bytes=%lu\n",
                  millis() / 1000, pressed ? "GEDRUECKT" : "offen",
                  pressed ? "WEISS" : names[phase],
                  rmtOk ? "OK" : "FEHLER", writes, writeFails, rxBytes);
  }
  delay(20);
}
