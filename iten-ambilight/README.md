# IT-EN Ambilight

Eigenes Ambilight für den PC: ESP32-S3 + WS2812B, Steuerung per USB.

## Inhalt
- `src/ItenAmbilight` – Windows-Programm (C#, .NET 8, WPF)
- `firmware/ambilight_v4_0` – aktuelle Firmware fürs ESP32-S3 (FastLED, Adalight, LED-Anzahl variabel bis 300)
- `firmware/ambilight_v3_1` – ältere Firmware (fest 112 LEDs)
- `firmware/diagnose` – Testprogramm (Strip rot/grün/blau, Taster, Statusmeldungen)

## Programm bauen
1. `ItenAmbilight.sln` in Visual Studio 2022 öffnen (Workload ".NET-Desktopentwicklung").
2. Start mit F5. Beim ersten Bauen lädt Visual Studio das Paket `System.IO.Ports`.
3. HyperHDR vorher beenden – der COM-Port kann nur von einem Programm benutzt werden.

Einstellungen liegen in `%AppData%\ItenAmbilight\settings.json`.
Start ohne Fenster (Autostart): `ItenAmbilight.exe --tray`

## Stand
**Etappe 1 (v0.1):** Bildschirmaufnahme (GDI), Monitor-Auswahl, LED-Berechnung aus der
Bildschirmgrösse, Randbeschnitt, Glättung, Helligkeit, Ausgabe per Adalight, Tray-Symbol, Vorschau.

**v0.2:** Aufnahme über die Desktop Duplication API (Verkleinerung auf der Grafikkarte),
GDI nur noch als Rückfall; Vorschau zeigt das aufgenommene Bild.

**Etappe 2 (v0.3):** Farbkalibrierung (Sättigung, Gamma, Weissabgleich Rot/Grün/Blau, Testfarbe),
automatische Erkennung schwarzer Balken, HDR-Aufnahme (16-Bit-Bild von Windows, Umrechnung auf den
normalen Bereich mit einstellbarem Weisspunkt).

**Etappe 3 (v0.4):** Betriebsarten Ambilight / Effekt / feste Farbe, sechs Effekte, Regeln pro Programm
(gilt, solange das Programm im Vordergrund ist), Start mit Windows, wählbare Messauflösung,
Anzeige der Firmware-Kennung (ab Firmware v4.0).

**v0.5:** Neue Oberfläche (dunkel, Seitenleiste, Seiten Start / Effekte / Regeln / Gerät / LED-Hardware /
Ambilight / Allgemein), ausklappbare Vorschau, Monitor identifizieren, Programmsymbole in den Regeln,
Anzeige von Prozessor, Grafikkarte und Arbeitsspeicher.

**v0.6:** Kalibrier-Assistent im Vollbild (Lage mit Ball, Ecken, Weiss, Graustufen, sechs Farben,
Schwarz-Schwelle, Verzögerung), getrennter Farbabgleich für SDR und HDR, 42 Effekte in fünf Kategorien mit
eigenen Reglern pro Effekt, eigene Effekte als Lua-Skript mit Baukasten und Live-Vorschau, Firmware v5.0
(Taster-Meldungen, Chip-Temperatur, geschätzte LED-Leistung, Ein/Aus und Dimmen per Programm), Verlauf.

Offen: Musik-Effekte, andere RGB-Geräte über OpenRGB, Tag/Nacht-Profile.

## Releases und Updates
- Ein Release entsteht automatisch, sobald ein Tag wie `v0.7.0` hochgeladen wird (`.github/workflows/release.yml`).
  Es enthält `ItenAmbilight.exe` (eine Datei, läuft ohne installiertes .NET) und `firmware.zip`.
- Das Programm fragt beim Start `github.com/BlugDeg/iten-ambilight` nach dem neuesten Release. Ist es neuer,
  fragt es nach und ersetzt nach einem Ja die eigene .exe. Das Repository steht in `Updater.cs` (`Repo`).
- Die Update-Suche findet Releases nur, wenn das Repository öffentlich ist.
- Programmsymbol: `app.ico` nach `src/ItenAmbilight/` legen, dann neu bauen.

## Eigene Effekte (Lua)
Ein Skript definiert `function led(i, pos, t, p)` und gibt eine Farbe zurück:
`i` = Nummer der LED (1..n), `pos` = Lage rund um den Monitor (0..1), `t` = Zeit in Sekunden,
`p.intensity` (0..1), `p.color1`, `p.color2`, `p.color3`, `p.n`.
Befehle: `rgb(r,g,b)`, `hsv(h,s,v)`, `mix(a,b,x)`, `scale(farbe,x)`, `wave(x)`, `noise(x,t)`, `clamp(x,lo,hi)`.
Optional wird `function frame(t, dt, p)` einmal pro Bild aufgerufen. Skripte laufen in einer Sandbox
(kein Dateizugriff) und werden bei einer Endlosschleife angehalten.

## Firmware v5.0
Befehle vom PC (Textzeile zwischen zwei Bildern): `!T` Zustand, `!OT` Ein/Aus umschalten, `!O1`/`!O0`,
`!BN` nächste Dimmstufe, `!B0`..`!B2`. Meldungen ans Programm: `#ST ...` (Zustand) und `#EV S|L ...` (Taster).
Die Firmware v4.0 funktioniert weiter, liefert aber keine Meldungen.

## LED-Anzahl
Mit Firmware v4.0 ist die LED-Anzahl frei (1 bis 300). Die Firmware übernimmt die Anzahl aus den
Bildern, die das Programm schickt. Die ältere v3.1 nimmt nur genau 112 LEDs an.
