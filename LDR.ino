#include <Arduino.h>
#include <ArduinoOTA.h>
#include <ESPmDNS.h>
#include <Preferences.h>
#include <WebServer.h>
#include <WiFi.h>

#include "DashboardPage.h"
#include ".generated/JvdpBuildConfig.h"

#ifndef WIFI_STA_SSID
#define WIFI_STA_SSID ""
#endif

#ifndef WIFI_STA_PASSWORD
#define WIFI_STA_PASSWORD ""
#endif

constexpr int LDR_PIN = 0;
constexpr uint8_t SAMPLE_COUNT = 9;
constexpr unsigned long SENSOR_INTERVAL_MS = 100;
constexpr unsigned long SERIAL_INTERVAL_MS = 1000;

constexpr int LDR_RAW_DARK = 0;
constexpr int LDR_RAW_BRIGHT = 4095;

constexpr char AP_SSID[] = JVDP_AP_SSID;
constexpr char AP_PASSWORD[] = JVDP_AP_PASSWORD;
constexpr char OTA_HOSTNAME[] = "jvdp-lightsensor";
constexpr char OTA_PASSWORD[] = JVDP_OTA_PASSWORD;
const IPAddress AP_IP(192, 168, 9, 1);
const IPAddress AP_GATEWAY(192, 168, 9, 1);
const IPAddress AP_SUBNET(255, 255, 255, 0);

constexpr uint8_t MAX_ISO_BANDS = 8;
constexpr uint32_t MIN_ISO = 100;
constexpr uint32_t MAX_ISO = 12800;
constexpr uint32_t SUPPORTED_ISO_VALUES[] = {
    100, 200, 400, 800, 1600, 3200, 6400, 12800};
constexpr size_t SUPPORTED_ISO_COUNT =
    sizeof(SUPPORTED_ISO_VALUES) / sizeof(SUPPORTED_ISO_VALUES[0]);

WebServer webServer(80);
Preferences preferences;

uint8_t isoBandCount = 4;
uint8_t isoUpperBounds[MAX_ISO_BANDS] = {25, 50, 75, 100, 100, 100, 100, 100};
uint32_t isoValues[MAX_ISO_BANDS] = {3200, 1600, 800, 400, 400, 400, 400, 400};

int rawLight = 0;
uint8_t lightPercent = 0;
bool apActive = false;
String stationSsid;
unsigned long lastSensorRead = 0;
unsigned long lastSerialPrint = 0;

int readLdrFiltered() {
  int samples[SAMPLE_COUNT];
  analogRead(LDR_PIN);
  delay(2);

  for (uint8_t i = 0; i < SAMPLE_COUNT; ++i) {
    samples[i] = analogRead(LDR_PIN);
    delay(2);
  }

  for (uint8_t i = 0; i < SAMPLE_COUNT - 1; ++i) {
    for (uint8_t j = i + 1; j < SAMPLE_COUNT; ++j) {
      if (samples[j] < samples[i]) {
        const int temporary = samples[i];
        samples[i] = samples[j];
        samples[j] = temporary;
      }
    }
  }

  long sum = 0;
  for (uint8_t i = 2; i < SAMPLE_COUNT - 2; ++i) {
    sum += samples[i];
  }
  return sum / (SAMPLE_COUNT - 4);
}

uint8_t rawToLightPercent(int value) {
  if (LDR_RAW_DARK == LDR_RAW_BRIGHT) return 0;
  const long percentage = map(value, LDR_RAW_DARK, LDR_RAW_BRIGHT, 0, 100);
  return static_cast<uint8_t>(constrain(percentage, 0L, 100L));
}

uint32_t currentIso() {
  for (uint8_t i = 0; i < isoBandCount; ++i) {
    if (lightPercent <= isoUpperBounds[i]) return isoValues[i];
  }
  return isoValues[isoBandCount - 1];
}

