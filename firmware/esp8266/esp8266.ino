#include <ESP8266WiFi.h>
#include <SoftwareSerial.h>
#include <EEPROM.h>
#include <ESP8266mDNS.h>
#include <WiFiUdp.h>
#include <bearssl/bearssl.h>
#include "secrets.h"  // Create from secrets.example.h; never commit credentials

// Diagnostic return uses isolated GPIO14/D5, not USB-UART GPIO3/RX.
SoftwareSerial picoDiagnostics(14, -1);  // RX only, 115200 baud
WiFiServer server(7777);
WiFiClient client;
String line;  // handshake line (plaintext phase only)
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
const char* FIRMWARE_VERSION = "3.0";
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
unsigned long badTags = 0;  // encrypted TCP frames that failed authentication
unsigned long wifiDisconnects = 0;
int lastWifiDisconnectReason = 0;
const char* lastSessionEnd = "none";
char picoCounters[64] = "none";  // latest Pico "C,..." line payload
WiFiEventHandler wifiDisconnectHandler;

// ---------------------------------------------------------------------------------------------
// Protocol v3 security (docs/protocol.md). The shared token never crosses the network:
//   client -> HELLO2 <cn>                     16 random bytes, hex
//   server -> CHAL <sn> <HMAC(token,"WKB3-srv"|cn|sn)>
//   client -> PROOF <HMAC(token,"WKB3-cli"|cn|sn)>
// Both sides then derive two 32-byte keys with HKDF-SHA256 (salt cn|sn, info "WKB3 keys") and
// every later byte is ChaCha20-Poly1305: TCP as [len(2)][ciphertext][tag(16)] with a per-direction
// frame counter as nonce, UDP as [type][seq(4)][ciphertext][tag(16)] with the sequence as nonce.
// ---------------------------------------------------------------------------------------------
const uint8_t* const TOKEN = (const uint8_t*)SHARED_TOKEN;
const size_t TOKEN_LEN = sizeof(SHARED_TOKEN) - 1;
const uint8_t CHAN_TCP = 1, CHAN_UDP = 2;
const size_t MAX_RX_PLAIN = 128;  // longest command line the client may send
uint8_t kC2S[32], kS2C[32];
uint64_t ctrRx = 0, ctrTx = 0;
uint8_t hsCn[16], hsSn[16];
uint8_t hsState = 0;  // 0 waiting for HELLO2, 1 waiting for PROOF
uint8_t rxBuf[2 + MAX_RX_PLAIN + 16];
size_t rxHave = 0;
uint8_t txBuf[2 + 512 + 16];
unsigned long authBlockedUntil = 0;  // grows with consecutive failures so guessing is slow
uint8_t consecutiveAuthFailures = 0;

struct Part { const uint8_t* p; size_t n; };

void hmacSha256(const uint8_t* key, size_t klen, const Part* parts, int n, uint8_t out[32]) {
  br_hmac_key_context kc;
  br_hmac_key_init(&kc, &br_sha256_vtable, key, klen);
  br_hmac_context hc;
  br_hmac_init(&hc, &kc, 0);
  for (int i = 0; i < n; i++) br_hmac_update(&hc, parts[i].p, parts[i].n);
  br_hmac_out(&hc, out);
}

void hkdfSha256(const uint8_t* salt, size_t sl, const uint8_t* ikm, size_t il, const uint8_t* info, size_t nl, uint8_t* out, size_t len) {
  uint8_t prk[32];
  Part e[1] = {{ikm, il}};
  hmacSha256(salt, sl, e, 1, prk);
  uint8_t t[32];
  size_t tl = 0, done = 0;
  uint8_t counter = 1;
  while (done < len) {
    Part ps[3];
    int n = 0;
    if (tl) ps[n++] = {t, tl};
    ps[n++] = {info, nl};
    ps[n++] = {&counter, 1};
    hmacSha256(prk, 32, ps, n, t);
    tl = 32;
    size_t c = len - done < 32 ? len - done : 32;
    memcpy(out + done, t, c);
    done += c;
    counter++;
  }
}

