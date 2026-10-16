#include <ESP8266WiFi.h>
#include "secrets.h"  // Create from secrets.example.h; never commit credentials

WiFiServer server(7777);
WiFiClient client;
String line;
bool authorized = false;
unsigned long lastFrame = 0;

void releaseKeys() {
  Serial.println("S,0,0,0,0,0,0,0");
  Serial.flush();
}
void resetSession() {
  releaseKeys();
  if (client) client.stop();
  authorized = false;
  line = "";
}
void setup() {
  Serial.begin(115200);  // GPIO1 TX → Pico GP1 RX; must match Pico UART
  WiFi.mode(WIFI_STA);
  WiFi.hostname("wifi-keyboard-bridge");
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  while (WiFi.status() != WL_CONNECTED) delay(400);
  server.begin();
  server.setNoDelay(true);
}
void loop() {
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
      } else if (line.length() > 0 && line.length() < 64 && line.startsWith("S,")) {
        bool valid = true;
        for (unsigned int i = 0; i < line.length(); i++)
          if (!(line[i] == ',' || (line[i] >= '0' && line[i] <= '9') || (i == 0 && line[i] == 'S'))) valid = false;
        if (valid) {
          Serial.println(line);
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