bool isoIsSupported(uint32_t value) {
  for (size_t i = 0; i < SUPPORTED_ISO_COUNT; ++i) {
    if (SUPPORTED_ISO_VALUES[i] == value) return true;
  }
  return false;
}

bool mappingIsValid(uint8_t count, const uint8_t* bounds, const uint32_t* values) {
  if (count < 1 || count > MAX_ISO_BANDS) return false;

  int previous = -1;
  for (uint8_t i = 0; i < count; ++i) {
    if (bounds[i] <= previous || bounds[i] > 100) return false;
    if (!isoIsSupported(values[i])) return false;
    previous = bounds[i];
  }
  return bounds[count - 1] == 100;
}

void saveMapping() {
  if (!preferences.begin("ldr-map", false)) return;
  preferences.putUChar("count", isoBandCount);
  preferences.putBytes("bounds", isoUpperBounds, isoBandCount * sizeof(uint8_t));
  preferences.putBytes("isos", isoValues, isoBandCount * sizeof(uint32_t));
  preferences.end();
}

void loadMapping() {
  if (!preferences.begin("ldr-map", true)) return;

  const uint8_t storedCount = preferences.getUChar("count", 0);
  if (storedCount < 1 || storedCount > MAX_ISO_BANDS ||
      !preferences.isKey("bounds") || !preferences.isKey("isos")) {
    preferences.end();
    return;
  }

  uint8_t storedBounds[MAX_ISO_BANDS] = {};
  uint32_t storedValues[MAX_ISO_BANDS] = {};
  const size_t boundsLength = preferences.getBytesLength("bounds");
  const size_t valuesLength = preferences.getBytesLength("isos");
  bool valid = boundsLength == storedCount * sizeof(uint8_t) &&
               valuesLength == storedCount * sizeof(uint32_t);

  if (valid) {
    preferences.getBytes("bounds", storedBounds, boundsLength);
    preferences.getBytes("isos", storedValues, valuesLength);
    valid = mappingIsValid(storedCount, storedBounds, storedValues);
  }
  preferences.end();

  if (!valid) return;
  isoBandCount = storedCount;
  memcpy(isoUpperBounds, storedBounds, storedCount * sizeof(uint8_t));
  memcpy(isoValues, storedValues, storedCount * sizeof(uint32_t));
}

void loadStationCredentials(String& ssid, String& password) {
  if (preferences.begin("wifi", true)) {
    ssid = preferences.getString("ssid", "");
    password = preferences.getString("password", "");
    preferences.end();
  }

  if (!ssid.isEmpty() || strlen(WIFI_STA_SSID) == 0) return;

  ssid = WIFI_STA_SSID;
  password = WIFI_STA_PASSWORD;
  if (!preferences.begin("wifi", false)) return;
  preferences.putString("ssid", ssid);
  preferences.putString("password", password);
  preferences.end();
}

void connectToLocalWifi() {
  String password;
  loadStationCredentials(stationSsid, password);
  if (stationSsid.isEmpty()) return;

  WiFi.setHostname(OTA_HOSTNAME);
  WiFi.setAutoReconnect(true);
  WiFi.begin(stationSsid.c_str(), password.c_str());

  const unsigned long startedAt = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - startedAt < 15000) {
    delay(250);
  }
}