bool ctEqual(const uint8_t* a, const uint8_t* b, size_t n) {
  uint8_t d = 0;
  for (size_t i = 0; i < n; i++) d |= a[i] ^ b[i];
  return d == 0;
}

void toHex(const uint8_t* in, size_t n, char* out) {
  static const char* digits = "0123456789abcdef";
  for (size_t i = 0; i < n; i++) { out[2 * i] = digits[in[i] >> 4]; out[2 * i + 1] = digits[in[i] & 15]; }
  out[2 * n] = 0;
}

bool fromHex(const char* s, uint8_t* out, size_t n) {
  for (size_t i = 0; i < n; i++) {
    int v[2];
    for (int j = 0; j < 2; j++) {
      char c = s[2 * i + j];
      v[j] = (c >= '0' && c <= '9') ? c - '0' : (c >= 'a' && c <= 'f') ? c - 'a' + 10 : (c >= 'A' && c <= 'F') ? c - 'A' + 10 : -1;
      if (v[j] < 0) return false;
    }
    out[i] = (uint8_t)(v[0] << 4 | v[1]);
  }
  return true;
}

void aeadRun(const uint8_t* key, const uint8_t* nonce, const uint8_t* aad, size_t aadLen, uint8_t* data, size_t len, uint8_t* tag, bool encrypt) {
  br_poly1305_ctmul_run(key, nonce, data, len, aad, aadLen, tag, br_chacha20_ct_run, encrypt ? 1 : 0);
}

void makeNonce(uint8_t n[12], uint8_t chan, uint64_t ctr) {
  n[0] = chan; n[1] = n[2] = n[3] = 0;
  for (int i = 0; i < 8; i++) n[4 + i] = (uint8_t)(ctr >> (8 * i));
}

// Decrypts in place. The tag is checked after, in constant time; on failure the data must be discarded.
bool aeadOpen(const uint8_t* key, uint8_t chan, uint64_t ctr, const uint8_t* aad, size_t aadLen, uint8_t* data, size_t len, const uint8_t* tag) {
  uint8_t nonce[12], calc[16];
  makeNonce(nonce, chan, ctr);
  aeadRun(key, nonce, aad, aadLen, data, len, calc, false);
  return ctEqual(calc, tag, 16);
}

void aeadSeal(const uint8_t* key, uint8_t chan, uint64_t ctr, const uint8_t* aad, size_t aadLen, uint8_t* data, size_t len, uint8_t* tag) {
  uint8_t nonce[12];
  makeNonce(nonce, chan, ctr);
  aeadRun(key, nonce, aad, aadLen, data, len, tag, true);
}

void proof(const char* label, const uint8_t* cn, const uint8_t* sn, uint8_t out[32]) {
  Part ps[3] = {{(const uint8_t*)label, strlen(label)}, {cn, 16}, {sn, 16}};
  hmacSha256(TOKEN, TOKEN_LEN, ps, 3, out);
}

void deriveSessionKeys() {
  uint8_t salt[32], okm[64];
  memcpy(salt, hsCn, 16);
  memcpy(salt + 16, hsSn, 16);
  static const char info[] = "WKB3 keys";
  hkdfSha256(salt, 32, TOKEN, TOKEN_LEN, (const uint8_t*)info, sizeof(info) - 1, okm, 64);
  memcpy(kC2S, okm, 32);
  memcpy(kS2C, okm + 32, 32);
  memset(okm, 0, sizeof(okm));
}

