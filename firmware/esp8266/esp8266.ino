#include <ESP8266WiFi.h>
#include <SoftwareSerial.h>
#include <EEPROM.h>
#include <ESP8266mDNS.h>
#include "secrets.h"  // Create from secrets.example.h; never commit credentials

// Diagnostic return uses isolated GPIO14/D5, not USB-UART GPIO3/RX.
SoftwareSerial picoDiagnostics(14, -1);  // RX only, 115200 baud
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
bool antiIdleEnabled = false;
bool remoteInputActive = false;
unsigned long lastIdleMove = 0;
bool idleMoveRight = true;

// Diagnostics: reported to Windows by the DIAG command and written to its log.
const char* FIRMWARE_VERSION = "2.2";
const unsigned long AUTH_TIMEOUT_MS = 2000;  // unauthenticated sockets cannot hold the slot
const unsigned long IDLE_TIMEOUT_MS = 5000;  // client keepalive is every 250 ms
unsigned long connectedAt = 0;
unsigned long lastRx = 0;
unsigned long maxRxGap = 0;  // longest silence from the PC since the last DIAG (ms)
unsigned long sessions = 0;
unsigned long authFailures = 0;
unsigned long authTimeouts = 0;
unsigned long idleTimeouts = 0;
unsigned long watchdogReleases = 0;
unsigned long invalidFrames = 0;
unsigned long lineOverflows = 0;
unsigned long wifiDisconnects = 0;
int lastWifiDisconnectReason = 0;
const char* lastSessionEnd = "none";
char picoCounters[64] = "none";  // latest Pico "C,..." line payload
WiFiEventHandler wifiDisconnectHandler;

