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
  response += ",\"apActive\":";
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
  }
}