// Known-answer tests for the primitives (RFC 4231, RFC 5869, RFC 8439); run by the SELFTEST command.
// Returns 0 when everything passes, otherwise a bit mask of the failing tests.
uint8_t selfTest() {
  uint8_t failed = 0;
  {  // RFC 4231 test case 1
    uint8_t key[20], mac[32], want[32];
    memset(key, 0x0b, sizeof(key));
    Part p[1] = {{(const uint8_t*)"Hi There", 8}};
    hmacSha256(key, 20, p, 1, mac);
    fromHex("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7", want, 32);
    if (!ctEqual(mac, want, 32)) failed |= 1;
  }
  {  // RFC 5869 test case 1
    uint8_t ikm[22], salt[13], info[10], okm[42], want[42];
    memset(ikm, 0x0b, sizeof(ikm));
    for (int i = 0; i < 13; i++) salt[i] = i;
    for (int i = 0; i < 10; i++) info[i] = 0xf0 + i;
    hkdfSha256(salt, 13, ikm, 22, info, 10, okm, 42);
    fromHex("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", want, 42);
    if (!ctEqual(okm, want, 42)) failed |= 2;
  }
  {  // RFC 8439 section 2.8.2
    uint8_t key[32], nonce[12] = {7, 0, 0, 0, 0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47};
    uint8_t aad[12] = {0x50, 0x51, 0x52, 0x53, 0xc0, 0xc1, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7};
    for (int i = 0; i < 32; i++) key[i] = 0x80 + i;
    static const char plain[] = "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.";
    uint8_t data[sizeof(plain) - 1], tag[16], want[16];
    memcpy(data, plain, sizeof(data));
    aeadRun(key, nonce, aad, 12, data, sizeof(data), tag, true);
    fromHex("1ae10b594f09e26a7e902ecbd0600691", want, 16);
    if (!ctEqual(tag, want, 16) || data[0] != 0xd3 || data[1] != 0x1a) failed |= 4;
    uint8_t calc[16];
    aeadRun(key, nonce, aad, 12, data, sizeof(data), calc, false);  // decrypt back
    if (memcmp(data, plain, sizeof(data)) != 0 || !ctEqual(calc, want, 16)) failed |= 8;
  }
  return failed;
}

// ---------------------------------------------------------------------------------------------
// UDP fast path. Every datagram is a complete, idempotent snapshot: a lost datagram is repaired
// by the next and a stale one is dropped by sequence number, so there is no head-of-line blocking.
//   state C1 seq(4) | enc( mods keys(6) buttons x(2) y(2) wheel(2) ) | tag(16)        35 bytes
//   ping  C2 seq(4) | enc( payload(4) ) | tag(16)        echoed as C3 with the server's own counter
// The first 5 bytes are authenticated as AAD; the sequence (shared by both packet types) is the nonce.
// ---------------------------------------------------------------------------------------------
WiFiUDP udp;
const uint16_t UDP_PORT = 7778;
const uint8_t UDP_STATE = 0xC1, UDP_PING = 0xC2, UDP_PONG = 0xC3;
const int MAX_MOUSE_REPORTS_PER_PACKET = 16;  // a catch-up after a gap; the remainder is dropped
bool udpActive = false;
bool udpSeqValid = false;
uint32_t udpLastSeq = 0;
uint32_t udpTxSeq = 0;
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

