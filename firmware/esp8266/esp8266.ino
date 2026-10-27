#include <ESP8266WiFi.h>
#include <SoftwareSerial.h>
#include <EEPROM.h>
#include <ESP8266mDNS.h>
#include <WiFiUdp.h>
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
const char* FIRMWARE_VERSION = "2.4";
const unsigned long AUTH_TIMEOUT_MS = 2000;  // unauthenticated sockets cannot hold the slot
const unsigned long IDLE_TIMEOUT_MS = 5000;  // client keepalive is every 250 ms
unsigned long connectedAt = 0;
unsigned long lastRx = 0;
unsigned long maxRxGap = 0;  // longest silence from the PC since the last DIAG (ms)
unsigned long stallRxGap = 0;  // same, since the last GAP? (the client asks after each stall)
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

// UDP fast path (optional). TCP delivers in order, so one lost Wi-Fi packet delays every report
// behind it and TCP's retransmit backoff keeps delaying them after the link recovers. UDP
// datagrams carry the complete keyboard state plus cumulative mouse counters, so each one is
// idempotent: a lost datagram is repaired by the next, and stale ones are dropped by sequence.
//   state: B1 sid(4) seq(2) mods keys(6) buttons x(2) y(2) wheel(2)   little endian, 21 bytes
//   ping:  B2 sid(4) seq(2) payload(4)                                echoed unchanged, 11 bytes
WiFiUDP udp;
const uint16_t UDP_PORT = 7778;
const uint8_t UDP_STATE = 0xB1, UDP_PING = 0xB2;
const size_t UDP_STATE_LEN = 21, UDP_PING_LEN = 11;
const int MAX_MOUSE_REPORTS_PER_PACKET = 16;  // a catch-up after a gap; the remainder is dropped
bool udpActive = false;
uint32_t udpSid = 0;
bool udpSeqValid = false;
uint16_t udpLastSeq = 0;
uint8_t udpKeys[7];  // last forwarded modifier + six keys
bool udpKeysSent = false;
unsigned long udpLastKeysForward = 0;
uint8_t udpButtons = 0;
int16_t udpCumX = 0, udpCumY = 0, udpCumW = 0;
unsigned long udpPackets = 0, udpLost = 0, udpStale = 0, udpBad = 0;

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
  udpActive = false;
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
#ifdef WIFI_FORCE_11G
  // Some routers deliver ESP8266 802.11n traffic unreliably; 11g is often steadier.
  WiFi.setPhyMode(WIFI_PHY_MODE_11G);
#endif
  wifiDisconnectHandler = WiFi.onStationModeDisconnected([](const WiFiEventStationModeDisconnected& e) {
    wifiDisconnects++;
    lastWifiDisconnectReason = e.reason;
  });
  WiFi.hostname("wifi-keyboard-bridge");
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  while (WiFi.status() != WL_CONNECTED) delay(400);
  server.begin();
  server.setNoDelay(true);
  udp.begin(UDP_PORT);
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
    "DIAG,fw=%s,up=%lu,rssi=%d,ch=%d,phy=%d,heap=%u,frag=%u,sessions=%lu,authfail=%lu,authtimeout=%lu,"
    "idletimeout=%lu,rxgap=%lu,wdrelease=%lu,badframe=%lu,overflow=%lu,picotimeout=%lu,wifidrop=%lu,"
    "wifireason=%d,udp=%lu,udplost=%lu,udpstale=%lu,udpbad=%lu,lastend=%s,pico=%s,reset=%s\n",
    FIRMWARE_VERSION, millis() / 1000, WiFi.RSSI(), WiFi.channel(), (int)WiFi.getPhyMode(), (unsigned)ESP.getFreeHeap(), (unsigned)ESP.getHeapFragmentation(),
    sessions, authFailures, authTimeouts, idleTimeouts, maxRxGap, watchdogReleases, invalidFrames, lineOverflows,
    picoProbeTimeouts, wifiDisconnects, lastWifiDisconnectReason, udpPackets, udpLost, udpStale, udpBad,
    lastSessionEnd, picoCounters, reset);
  if (n >= (int)sizeof(buf)) n = sizeof(buf) - 1;
  buf[n - 1] = '\n';
  client.write((const uint8_t*)buf, n);
  maxRxGap = 0;
}
uint16_t readLe16(const uint8_t* p) { return (uint16_t)(p[0] | (p[1] << 8)); }
uint32_t readLe32(const uint8_t* p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }

