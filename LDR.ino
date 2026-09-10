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

WebServer webServer(80);
Preferences preferences;

int rawLight = 0;
uint8_t lightPercent = 0;
bool apActive = false;
String stationSsid;
unsigned long lastSensorRead = 0;
unsigned long lastSerialPrint = 0;
uint32_t serialSequence = 0;
String sensorId;

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

  // Wi-Fi association is asynchronous; USB measurements must keep flowing
  // even when a previously configured access point is unavailable.
}

String jsonEscape(const String& input) {
  String out;
  for (unsigned int i = 0; i < input.length(); ++i) {
    const char c = input[i];
    if (c == '"' || c == '\\') out += '\\';
    if (static_cast<uint8_t>(c) < 32) out += ' ';
    else out += c;
  }
  return out;
}

String stateJson() {
  const bool wifiConnected = WiFi.status() == WL_CONNECTED;
  String response;
  response.reserve(768);
  response = "{\"ok\":true,\"light\":";
  response += lightPercent;
  response += ",\"rawLight\":";
  response += rawLight;
  response += ",\"apActive\":";
  response += (apActive ? "true" : "false");
  response += ",\"apSsid\":\"";
  response += jsonEscape(AP_SSID);
  response += "\",\"apIp\":\"";
  response += AP_IP.toString();
  response += "\",\"wifiConnected\":";
  response += (wifiConnected ? "true" : "false");
  response += ",\"wifiSsid\":\"";
  response += jsonEscape(stationSsid);
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

void handleRoot() {
  webServer.sendHeader("Cache-Control", "no-store, no-cache, must-revalidate");
  webServer.send_P(200, PSTR("text/html; charset=utf-8"), DASHBOARD_HTML);
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
  ArduinoOTA.begin();
  MDNS.addService("http", "tcp", 80);
  setupWebServer();
}

void updateSensor(unsigned long now) {
  if (now - lastSensorRead < SENSOR_INTERVAL_MS) return;
  lastSensorRead = now;
  rawLight = readLdrFiltered();
  lightPercent = rawToLightPercent(rawLight);
}

void setup() {
  Serial.begin(115200);
  char identifier[17];
  snprintf(identifier, sizeof(identifier), "%016llX", ESP.getEfuseMac());
  sensorId = identifier;
  analogReadResolution(12);
  analogSetPinAttenuation(LDR_PIN, ADC_11db);
  delay(250);

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
    Serial.print("JVDP|light=");
    Serial.println(lightPercent);
    // Separate versioned record preserves compatibility with installed v1 apps.
    Serial.print("JVDP2|light="); Serial.print(lightPercent);
    Serial.print("|raw="); Serial.print(rawLight);
    Serial.print("|id="); Serial.print(sensorId);
    Serial.print("|fw="); Serial.print(JVDP_VERSION);
    Serial.print("|seq="); Serial.print(++serialSequence);
    Serial.print("|uptime="); Serial.println(now);
  }
}