// Encrypts txBuf[2 .. 2+len) (already filled) and sends it as one frame.
void sealAndSend(size_t len) {
  txBuf[0] = len & 255;
  txBuf[1] = len >> 8;
  aeadSeal(kS2C, CHAN_TCP, ctrTx++, txBuf, 2, txBuf + 2, len, txBuf + 2 + len);
  client.write(txBuf, 2 + len + 16);
}
void sendLine(const char* s) {
  size_t n = strlen(s);
  if (n > 512) n = 512;
  memcpy(txBuf + 2, s, n);
  sealAndSend(n);
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
          char buf[40];
          snprintf(buf, sizeof(buf), "PICO,%u,%lu", picoProbeId, us);
          sendLine(buf);
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
  hsState = 0;
  rxHave = 0;
  ctrRx = ctrTx = 0;
  memset(kC2S, 0, sizeof(kC2S));  // keys exist only for the life of a session
  memset(kS2C, 0, sizeof(kS2C));
}
void failAuth(const char* reason) {
  authFailures++;
  if (consecutiveAuthFailures < 8) consecutiveAuthFailures++;
  // 0.5 s, 1 s, 2 s ... capped at 8 s: online guessing of the token becomes impractical.
  authBlockedUntil = millis() + (250UL << consecutiveAuthFailures > 8000UL ? 8000UL : 250UL << consecutiveAuthFailures);
  if (client) client.print("ERR\n");  // the only plaintext reply, and it reveals nothing
  resetSession(reason);
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
  snprintf(buf, sizeof(buf), "STAT,%d,%u,%lu,%lu", WiFi.RSSI(), (unsigned)ESP.getFreeHeap(), serialFrames, serialBytes);
  sendLine(buf);
}
void sendDiag() {
  // key=value pairs so fields can be added without breaking older clients.
  char reset[40];
  ESP.getResetReason().toCharArray(reset, sizeof(reset));
  for (char* p = reset; *p; p++) if (*p == ',') *p = ' ';
  char* buf = (char*)txBuf + 2;
  int n = snprintf(buf, 512,
    "DIAG,fw=%s,sec=3,up=%lu,rssi=%d,ch=%d,phy=%d,heap=%u,frag=%u,sessions=%lu,authfail=%lu,authtimeout=%lu,"
    "idletimeout=%lu,rxgap=%lu,wdrelease=%lu,badframe=%lu,overflow=%lu,badtag=%lu,picotimeout=%lu,wifidrop=%lu,"
    "wifireason=%d,udp=%lu,udplost=%lu,udpstale=%lu,udpbad=%lu,lastend=%s,pico=%s,reset=%s",
    FIRMWARE_VERSION, millis() / 1000, WiFi.RSSI(), WiFi.channel(), (int)WiFi.getPhyMode(), (unsigned)ESP.getFreeHeap(), (unsigned)ESP.getHeapFragmentation(),
    sessions, authFailures, authTimeouts, idleTimeouts, maxRxGap, watchdogReleases, invalidFrames, lineOverflows, badTags,
    picoProbeTimeouts, wifiDisconnects, lastWifiDisconnectReason, udpPackets, udpLost, udpStale, udpBad,
    lastSessionEnd, picoCounters, reset);
  if (n >= 512) n = 511;
  sealAndSend(n);
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
    uint8_t buf[48];
    if (len > (int)sizeof(buf) || len < 5 + 16) { udpBad++; continue; }
    udp.read(buf, len);
    // Cheap checks first; only then the cryptographic one. Only the authenticated TCP peer may drive input.
    if (!udpActive || udp.remoteIP() != client.remoteIP()) { udpBad++; continue; }
    IPAddress peer = udp.remoteIP();
    uint16_t peerPort = udp.remotePort();
    uint8_t type = buf[0];
    uint32_t seq = readLe32(buf + 1);
    size_t plen = len - 5 - 16;
    if (!((type == UDP_STATE && plen == 14) || (type == UDP_PING && plen == 4))) { udpBad++; continue; }
    if (udpSeqValid && (int32_t)(seq - udpLastSeq) <= 0) { udpStale++; continue; }  // replay or reorder
    uint8_t data[14];
    memcpy(data, buf + 5, plen);
    if (!aeadOpen(kC2S, CHAN_UDP, seq, buf, 5, data, plen, buf + 5 + plen)) { udpBad++; continue; }
    // Authentic. Only now may the replay window move (a forged huge sequence must not lock out the real client).
    if (udpSeqValid) udpLost += (uint32_t)(seq - udpLastSeq) - 1;
    udpSeqValid = true;
    udpLastSeq = seq;
    if (type == UDP_PING) {
      uint8_t reply[5 + 4 + 16];
      reply[0] = UDP_PONG;
      uint32_t txSeq = ++udpTxSeq;
      for (int i = 0; i < 4; i++) reply[1 + i] = (uint8_t)(txSeq >> (8 * i));
      memcpy(reply + 5, data, 4);
      aeadSeal(kS2C, CHAN_UDP, txSeq, reply, 5, reply + 5, 4, reply + 9);
      udp.beginPacket(peer, peerPort);
      udp.write(reply, sizeof(reply));
      udp.endPacket();
      continue;
    }
    udpPackets++;
    lastFrame = millis();  // feeds the same watchdog as TCP frames
    forwardUdpState(data);
  }
}