void forwardUdpState(const uint8_t* d) {
  // d: mods, keys[6], buttons, x(2), y(2), wheel(2)
  unsigned long now = millis();
  // Resend the keyboard state only on change or as the Pico's keepalive, not for every mouse packet.
  if (!udpKeysSent || memcmp(udpKeys, d, 7) != 0 || now - udpLastKeysForward >= 250) {
    memcpy(udpKeys, d, 7);
    udpKeysSent = true;
    udpLastKeysForward = now;
    char buf[40];
    int n = snprintf(buf, sizeof(buf), "S,%u,%u,%u,%u,%u,%u,%u\n", d[0], d[1], d[2], d[3], d[4], d[5], d[6]);
    Serial.write((const uint8_t*)buf, n);
    serialFrames++;
    serialBytes += n;
  }
  uint8_t buttons = d[7] & 7;
  int16_t x = (int16_t)readLe16(d + 8), y = (int16_t)readLe16(d + 10), w = (int16_t)readLe16(d + 12);
  int dx = (int16_t)(x - udpCumX), dy = (int16_t)(y - udpCumY), dw = (int16_t)(w - udpCumW);
  udpCumX = x; udpCumY = y; udpCumW = w;
  if (buttons == udpButtons && dx == 0 && dy == 0 && dw == 0) return;
  udpButtons = buttons;
  int reports = 0;
  do {
    int sx = constrain(dx, -127, 127), sy = constrain(dy, -127, 127), sw = constrain(dw, -127, 127);
    char buf[32];
    int n = snprintf(buf, sizeof(buf), "M,%u,%d,%d,%d\n", buttons, sx, sy, sw);
    Serial.write((const uint8_t*)buf, n);
    serialFrames++;
    serialBytes += n;
    dx -= sx; dy -= sy; dw -= sw;
  } while ((dx != 0 || dy != 0 || dw != 0) && ++reports < MAX_MOUSE_REPORTS_PER_PACKET);
}

void handleUdp() {
  // Bounded so a flood cannot starve the TCP side.
  for (int budget = 8; budget > 0; budget--) {
    int len = udp.parsePacket();
    if (len <= 0) return;
    uint8_t buf[32];
    if (len > (int)sizeof(buf)) { udpBad++; continue; }
    udp.read(buf, len);
    // Only the authenticated TCP peer may drive input: same address and the session id issued to it.
    if (!udpActive || udp.remoteIP() != client.remoteIP() || len < 7 || readLe32(buf + 1) != udpSid) { udpBad++; continue; }
    if (buf[0] == UDP_PING && len == (int)UDP_PING_LEN) {
      udp.beginPacket(udp.remoteIP(), udp.remotePort());
      udp.write(buf, len);
      udp.endPacket();
      continue;
    }
    if (buf[0] != UDP_STATE || len != (int)UDP_STATE_LEN) { udpBad++; continue; }
    uint16_t seq = readLe16(buf + 5);
    if (udpSeqValid) {
      int16_t diff = (int16_t)(seq - udpLastSeq);
      if (diff <= 0) { udpStale++; continue; }
      udpLost += diff - 1;
    }
    udpSeqValid = true;
    udpLastSeq = seq;
    udpPackets++;
    lastFrame = millis();  // feeds the same watchdog as TCP frames
    forwardUdpState(buf + 7);
  }
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
  if (authorized && udpActive) handleUdp();
  if (client.available()) {
    // The client sends at least every 250 ms, so a long gap means PC -> NodeMCU stalled.
    unsigned long gap = millis() - lastRx;
    if (authorized && gap > maxRxGap) maxRxGap = gap;
    if (authorized && gap > stallRxGap) stallRxGap = gap;
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
      } else if (line == "UDP?") {
        // Offer the UDP fast path: the client sends datagrams to this port carrying this session id.
        udpSid = ESP.random();
        udpActive = true;
        udpSeqValid = false;
        udpKeysSent = false;
        udpButtons = 0;
        udpCumX = udpCumY = udpCumW = 0;  // the client's cumulative counters start at zero too
        char buf[40];
        int n = snprintf(buf, sizeof(buf), "UDP,%u,%lu\n", UDP_PORT, (unsigned long)udpSid);
        client.write((const uint8_t*)buf, n);
      } else if (line == "UACK?") {
        // Lets the client confirm that its datagrams arrive, and fall back to TCP if not.
        char buf[56];
        int n = snprintf(buf, sizeof(buf), "UACK,%lu,%lu,%lu\n", udpPackets, udpLost, udpStale);
        client.write((const uint8_t*)buf, n);
      } else if (line == "GAP?") {
        // Lets the client tell a PC -> NodeMCU stall from a delayed reply.
        char buf[24];
        int n = snprintf(buf, sizeof(buf), "GAP,%lu\n", stallRxGap);
        client.write((const uint8_t*)buf, n);
        stallRxGap = 0;
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