void saveAntiIdle(bool enabled) {
  if (antiIdleEnabled == enabled) return;
  antiIdleEnabled = enabled;
  EEPROM.write(0, enabled ? 0xA5 : 0x00);
  EEPROM.commit();
}
void tickAntiIdle() {
  if (!antiIdleEnabled || remoteInputActive) return;
  unsigned long now = millis();
  if (now - lastIdleMove < 15000UL) return;
  Serial.println(idleMoveRight ? "M,0,4,0,0" : "M,0,-4,0,0");
  idleMoveRight = !idleMoveRight;
  lastIdleMove = now;
}


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
      else if (picoLine.startsWith("C,") && picoLine.length() < sizeof(picoCounters)) {
        // Pico counters up,frames,bad,watchdog,usb,errors; '/' keeps DIAG comma-separated.
        picoLine.substring(2).toCharArray(picoCounters, sizeof(picoCounters));
        for (char* p = picoCounters; *p; p++) if (*p == ',') *p = '/';
      }
      picoLine = "";
    } else if (picoLine.length() < 64) picoLine += c;
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
void resetSession(const char* reason) {
  releaseKeys();
  if (client) client.stop();
  lastSessionEnd = reason;
  authorized = false;
  // Anti-idle must resume when the PC goes away while inputs were REMOTE.
  remoteInputActive = false;
  picoProbePending = false;
  picoLine = "";
  line = "";
}
void setup() {
  EEPROM.begin(16);
  antiIdleEnabled = EEPROM.read(0) == 0xA5;
  Serial.begin(115200);  // GPIO1 TX → Pico GP1 RX; must match Pico UART
  picoDiagnostics.begin(115200);  // GPIO14/D5 RX ← Pico GP0 TX (diagnostics only)
  WiFi.persistent(false);
  WiFi.mode(WIFI_STA);
  // Modem sleep adds 100+ ms latency spikes; this device is mains powered.
  WiFi.setSleepMode(WIFI_NONE_SLEEP);
  WiFi.setAutoReconnect(true);
  wifiDisconnectHandler = WiFi.onStationModeDisconnected([](const WiFiEventStationModeDisconnected& e) {
    wifiDisconnects++;
    lastWifiDisconnectReason = e.reason;
  });
  WiFi.hostname("wifi-keyboard-bridge");
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  while (WiFi.status() != WL_CONNECTED) delay(400);
  server.begin();
  server.setNoDelay(true);
  if (MDNS.begin("wifi-keyboard-bridge"))
    MDNS.addService("keyboardbridge", "tcp", 7777);
}
void sendStat() {
  char buf[96];
  int n = snprintf(buf, sizeof(buf), "STAT,%d,%u,%lu,%lu\n", WiFi.RSSI(), (unsigned)ESP.getFreeHeap(), serialFrames, serialBytes);
  client.write((const uint8_t*)buf, n);
}
void sendDiag() {
  // key=value pairs so fields can be added without breaking older clients.
  char reset[40];
  ESP.getResetReason().toCharArray(reset, sizeof(reset));
  for (char* p = reset; *p; p++) if (*p == ',') *p = ' ';
  char buf[512];
  int n = snprintf(buf, sizeof(buf),
    "DIAG,fw=%s,up=%lu,rssi=%d,ch=%d,heap=%u,frag=%u,sessions=%lu,authfail=%lu,authtimeout=%lu,"
    "idletimeout=%lu,rxgap=%lu,wdrelease=%lu,badframe=%lu,overflow=%lu,picotimeout=%lu,wifidrop=%lu,"
    "wifireason=%d,lastend=%s,pico=%s,reset=%s\n",
    FIRMWARE_VERSION, millis() / 1000, WiFi.RSSI(), WiFi.channel(), (unsigned)ESP.getFreeHeap(), (unsigned)ESP.getHeapFragmentation(),
    sessions, authFailures, authTimeouts, idleTimeouts, maxRxGap, watchdogReleases, invalidFrames, lineOverflows,
    picoProbeTimeouts, wifiDisconnects, lastWifiDisconnectReason, lastSessionEnd, picoCounters, reset);
  if (n >= (int)sizeof(buf)) n = sizeof(buf) - 1;
  buf[n - 1] = '\n';
  client.write((const uint8_t*)buf, n);
  maxRxGap = 0;
}
void loop() {
  MDNS.update();
  readPicoReplies();
  tickAntiIdle();
  if (!client || !client.connected()) {
    if (authorized) resetSession("closed");
    WiFiClient candidate = server.available();
    if (candidate) {
      client = candidate;
      client.setNoDelay(true);
      authorized = false;
      line = "";
      connectedAt = lastRx = millis();
    }
    return;
  }
  if (!authorized && millis() - connectedAt > AUTH_TIMEOUT_MS) {
    authTimeouts++;
    resetSession("auth-timeout");
    return;
  }
  if (authorized && millis() - lastRx > IDLE_TIMEOUT_MS) {
    // Half-open session (PC crashed or lost Wi-Fi); free the slot for a reconnect.
    idleTimeouts++;
    resetSession("idle-timeout");
    return;
  }
  if (client.available()) {
    // The client sends at least every 250 ms, so a long gap means PC -> NodeMCU stalled.
    if (authorized && millis() - lastRx > maxRxGap) maxRxGap = millis() - lastRx;
    lastRx = millis();
  }
  while (client.available()) {
    char c = (char)client.read();
    if (c == '\r') continue;
    if (c == '\n') {
      if (!authorized) {
        if (line == String("AUTH ") + SHARED_TOKEN) {
          authorized = true;
          sessions++;
          client.println("OK");
          lastFrame = millis();
        } else {
          authFailures++;
          client.println("ERR");
          resetSession("auth-failed");
          return;
        }
      } else if (line == "STAT") {
        sendStat();
      } else if (line == "DIAG") {
        sendDiag();
      } else if (line.startsWith("PICO?,")) {
        unsigned long id = line.substring(6).toInt();
        if (line.length() <= 11 && id <= 65535 && line == String("PICO?,") + id && !picoProbePending) {
          picoProbeId = (unsigned int)id;
          Serial.print("D,");
          Serial.println(picoProbeId);
          picoProbeStart = micros();
          picoProbePending = true;
        }
      } else if (line == "IDLE,ON" || line == "IDLE,OFF") {
        saveAntiIdle(line == "IDLE,ON");
        client.println(antiIdleEnabled ? "IDLE,ON" : "IDLE,OFF");
      } else if (line == "IDLE,PAUSE") {
        remoteInputActive = true;
      } else if (line == "IDLE,RESUME") {
        remoteInputActive = false;
        lastIdleMove = millis();
      } else if (line == "IDLE?") {
        client.println(antiIdleEnabled ? "IDLE,ON" : "IDLE,OFF");
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
        } else invalidFrames++;
      }
      line = "";
    } else if (line.length() < 127) line += c;
    else { lineOverflows++; resetSession("line-overflow"); return; }
  }
  if (authorized && millis() - lastFrame > 1500) {
    watchdogReleases++;
    releaseKeys();
    lastFrame = millis();
  }
}