// One decrypted command line from the PC. Returns false if the session was ended.
bool handleLine(const String& cmd) {
  if (cmd == "STAT") {
    sendStat();
  } else if (cmd == "DIAG") {
    sendDiag();
  } else if (cmd == "UDP?") {
    // Offer the UDP fast path; datagrams are authenticated by the session keys, not by an id.
    udpActive = true;
    udpSeqValid = false;
    udpTxSeq = 0;
    udpKeysSent = false;
    udpButtons = 0;
    udpCumX = udpCumY = udpCumW = 0;  // the client's cumulative counters start at zero too
    char buf[24];
    snprintf(buf, sizeof(buf), "UDP,%u", UDP_PORT);
    sendLine(buf);
  } else if (cmd == "UACK?") {
    // Lets the client confirm that its datagrams arrive, and fall back to TCP if not.
    char buf[56];
    snprintf(buf, sizeof(buf), "UACK,%lu,%lu,%lu", udpPackets, udpLost, udpStale);
    sendLine(buf);
  } else if (cmd == "SELFTEST") {
    char buf[24];
    uint8_t failed = selfTest();
    if (failed) snprintf(buf, sizeof(buf), "SELFTEST,FAIL,%u", failed);
    else snprintf(buf, sizeof(buf), "SELFTEST,OK");
    sendLine(buf);
  } else if (cmd == "GAP?") {
    // Lets the client tell a PC -> NodeMCU stall from a delayed reply.
    char buf[24];
    snprintf(buf, sizeof(buf), "GAP,%lu", stallRxGap);
    sendLine(buf);
    stallRxGap = 0;
  } else if (cmd.startsWith("PICO?,")) {
    unsigned long id = cmd.substring(6).toInt();
    if (cmd.length() <= 11 && id <= 65535 && cmd == String("PICO?,") + id && !picoProbePending) {
      picoProbeId = (unsigned int)id;
      Serial.print("D,");
      Serial.println(picoProbeId);
      picoProbeStart = micros();
      picoProbePending = true;
    }
  } else if (cmd == "IDLE,ON" || cmd == "IDLE,OFF") {
    saveAntiIdle(cmd == "IDLE,ON");
    sendLine(antiIdleEnabled ? "IDLE,ON" : "IDLE,OFF");
  } else if (cmd == "IDLE,PAUSE") {
    remoteInputActive = true;
  } else if (cmd == "IDLE,RESUME") {
    remoteInputActive = false;
    lastIdleMove = millis();
  } else if (cmd == "IDLE?") {
    sendLine(antiIdleEnabled ? "IDLE,ON" : "IDLE,OFF");
  } else if (cmd == "PING") {
    sendLine("PONG");
  } else if (cmd.length() > 0 && cmd.length() < 64 && (cmd.startsWith("S,") || cmd.startsWith("M,"))) {
    bool valid = true;
    for (unsigned int i = 0; i < cmd.length(); i++)
      if (!(cmd[i] == ',' || (cmd[i] >= '0' && cmd[i] <= '9') || (cmd[0] == 'M' && cmd[i] == '-') || (i == 0 && (cmd[i] == 'S' || cmd[i] == 'M')))) valid = false;
    if (valid) {
      Serial.println(cmd);
      serialFrames++;
      serialBytes += cmd.length() + 2;
      lastFrame = millis();
    } else invalidFrames++;
  }
  return true;
}