String stateJson() {
  const bool wifiConnected = WiFi.status() == WL_CONNECTED;
  String response;
  response.reserve(768);
  response = "{\"ok\":true,\"light\":";
  response += lightPercent;
  response += ",\"rawLight\":";
  response += rawLight;
  response += ",\"currentIso\":";
  response += currentIso();
  response += ",\"bandCount\":";
  response += isoBandCount;
  response += ",\"bands\":[";

  for (uint8_t i = 0; i < isoBandCount; ++i) {
    if (i > 0) response += ',';
    response += "{\"max\":";
    response += isoUpperBounds[i];
    response += ",\"iso\":";
    response += isoValues[i];
    response += '}';
  }

  response += "],\"apActive\":";
  response += (apActive ? "true" : "false");
  response += ",\"apSsid\":\"";
  response += AP_SSID;
  response += "\",\"apIp\":\"";
  response += AP_IP.toString();
  response += "\",\"wifiConnected\":";
  response += (wifiConnected ? "true" : "false");
  response += ",\"wifiSsid\":\"";
  response += stationSsid;
  response += "\",\"wifiIp\":\"";
  response += (wifiConnected ? WiFi.localIP().toString() : "");
  response += "\",\"firmwareVersion\":\"";
  response += JVDP_VERSION;
  response += "\",\"otaReady\":true,\"otaHostname\":\"";
  response += OTA_HOSTNAME;
  response += ".local";
  response += "\",\"uptimeMs\":";
  response += millis();
  response += '}';
  return response;
}

void sendJson(int status, const String& body) {
  webServer.sendHeader("Cache-Control", "no-store, no-cache, must-revalidate");
  webServer.sendHeader("X-Content-Type-Options", "nosniff");
  webServer.send(status, "application/json; charset=utf-8", body);
}

bool parseUnsignedList(const String& source, uint8_t count, uint32_t minimum,
                       uint32_t maximum, uint32_t* output) {
  int start = 0;
  for (uint8_t i = 0; i < count; ++i) {
    const int separator = source.indexOf(',', start);
    const bool lastValue = i == count - 1;
    if ((!lastValue && separator < 0) || (lastValue && separator >= 0)) return false;

    const int end = lastValue ? source.length() : separator;
    if (end <= start) return false;
    const String token = source.substring(start, end);
    for (size_t character = 0; character < token.length(); ++character) {
      if (!isDigit(token[character])) return false;
    }

    const unsigned long parsed = strtoul(token.c_str(), nullptr, 10);
    if (parsed < minimum || parsed > maximum) return false;
    output[i] = static_cast<uint32_t>(parsed);
    start = end + 1;
  }
  return true;
}

void handleRoot() {
  webServer.sendHeader("Cache-Control", "no-store, no-cache, must-revalidate");
  webServer.send_P(200, PSTR("text/html; charset=utf-8"), DASHBOARD_HTML);
}

void handleAction() {
  if (!webServer.hasArg("bandCount") || !webServer.hasArg("bounds") ||
      !webServer.hasArg("isos")) {
    sendJson(400, "{\"ok\":false,\"error\":\"Incomplete ISO mapping\"}");
    return;
  }

  const int requestedCount = webServer.arg("bandCount").toInt();
  if (requestedCount < 1 || requestedCount > MAX_ISO_BANDS) {
    sendJson(400, "{\"ok\":false,\"error\":\"Invalid range count\"}");
    return;
  }

  uint32_t parsedBounds[MAX_ISO_BANDS] = {};
  uint32_t parsedValues[MAX_ISO_BANDS] = {};
  if (!parseUnsignedList(webServer.arg("bounds"), requestedCount, 0, 100, parsedBounds) ||
      !parseUnsignedList(webServer.arg("isos"), requestedCount, MIN_ISO, MAX_ISO,
                         parsedValues)) {
    sendJson(400, "{\"ok\":false,\"error\":\"Invalid ISO mapping\"}");
    return;
  }

  uint8_t newBounds[MAX_ISO_BANDS] = {};
  for (int i = 0; i < requestedCount; ++i) {
    newBounds[i] = static_cast<uint8_t>(parsedBounds[i]);
  }
  if (!mappingIsValid(requestedCount, newBounds, parsedValues)) {
    sendJson(400, "{\"ok\":false,\"error\":\"Ranges must cover 0 to 100\"}");
    return;
  }

  isoBandCount = static_cast<uint8_t>(requestedCount);
  memcpy(isoUpperBounds, newBounds, isoBandCount * sizeof(uint8_t));
  memcpy(isoValues, parsedValues, isoBandCount * sizeof(uint32_t));
  saveMapping();

  String response = "{\"ok\":true,\"message\":\"ISO mapping saved\",\"state\":";
  response += stateJson();
  response += '}';
  sendJson(200, response);
}

