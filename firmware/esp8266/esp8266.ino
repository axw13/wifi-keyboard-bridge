#include <ESP8266WiFi.h>
#include <SoftwareSerial.h>
#include "secrets.h"  // Create from secrets.example.h; never commit credentials

// Diagnostic return uses isolated GPIO14/D5, not USB-UART GPIO3/RX.
SoftwareSerial picoDiagnostics(14, -1);  // RX only, 38400 baud
WiFiServer server(7777);
WiFiClient client;
String line;
bool authorized = false;
unsigned long lastFrame = 0;
unsigned long serialFrames = 0;
unsigned long serialBytes = 0;
String picoLine;
bool picoProbePending = false;
unsigned int picoProbeId = 0;
unsigned long picoProbeStart = 0;
unsigned long picoProbeTimeouts = 0;

void readPicoReplies() {
  // Bound processing so telemetry cannot starve keyboard/mouse forwarding.
  int budget = 64;
  while (budget-- > 0 && picoDiagnostics.available()) {
    char c = (char)picoDiagnostics.read();
    if (c == '\r') continue;
    if (c == '\n') {
      if (picoProbePending && picoLine == String("A,") + picoProbeId) {
        unsigned long us = micros() - picoProbeStart;
        if (authorized && client.connected()) {
          client.print("PICO,");
          client.print(picoProbeId);
          client.print(",");
          client.println(us);
        }
        picoProbePending = false;
      }
      picoLine = "";
    } else if (picoLine.length() < 24) picoLine += c;
    else picoLine = "";
  }
  if (picoProbePending && micros() - picoProbeStart > 500000UL) {
    picoProbePending = false;
    picoProbeTimeouts++;
  }
}


void releaseKeys() {
  Serial.println("S,0,0,0,0,0,0,0");
  Serial.println("M,0,0,0,0");
  Serial.flush();
}
void resetSession() {
  releaseKeys();
  if (client) client.stop();
  authorized = false;
  picoProbePending = false;
  picoLine = "";
  line = "";
}
void setup() {
  Serial.begin(115200);  // GPIO1 TX → Pico GP1 RX; must match Pico UART
  picoDiagnostics.begin(38400);  // GPIO14/D5 RX ← Pico GP4 TX (diagnostics only)
  WiFi.mode(WIFI_STA);
  WiFi.hostname("wifi-keyboard-bridge");
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  while (WiFi.status() != WL_CONNECTED) delay(400);
  server.begin();
  server.setNoDelay(true);
}
void loop() {
  readPicoReplies();
  if (!client || !client.connected()) {
    if (authorized) resetSession();
    WiFiClient candidate = server.available();
    if (candidate) {
      client = candidate;
      client.setNoDelay(true);
      authorized = false;
      line = "";
    }
    return;
  }
  while (client.available()) {
    char c = (char)client.read();
    if (c == '\r') continue;
    if (c == '\n') {
      if (!authorized) {
        if (line == String("AUTH ") + SHARED_TOKEN) {
          authorized = true;
          client.println("OK");
          lastFrame = millis();
        } else {
          client.println("ERR");
          client.stop();
        }
      } else if (line == "STAT") {
        client.print("STAT,");
        client.print(WiFi.RSSI());
        client.print(",");
        client.print(ESP.getFreeHeap());
        client.print(",");
        client.print(serialFrames);
        client.print(",");
        client.println(serialBytes);
      } else if (line.startsWith("PICO?,")) {
        unsigned long id = line.substring(6).toInt();
        if (line.length() <= 11 && id <= 65535 && line == String("PICO?,") + id && !picoProbePending) {
          picoProbeId = (unsigned int)id;
          Serial.print("D,");
          Serial.println(picoProbeId);
          picoProbeStart = micros();
          picoProbePending = true;
        }
      } else if (line == "PING") {
        client.println("PONG");
      } else if (line.length() > 0 && line.length() < 64 && (line.startsWith("S,") || line.startsWith("M,"))) {
        bool valid = true;
        for (unsigned int i = 0; i < line.length(); i++)
          if (!(line[i] == ',' || (line[i] >= '0' && line[i] <= '9') || (line[0] == 'M' && line[i] == '-') || (i == 0 && (line[i] == 'S' || line[i] == 'M')))) valid = false;
        if (valid) {
          Serial.println(line);
          serialFrames++;
          serialBytes += line.length() + 2;
          lastFrame = millis();
        }
      }
      line = "";
    } else if (line.length() < 127) line += c;
    else { resetSession(); return; }
  }
  if (authorized && millis() - lastFrame > 1500) {
    releaseKeys();
    lastFrame = millis();
  }
}