// Plaintext handshake line (HELLO2 / PROOF). Returns false if the session was ended.
bool handleHandshakeLine() {
  if (hsState == 0 && line.length() == 7 + 32 && line.startsWith("HELLO2 ") && fromHex(line.c_str() + 7, hsCn, 16)) {
    for (int i = 0; i < 16; i += 4) {
      uint32_t r = ESP.random();  // hardware RNG
      memcpy(hsSn + i, &r, 4);
    }
    uint8_t p[32];
    proof("WKB3-srv", hsCn, hsSn, p);
    char out[8 + 32 + 1 + 64 + 2];
    memcpy(out, "CHAL ", 5);
    toHex(hsSn, 16, out + 5);
    out[37] = ' ';
    toHex(p, 32, out + 38);
    out[102] = '\n';
    out[103] = 0;
    client.write((const uint8_t*)out, 103);
    hsState = 1;
    return true;
  }
  if (hsState == 1 && line.length() == 6 + 64 && line.startsWith("PROOF ")) {
    uint8_t got[32], want[32];
    if (fromHex(line.c_str() + 6, got, 32)) {
      proof("WKB3-cli", hsCn, hsSn, want);
      if (ctEqual(got, want, 32)) {
        deriveSessionKeys();
        authorized = true;
        sessions++;
        consecutiveAuthFailures = 0;
        ctrRx = ctrTx = 0;
        rxHave = 0;
        lastFrame = lastRx = millis();
        sendLine("OK");  // first encrypted frame: proves to the client that we derived the same keys
        return true;
      }
    }
  }
  failAuth("auth-failed");
  return false;
}

// Datagrams that arrive while no UDP session is active are read and counted, never queued up.
void discardUnexpectedUdp() {
  for (int budget = 8; budget > 0 && udp.parsePacket() > 0; budget--) udpBad++;
}

void loop() {
  MDNS.update();
  readPicoReplies();
  tickAntiIdle();
  if (!(authorized && udpActive)) discardUnexpectedUdp();
  if (!client || !client.connected()) {
    if (authorized) resetSession("closed");
    WiFiClient candidate = server.available();
    if (candidate) {
      if ((long)(millis() - authBlockedUntil) < 0) { candidate.stop(); return; }  // back-off after failed logins
      client = candidate;
      client.setNoDelay(true);
      authorized = false;
      line = "";
      hsState = 0;
      rxHave = 0;
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
    if (!authorized) {
      char c = (char)client.read();
      if (c == '\r') continue;
      if (c == '\n') {
        bool ok = handleHandshakeLine();
        line = "";
        if (!ok) return;
      } else if (line.length() < 100) line += c;
      else { failAuth("handshake-overflow"); return; }
      continue;
    }
    // Authenticated phase: [len(2)][ciphertext][tag(16)].
    size_t want = rxHave < 2 ? 2 - rxHave : (2 + (size_t)readLe16(rxBuf) + 16) - rxHave;
    int avail = client.available();
    int n = client.read(rxBuf + rxHave, (size_t)avail < want ? avail : want);
    if (n <= 0) break;
    rxHave += n;
    if (rxHave < 2) continue;
    size_t plen = readLe16(rxBuf);
    if (plen > MAX_RX_PLAIN) { lineOverflows++; resetSession("frame-too-long"); return; }
    if (rxHave < 2 + plen + 16) continue;
    uint8_t* data = rxBuf + 2;
    if (!aeadOpen(kC2S, CHAN_TCP, ctrRx, rxBuf, 2, data, plen, data + plen)) {
      // Altered, replayed, reordered or forged frame: end the session rather than guess.
      badTags++;
      resetSession("bad-tag");
      return;
    }
    ctrRx++;
    rxHave = 0;
    data[plen] = 0;
    String cmd((char*)data);
    cmd.trim();
    handleLine(cmd);
  }
  if (authorized && millis() - lastFrame > 1500) {
    watchdogReleases++;
    releaseKeys();
    lastFrame = millis();
  }
}