void setupWebServer() {
  webServer.on("/", HTTP_GET, handleRoot);
  webServer.on("/ping", HTTP_GET, []() {
    webServer.sendHeader("Cache-Control", "no-store");
    webServer.send(200, "text/plain; charset=utf-8", "ok");
  });
  webServer.on("/favicon.ico", HTTP_GET, []() {
    webServer.send(204, "image/x-icon", "");
  });
  webServer.on("/api/health", HTTP_GET, []() {
    sendJson(200, "{\"ok\":true}");
  });
  webServer.on("/api/state", HTTP_GET, []() {
    sendJson(200, stateJson());
  });
  webServer.on("/api/action", HTTP_POST, handleAction);
  webServer.onNotFound([]() {
    sendJson(404, "{\"ok\":false,\"error\":\"Not found\"}");
  });
  webServer.begin();
}

void setupNetwork() {
  WiFi.persistent(false);
  WiFi.mode(WIFI_AP_STA);
  WiFi.softAPConfig(AP_IP, AP_GATEWAY, AP_SUBNET);
  apActive = WiFi.softAP(AP_SSID, AP_PASSWORD, 1, false, 4);
  connectToLocalWifi();

  ArduinoOTA.setHostname(OTA_HOSTNAME);
  ArduinoOTA.setPassword(OTA_PASSWORD);
  ArduinoOTA.onStart([]() {
    Serial.println("OTA update started");
  });
  ArduinoOTA.onEnd([]() {
    Serial.println("\nOTA update finished");
  });
  ArduinoOTA.onProgress([](unsigned int progress, unsigned int total) {
    if (total > 0) Serial.printf("OTA progress: %u%%\r", progress / (total / 100));
  });
  ArduinoOTA.onError([](ota_error_t error) {
    Serial.printf("OTA error: %u\n", error);
  });
  ArduinoOTA.begin();
  MDNS.addService("http", "tcp", 80);
  setupWebServer();

  Serial.println();
  Serial.print("Dashboard fallback network: ");
  Serial.println(AP_SSID);
  Serial.print("Fallback dashboard: http://");
  Serial.println(AP_IP);
  if (WiFi.status() == WL_CONNECTED) {
    Serial.print("Local Wi-Fi: ");
    Serial.println(stationSsid);
    Serial.print("LAN dashboard: http://");
    Serial.println(WiFi.localIP());
  } else if (!stationSsid.isEmpty()) {
    Serial.print("Local Wi-Fi unavailable: ");
    Serial.println(stationSsid);
  }
  Serial.print("OTA ready: ");
  Serial.print(OTA_HOSTNAME);
  Serial.println(".local");
}

void updateSensor(unsigned long now) {
  if (now - lastSensorRead < SENSOR_INTERVAL_MS) return;
  lastSensorRead = now;
  rawLight = readLdrFiltered();
  lightPercent = rawToLightPercent(rawLight);
}

void setup() {
  Serial.begin(115200);
  analogReadResolution(12);
  analogSetPinAttenuation(LDR_PIN, ADC_11db);
  delay(250);

  loadMapping();
  rawLight = readLdrFiltered();
  lightPercent = rawToLightPercent(rawLight);
  setupNetwork();
}

void loop() {
  const unsigned long now = millis();
  updateSensor(now);
  webServer.handleClient();
  ArduinoOTA.handle();

  if (now - lastSerialPrint >= SERIAL_INTERVAL_MS) {
    lastSerialPrint = now;
    Serial.print("Raw: ");
    Serial.print(rawLight);
    Serial.print(" | Light: ");
    Serial.print(lightPercent);
    Serial.print(" | ISO: ");
    Serial.println(currentIso());
    Serial.print("JVDP|light=");
    Serial.print(lightPercent);
    Serial.print("|iso=");
    Serial.println(currentIso());
  }
}
