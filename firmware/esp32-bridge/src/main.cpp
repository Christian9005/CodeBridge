/*
 * CodeBridge Firmware v0.3.0
 * ─────────────────────────
 * Bridge firmware for ESP32 that receives commands from the
 * CodeBridge .NET SDK via Serial AND WiFi (TCP) and controls
 * hardware peripherals.
 *
 * v0.3.0: Added SPI, OneWire, Servo, NeoPixel, Tone, DHT,
 *         Ultrasonic, Motor/Stepper support.
 *
 * Protocol: Text-based commands over serial or TCP (115200 baud / port 8080)
 * Format:   CMD:PARAM1:PARAM2:...\n
 * Response: OK:DATA\n or ERR:MESSAGE\n
 *
 * Copyright (c) 2026 CodeBridge Project
 */

#include <Arduino.h>
#include <Wire.h>
#include <SPI.h>
#include <WiFi.h>
#include <ArduinoJson.h>
#include <Preferences.h>
#include <ESPmDNS.h>
#include <soc/soc_caps.h>
#include <OneWire.h>
#include <DallasTemperature.h>
#include <Adafruit_NeoPixel.h>
#include <ESP32Servo.h>
#include <Adafruit_GFX.h>
#include <Adafruit_SSD1306.h>
#include <LiquidCrystal_I2C.h>
#include <Adafruit_BME280.h>
#include <BH1750.h>
#include <Adafruit_MPU6050.h>
#include <Adafruit_Sensor.h>
#include <Adafruit_TCS34725.h>
#include <Adafruit_INA219.h>
#include <HTTPClient.h>
#include <Update.h>
#include <PubSubClient.h>

#define FIRMWARE_VERSION "0.9.1"
#define MAX_CMD_LENGTH 512
#define SERIAL_BAUD 115200
#define TCP_PORT 8080
#define MAX_TCP_CLIENTS 2

// ── WiFi Configuration ──────────────────────────────────────
char wifiSSID[64] = "";
char wifiPassword[64] = "";
bool wifiEnabled = false;
bool wifiConnected = false;

// ── TCP Server ──────────────────────────────────────────────
WiFiServer tcpServer(TCP_PORT);
WiFiClient tcpClients[MAX_TCP_CLIENTS];

// ── Command Buffers ─────────────────────────────────────────
char cmdBuffer[MAX_CMD_LENGTH];
int cmdIndex = 0;

char tcpCmdBuffers[MAX_TCP_CLIENTS][MAX_CMD_LENGTH];
int tcpCmdIndexes[MAX_TCP_CLIENTS] = {0};
bool tcpOverflow[MAX_TCP_CLIENTS] = {false};      // current line exceeded MAX_CMD_LENGTH: discard until newline
bool tcpAuthed[MAX_TCP_CLIENTS] = {false};
uint8_t tcpAuthFails[MAX_TCP_CLIENTS] = {0};
unsigned long tcpConnectedAt[MAX_TCP_CLIENTS] = {0};
bool serialOverflow = false;

// Network access is closed by default: a TCP client must send AUTH:<token> (set over USB with WTOK) before any command.
#define TCP_AUTH_TIMEOUT_MS 5000
#define TCP_MAX_AUTH_FAILS 3
char apiToken[65] = "";

// -1 = Serial, 0..MAX_TCP_CLIENTS-1 = TCP client, -2 = a command relayed over ESP-NOW (the answer is collected and sent back over the air)
int activeResponseTarget = -1;

// ── PWM Configuration ───────────────────────────────────────
struct PwmChannel {
  int pin;
  int channel;
  bool active;
};

#define MAX_PWM_CHANNELS 16
PwmChannel pwmChannels[MAX_PWM_CHANNELS];
int nextPwmChannel = 0;

// ── Preferences ─────────────────────────────────────────────
Preferences preferences;

// ── SPI Configuration ───────────────────────────────────────
int spiClockSpeed = 1000000; // 1MHz default
int spiMode = SPI_MODE0;

// ── OneWire ─────────────────────────────────────────────────
// Created dynamically per pin when needed
OneWire* owBus = nullptr;
int owPin = -1;
DallasTemperature* dallasSensors = nullptr;

// ── Servo ───────────────────────────────────────────────────
#define MAX_SERVOS 8
Servo servos[MAX_SERVOS];
int servoPins[MAX_SERVOS];
bool servoActive[MAX_SERVOS];
int servoCount = 0;

// ── NeoPixel ────────────────────────────────────────────────
Adafruit_NeoPixel* neoStrip = nullptr;
int neoPin = -1;
int neoCount = 0;

// ── Motor (H-Bridge DC motor) ────────────────────────────────
#define MAX_MOTORS 4
struct MotorDef {
  int in1;
  int in2;
  int enPin;
  int pwmChannel;
  bool active;
};
MotorDef motors[MAX_MOTORS];
int motorCount = 0;

// ── Stepper ──────────────────────────────────────────────────
#define MAX_STEPPERS 2
struct StepperDef {
  int pins[4];
  int stepsPerRev;
  bool active;
};
StepperDef steppers[MAX_STEPPERS];
int stepperCount = 0;

// ── OLED Display (SSD1306 I2C) ──────────────────────────────
Adafruit_SSD1306* oledDisplay = nullptr;
int oledWidth = 128;
int oledHeight = 64;

// ── LCD Display (I2C HD44780) ───────────────────────────────
LiquidCrystal_I2C* lcdDisplay = nullptr;
int lcdCols = 16;
int lcdRows = 2;

// ── BME280 (I2C temp/humidity/pressure) ──────────────────────
Adafruit_BME280* bme280 = nullptr;
bool bme280Ready = false;

// ── BH1750 (I2C light sensor) ────────────────────────────────
BH1750* bh1750 = nullptr;
bool bh1750Ready = false;

// ── MPU6050 (I2C IMU) ────────────────────────────────────────
Adafruit_MPU6050* mpu6050 = nullptr;
bool mpu6050Ready = false;

// ── TCS34725 (I2C color sensor) ──────────────────────────────
Adafruit_TCS34725* tcs34725 = nullptr;
bool tcs34725Ready = false;

// ── INA219 (I2C current/power sensor) ────────────────────────
Adafruit_INA219* ina219 = nullptr;
bool ina219Ready = false;

// ── GPIO Interrupts ──────────────────────────────────────────
#define MAX_INTERRUPT_PINS 8
struct InterruptEvent {
  int pin;
  int edge; // 1=RISING, 2=FALLING, 3=CHANGE
  volatile uint32_t count;
  volatile unsigned long lastTrigger;
  volatile bool triggered;
};
InterruptEvent intPins[MAX_INTERRUPT_PINS];
int intPinCount = 0;

void IRAM_ATTR isrHandler0() { intPins[0].count++; intPins[0].lastTrigger = millis(); intPins[0].triggered = true; }
void IRAM_ATTR isrHandler1() { intPins[1].count++; intPins[1].lastTrigger = millis(); intPins[1].triggered = true; }
void IRAM_ATTR isrHandler2() { intPins[2].count++; intPins[2].lastTrigger = millis(); intPins[2].triggered = true; }
void IRAM_ATTR isrHandler3() { intPins[3].count++; intPins[3].lastTrigger = millis(); intPins[3].triggered = true; }
void IRAM_ATTR isrHandler4() { intPins[4].count++; intPins[4].lastTrigger = millis(); intPins[4].triggered = true; }
void IRAM_ATTR isrHandler5() { intPins[5].count++; intPins[5].lastTrigger = millis(); intPins[5].triggered = true; }
void IRAM_ATTR isrHandler6() { intPins[6].count++; intPins[6].lastTrigger = millis(); intPins[6].triggered = true; }
void IRAM_ATTR isrHandler7() { intPins[7].count++; intPins[7].lastTrigger = millis(); intPins[7].triggered = true; }

typedef void (*IsrFunc)();
IsrFunc isrHandlers[MAX_INTERRUPT_PINS] = {
  isrHandler0, isrHandler1, isrHandler2, isrHandler3,
  isrHandler4, isrHandler5, isrHandler6, isrHandler7
};

// ── Watchdog ─────────────────────────────────────────────────
#include <esp_task_wdt.h>
bool wdtEnabled = false;

// ── MQTT ─────────────────────────────────────────────────────
WiFiClient mqttWifiClient;
PubSubClient mqttClient(mqttWifiClient);
bool mqttConnected = false;

#define MQTT_MSG_QUEUE_SIZE 32
struct MqttMsg {
  String topic;
  String payload;
};
MqttMsg mqttMsgQueue[MQTT_MSG_QUEUE_SIZE];
int mqttMsgHead = 0;
int mqttMsgTail = 0;

// Buffered acquisition channels for dashboard-friendly sample streaming.
#define MAX_SAMPLE_CHANNELS 4
#define MAX_SAMPLE_BUFFER_CAPACITY 4096
struct SampleValue {
  uint32_t sequence;
  uint32_t elapsedMicros;
  int value;
};

struct SampleChannel {
  bool active;
  int pin;
  bool analog;
  int mode; // 0=polling, 1=hardware-timer scheduler, 2=interrupt-compatible scheduler
  int backpressure; // 0=drop oldest, 1=drop newest, 2=aggregate, 3=pause
  uint32_t sampleRateHz;
  uint32_t periodMicros;
  uint32_t lastSampleMicros;
  uint32_t sequence;
  uint16_t capacity;
  uint16_t head;
  uint16_t tail;
  uint16_t count;
  uint32_t dropped;
  SampleValue* buffer;
};

SampleChannel sampleChannels[MAX_SAMPLE_CHANNELS];

void mqttCallback(char* topic, byte* payload, unsigned int length) {
  int next = (mqttMsgHead + 1) % MQTT_MSG_QUEUE_SIZE;
  if (next != mqttMsgTail) {
    mqttMsgQueue[mqttMsgHead].topic = String(topic);
    mqttMsgQueue[mqttMsgHead].payload = "";
    for (unsigned int i = 0; i < length; i++) {
      mqttMsgQueue[mqttMsgHead].payload += (char)payload[i];
    }
    mqttMsgHead = next;
  }
}

// ── OTA Status ───────────────────────────────────────────────
String otaStatus = "idle";
int otaProgress = 0;

// ── Function Declarations ───────────────────────────────────
void processCommand(const char* cmd);
void meshBegin();
void meshService();
void meshOnWifiUp();
void handlePinMode(const char* params);
void handleDigitalWrite(const char* params);
void handleDigitalRead(const char* params);
void handleAnalogRead(const char* params);
void handlePwmWrite(const char* params);
void handleI2cScan(const char* params);
void handleI2cWrite(const char* params);
void handleI2cRead(const char* params);
void handleI2cWriteReg(const char* params);
void handleI2cReadReg(const char* params);
void handlePing();
void handleInfo();
void handleVersion();
void handleReset();
void handleWifiConfig(const char* params);
void handleWifiConfigHex(const char* params);
static void applyWifiConfig(const String& ssid, const String& pass);
void handleWifiStatus();
void handleWifiScan();

// Phase 3: SPI
void handleSpiTransfer(const char* params);
void handleSpiWrite(const char* params);
void handleSpiRead(const char* params);
void handleSpiConfig(const char* params);

// Phase 3: OneWire
void handleOwScan(const char* params);
void handleOwRead(const char* params);
void handleOwWrite(const char* params);
void handleOwTemp(const char* params);

// Phase 3+: Servo
void handleServoAttach(const char* params);
void handleServoWrite(const char* params);
void handleServoRead(const char* params);
void handleServoMicroseconds(const char* params);
void handleServoDetach(const char* params);

// Phase 3+: NeoPixel
void handleNeoInit(const char* params);
void handleNeoSet(const char* params);
void handleNeoAll(const char* params);
void handleNeoShow();
void handleNeoClear();
void handleNeoBrightness(const char* params);
void handleNeoRange(const char* params);

// Phase 3+: Tone/Buzzer
void handleTone(const char* params);
void handleNoTone(const char* params);

// Phase 3+: DHT
void handleDhtInit(const char* params);
void handleDhtRead(const char* params);

// Phase 3+: Ultrasonic
void handleUltrasonicRead(const char* params);

// Phase 4: Motor / H-Bridge
void handleMotorInit(const char* params);
void handleMotorSpeed(const char* params);
void handleMotorStop(const char* params);

// Phase 4: Stepper
void handleStepperInit(const char* params);
void handleStepperStep(const char* params);

// Phase 4: OLED (SSD1306)
void handleOledInit(const char* params);
void handleOledClear();
void handleOledText(const char* params);
void handleOledPixel(const char* params);
void handleOledLine(const char* params);
void handleOledRect(const char* params);
void handleOledCircle(const char* params);
void handleOledFlush();
void handleOledBrightness(const char* params);

// Phase 4: LCD (I2C HD44780)
void handleLcdInit(const char* params);
void handleLcdClear();
void handleLcdText(const char* params);
void handleLcdBacklight(const char* params);
void handleLcdCursor(const char* params);

// Phase 5: BME280
void handleBme280Init(const char* params);
void handleBme280Read(const char* params);

// Phase 5: BH1750
void handleBh1750Init(const char* params);
void handleBh1750Read(const char* params);

// Phase 5: MPU6050
void handleMpu6050Init(const char* params);
void handleMpu6050Read(const char* params);

// Phase 6: TCS34725
void handleTcs34725Init(const char* params);
void handleTcs34725Read(const char* params);

// Phase 6: INA219
void handleIna219Init(const char* params);
void handleIna219Read(const char* params);

// Phase 7: GPIO Interrupts
void handleInterruptAttach(const char* params);
void handleInterruptDetach(const char* params);
void handleInterruptPoll();

// Phase 7: Watchdog
void handleWatchdogInit(const char* params);
void handleWatchdogFeed();
void handleWatchdogDisable();

// Phase 7: Deep Sleep
void handleDeepSleep(const char* params);
void handleDeepSleepPin(const char* params);

// Phase 7: OTA
void handleOtaBegin(const char* params);
void handleOtaStatus();

// Phase 7: MQTT
void handleMqttConnect(const char* params);
void handleMqttPublish(const char* params);
void handleMqttSubscribe(const char* params);
void handleMqttUnsubscribe(const char* params);
void handleMqttRead();
void handleMqttDisconnect();

// Phase 8: Buffered acquisition
void handleSampleConfig(const char* params);
void handleSampleRead(const char* params);
void handleSampleStop(const char* params);
void serviceSampleChannels();
void pushSample(SampleChannel& channel, uint32_t elapsedMicros, int value);
int parseSampleChannelId(const String& channelId);

void sendOK(const char* data = "");
void sendError(const char* msg);
void sendResponse(const char* response);
int getNextParam(const char* &ptr);
String getNextParamStr(const char* &ptr);
int findOrCreatePwmChannel(int pin);
uint8_t hexCharToNibble(char c);
void hexStringToBytes(const char* hex, uint8_t* bytes, int len);
String bytesToHexString(const uint8_t* data, int len);
int findServo(int pin);
void ensureOneWire(int pin);

void loadWifiConfig();
void saveWifiConfig();
void connectWifi();
void handleTcpClients();

// Wi-Fi saved in flash is joined in the background (waiting for it at boot delayed the CODEBRIDGE_READY banner by up to
// 10 seconds when the access point was not reachable). serviceWifi() keeps trying for as long as the board is on, starts
// the network services when the link comes up and stops them when it goes down, so a slow router, a router restart or a
// power cut never leaves the board unreachable until someone presses reset.
static bool wifiBootPending = false;          // a join attempt was started and is still within its time slot
static unsigned long wifiBootStartedAt = 0;
static const unsigned long WIFI_ATTEMPT_MS = 20000;
static bool tcpServerRunning = false;

// "codebridge-a1b2.local": the last 4 hex digits of the MAC address make the name unique per board.
static void startMdns() {
  String mac = WiFi.macAddress();
  mac.replace(":", "");
  mac.toLowerCase();
  String host = "codebridge-" + mac.substring(mac.length() - 4);

  MDNS.end();
  if (!MDNS.begin(host.c_str())) return;
  MDNS.addService("codebridge", "tcp", TCP_PORT);
  MDNS.addServiceTxt("codebridge", "tcp", "id", mac);
  MDNS.addServiceTxt("codebridge", "tcp", "fw", FIRMWARE_VERSION);
  MDNS.addServiceTxt("codebridge", "tcp", "board", ESP.getChipModel());
  MDNS.addServiceTxt("codebridge", "tcp", "auth", strlen(apiToken) > 0 ? "1" : "0");
}

static void startNetworkServices() {
  meshOnWifiUp();
  wifiConnected = true;
  wifiEnabled = true;
  wifiBootPending = false;
  WiFi.setSleep(false);  // modem sleep adds ~100 ms to every answer
  if (!tcpServerRunning) {
    tcpServer.begin();
    tcpServerRunning = true;
  }
  startMdns();
}

static void stopNetworkServices() {
  wifiConnected = false;
  for (int i = 0; i < MAX_TCP_CLIENTS; i++) {
    if (tcpClients[i]) tcpClients[i].stop();
    tcpCmdIndexes[i] = 0;
    tcpOverflow[i] = false;
    tcpAuthed[i] = false;
  }
  if (tcpServerRunning) {
    tcpServer.end();
    tcpServerRunning = false;
  }
}

static void serviceWifi() {
  if (WiFi.status() == WL_CONNECTED) {
    if (!wifiConnected) startNetworkServices();
    return;
  }

  if (wifiConnected) stopNetworkServices();  // the link was lost
  if (strlen(wifiSSID) == 0) return;

  if (!wifiBootPending || millis() - wifiBootStartedAt > WIFI_ATTEMPT_MS) {
    WiFi.mode(WIFI_STA);
    WiFi.begin(wifiSSID, wifiPassword);
    wifiBootPending = true;
    wifiBootStartedAt = millis();
  }
}

// ══════════════════════════════════════════════════════════════
//  SETUP
// ══════════════════════════════════════════════════════════════
void setup() {
  Serial.begin(SERIAL_BAUD);
  Wire.begin();
  SPI.begin();

  for (int i = 0; i < MAX_PWM_CHANNELS; i++) {
    pwmChannels[i] = {-1, i, false};
  }
  for (int i = 0; i < MAX_SERVOS; i++) {
    servoPins[i] = -1;
    servoActive[i] = false;
  }
  for (int i = 0; i < MAX_MOTORS; i++) {
    motors[i] = {-1, -1, -1, -1, false};
  }
  for (int i = 0; i < MAX_STEPPERS; i++) {
    steppers[i] = {{-1,-1,-1,-1}, 0, false};
  }
  for (int i = 0; i < MAX_INTERRUPT_PINS; i++) {
    intPins[i] = {-1, 0, 0, 0, false};
  }
  for (int i = 0; i < MAX_SAMPLE_CHANNELS; i++) {
    sampleChannels[i] = {false, -1, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, nullptr};
  }

  while (!Serial) { delay(10); }

  // Load saved WiFi config from flash
  loadWifiConfig();

  // Announce readiness first so the host can talk to the board right after boot.
  Serial.println("OK:CODEBRIDGE_READY");

  // Auto-connect in the background if credentials exist
  if (strlen(wifiSSID) > 0) {
    WiFi.mode(WIFI_STA);
    WiFi.begin(wifiSSID, wifiPassword);
    wifiBootPending = true;
    wifiBootStartedAt = millis();
  }

  meshBegin();
}

// ══════════════════════════════════════════════════════════════
//  MAIN LOOP
// ══════════════════════════════════════════════════════════════
void loop() {
  serviceWifi();
  meshService();

  // Handle Serial
  while (Serial.available()) {
    char c = Serial.read();

    if (c == '\n' || c == '\r') {
      if (serialOverflow) {
        serialOverflow = false;
        cmdIndex = 0;
        activeResponseTarget = -1;
        sendError("Line too long");
      } else if (cmdIndex > 0) {
        cmdBuffer[cmdIndex] = '\0';
        activeResponseTarget = -1;
        processCommand(cmdBuffer);
        cmdIndex = 0;
      }
    } else if (serialOverflow) {
      // swallow the rest of an oversized line instead of executing its truncated head
    } else if (cmdIndex < MAX_CMD_LENGTH - 1) {
      cmdBuffer[cmdIndex++] = c;
    } else {
      serialOverflow = true;
    }
  }

  // Handle TCP
  if (wifiConnected) {
    handleTcpClients();
  }

  // Handle MQTT
  if (mqttConnected && mqttClient.connected()) {
    mqttClient.loop();
  } else if (mqttConnected && !mqttClient.connected()) {
    mqttConnected = false;
  }

  serviceSampleChannels();
}

// ══════════════════════════════════════════════════════════════
//  WiFi FUNCTIONS
// ══════════════════════════════════════════════════════════════
void loadWifiConfig() {
  preferences.begin("codebridge", true);
  String ssid = preferences.getString("ssid", "");
  String pass = preferences.getString("pass", "");
  preferences.end();

  ssid.toCharArray(wifiSSID, sizeof(wifiSSID));
  pass.toCharArray(wifiPassword, sizeof(wifiPassword));

  preferences.begin("codebridge", true);
  String token = preferences.getString("token", "");
  preferences.end();
  token.toCharArray(apiToken, sizeof(apiToken));
}

void saveWifiConfig() {
  preferences.begin("codebridge", false);
  preferences.putString("ssid", wifiSSID);
  preferences.putString("pass", wifiPassword);
  preferences.end();
}

void connectWifi() {
  wifiBootPending = false;
  Serial.print("WiFi: Connecting to ");
  Serial.print(wifiSSID);
  Serial.print("...");

  WiFi.mode(WIFI_STA);
  WiFi.begin(wifiSSID, wifiPassword);

  int attempts = 0;
  while (WiFi.status() != WL_CONNECTED && attempts < 20) {
    delay(500);
    Serial.print(".");
    attempts++;
  }

  if (WiFi.status() == WL_CONNECTED) {
    startNetworkServices();

    Serial.println(" Connected!");
    Serial.print("WiFi: IP = ");
    Serial.println(WiFi.localIP());
    Serial.print("WiFi: TCP port ");
    Serial.println(TCP_PORT);
  } else {
    wifiConnected = false;
    Serial.println(" FAILED");
    Serial.println("WiFi: Continuing with Serial only");
  }
}

static void dropTcpClient(int i) {
  tcpClients[i].stop();
  tcpCmdIndexes[i] = 0;
  tcpOverflow[i] = false;
  tcpAuthed[i] = false;
  tcpAuthFails[i] = 0;
}

void handleTcpClients() {
  // Accept new clients
  WiFiClient newClient = tcpServer.available();
  if (newClient) {
    bool placed = false;
    for (int i = 0; i < MAX_TCP_CLIENTS; i++) {
      if (!tcpClients[i] || !tcpClients[i].connected()) {
        tcpClients[i] = newClient;
        tcpCmdIndexes[i] = 0;
        tcpOverflow[i] = false;
        tcpAuthed[i] = false;
        tcpAuthFails[i] = 0;
        tcpConnectedAt[i] = millis();
        tcpClients[i].println("OK:CODEBRIDGE_READY");
        placed = true;
        break;
      }
    }
    if (!placed) newClient.stop();
  }

  // Read from connected clients
  for (int i = 0; i < MAX_TCP_CLIENTS; i++) {
    if (tcpClients[i] && tcpClients[i].connected()) {
      // A client that never authenticates must not hold a slot forever.
      if (!tcpAuthed[i] && millis() - tcpConnectedAt[i] > TCP_AUTH_TIMEOUT_MS) {
        activeResponseTarget = i;
        sendError("AUTH_TIMEOUT");
        dropTcpClient(i);
        continue;
      }

      int budget = 256;  // bytes handled per client per pass, so one chatty client cannot starve the rest of loop()
      while (budget-- > 0 && tcpClients[i].available()) {
        char c = tcpClients[i].read();

        if (c == '\n' || c == '\r') {
          if (tcpOverflow[i]) {
            tcpOverflow[i] = false;
            tcpCmdIndexes[i] = 0;
            activeResponseTarget = i;
            sendError("Line too long");
          } else if (tcpCmdIndexes[i] > 0) {
            tcpCmdBuffers[i][tcpCmdIndexes[i]] = '\0';
            activeResponseTarget = i;
            processCommand(tcpCmdBuffers[i]);
            tcpCmdIndexes[i] = 0;
            if (!tcpClients[i] || !tcpClients[i].connected()) break;
          }
        } else if (tcpOverflow[i]) {
          // discard
        } else if (tcpCmdIndexes[i] < MAX_CMD_LENGTH - 1) {
          tcpCmdBuffers[i][tcpCmdIndexes[i]++] = c;
        } else {
          tcpOverflow[i] = true;
        }
      }
    } else if (tcpClients[i]) {
      dropTcpClient(i);
    }
  }
}

// Constant-time comparison so the token cannot be recovered from response timing.
static bool tokenMatches(const char* given) {
  size_t expected = strlen(apiToken);
  size_t got = strlen(given);
  if (expected == 0) return false;
  uint8_t diff = (uint8_t)(expected ^ got);
  for (size_t k = 0; k < expected; k++) {
    char g = k < got ? given[k] : 0;
    diff |= (uint8_t)(apiToken[k] ^ g);
  }
  return diff == 0;
}

// AUTH:<token> - the first command a TCP client must send.
static void handleAuth(const char* params) {
  int i = activeResponseTarget;
  if (i < 0) { sendOK(); return; }  // USB serial is physically trusted
  if (strlen(apiToken) == 0) { sendError("NO_TOKEN: pair the board over USB first"); return; }

  if (tokenMatches(params)) {
    tcpAuthed[i] = true;
    tcpAuthFails[i] = 0;
    sendOK();
    return;
  }

  delay(250);  // slow down guessing
  if (++tcpAuthFails[i] >= TCP_MAX_AUTH_FAILS) {
    sendError("AUTH_FAILED: too many attempts");
    dropTcpClient(i);
  } else {
    sendError("AUTH_FAILED");
  }
}

// WTOK:<token> - USB only. Stores the pairing token (8-64 printable characters). "WTOK:" with nothing clears it.
static void handleSetToken(const char* params) {
  if (activeResponseTarget >= 0) { sendError("WTOK is only accepted over USB"); return; }

  size_t n = strlen(params);
  if (n != 0 && (n < 8 || n > 64)) { sendError("Token must be 8-64 characters"); return; }
  for (size_t k = 0; k < n; k++) {
    if (params[k] < 33 || params[k] > 126) { sendError("Token contains invalid characters"); return; }
  }

  strncpy(apiToken, params, sizeof(apiToken) - 1);
  apiToken[sizeof(apiToken) - 1] = '\0';
  preferences.begin("codebridge", false);
  preferences.putString("token", apiToken);
  preferences.end();

  for (int i = 0; i < MAX_TCP_CLIENTS; i++) {
    if (tcpClients[i]) dropTcpClient(i);  // sessions opened with the old token are over
  }
  sendOK();
}

// ══════════════════════════════════════════════════════════════
//  MESH (ESP-NOW relay)
//
//  Lets a board be reached through another one over ESP-NOW while that other board keeps its Wi-Fi link:
//    PC ──USB or Wi-Fi──> gateway ══ESP-NOW══> node
//  The gateway is any board with the mesh on that the PC can talk to; a node is any board with the mesh on. The same firmware plays
//  both roles. A node without Wi-Fi hops channels until a gateway answers, then stays on the gateway's channel.
//
//  Every frame carries an 8-byte HMAC-SHA256 tag made with the shared 16-byte mesh key (set over USB with MESHKEY, like WTOK), so a
//  stranger's frame is dropped. A command also has to echo a single-use challenge that the node handed out in its last reply, so a
//  captured command cannot be replayed. Wi-Fi, token, OTA and MQTT commands are never relayed.
//
//  Frame: 'C' 'B' | type | flags | seq(4) | field(4) | payload | tag(8)
//    HELLO_REQ field 0 | HELLO field = challenge for the receiver (0 in a beacon) | CMD field = challenge echoed
//    RESP payload = reqSeq(4) + text, field = the next challenge
// ══════════════════════════════════════════════════════════════
#include <esp_now.h>
#include <esp_wifi.h>
#include <esp_idf_version.h>
#include <mbedtls/md.h>

#define MESH_MAX_FRAME 250
#define MESH_HDR_LEN 12
#define MESH_TAG_LEN 8
#define MESH_MAX_NODES 8
#define MESH_RX_QUEUE 10
#define MESH_BEACON_MS 12000
#define MESH_HOP_MS 350
#define MESH_LOCK_TIMEOUT_MS 40000
#define MESH_NODE_TTL_MS 90000
#define MESH_RELAY_TIMEOUT_MS 1500

enum MeshType : uint8_t { MESH_HELLO_REQ = 1, MESH_HELLO = 2, MESH_CMD = 3, MESH_RESP = 4 };

struct MeshRx {
  uint8_t mac[6];
  uint8_t len;
  uint8_t data[MESH_MAX_FRAME];
};

struct MeshNode {
  bool used;
  uint8_t mac[6];
  char board[20];
  char fw[12];
  char name[26];
  uint32_t theirChal;   // challenge this node gave us: needed to send it a command
  uint32_t ourChal;     // challenge we gave this node: it must echo it in a command to us
  unsigned long seen;
  unsigned long lastAck;
};

static const uint8_t MESH_BROADCAST[6] = {0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF};
static bool meshEnabled = false;
static bool meshRadioUp = false;
static bool meshHasKey = false;
static bool meshLocked = false;         // a gateway answered on this channel (only meaningful without Wi-Fi)
static bool meshWaiting = false;        // inside MSCAN/MRLY: commands from the air wait until we are back in loop()
static uint8_t meshKey[16];
static uint8_t meshSelfMac[6];
static uint8_t meshHopChannel = 1;
static char meshName[26] = "";
static uint32_t meshTxSeq = 0;
static unsigned long meshLastHeard = 0, meshLastBeacon = 0, meshLastHop = 0;
static uint8_t meshFixedChannel = 0;   // 0 = search; 1-13 = stay on this channel (a board without Wi-Fi)
static uint8_t meshHopBeacons = 0;   // extra beacons still to send on this channel visit
static QueueHandle_t meshQueue = nullptr;
static MeshNode meshNodes[MESH_MAX_NODES];
static String meshReply;                // collects what a relayed command answers (response target -2)
static volatile uint32_t meshRawRx = 0;
static uint32_t meshRxFrames = 0, meshBadTags = 0, meshTxQueued = 0, meshTxDelivered = 0, meshTxLost = 0;   // diagnostics shown by MESH

// What a relay request is waiting for.
static bool meshPendingActive = false;
static uint8_t meshPendingMac[6];
static uint32_t meshPendingSeq = 0;
static bool meshPendingDone = false;
static String meshPendingText;
static bool meshPendingTruncated = false;

static String meshMacString(const uint8_t* mac) { return bytesToHexString(mac, 6); }

static bool meshParseMac(const char* text, uint8_t* mac) {
  if (strlen(text) < 12) return false;
  for (int i = 0; i < 12; i++) {
    char c = text[i];
    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
  }
  hexStringToBytes(text, mac, 6);
  return true;
}

static uint32_t meshRandomChallenge() {
  uint32_t v;
  do { v = esp_random(); } while (v == 0);
  return v;
}

static MeshNode* meshFindNode(const uint8_t* mac, bool create) {
  MeshNode* free_slot = nullptr;
  MeshNode* oldest = &meshNodes[0];
  for (int i = 0; i < MESH_MAX_NODES; i++) {
    MeshNode& n = meshNodes[i];
    if (n.used && memcmp(n.mac, mac, 6) == 0) return &n;
    if (!n.used && !free_slot) free_slot = &n;
    if (n.used && n.seen < oldest->seen) oldest = &n;
  }
  if (!create) return nullptr;
  MeshNode* slot = free_slot ? free_slot : oldest;
  memset(slot, 0, sizeof(MeshNode));
  slot->used = true;
  memcpy(slot->mac, mac, 6);
  return slot;
}

static void meshLoadConfig() {
  preferences.begin("codebridge", true);
  meshEnabled = preferences.isKey("mesh") && preferences.getBool("mesh", false);
  meshFixedChannel = preferences.isKey("meshch") ? preferences.getUChar("meshch", 0) : 0;
  if (meshFixedChannel > 13) meshFixedChannel = 0;
  // isKey() first: reading a missing entry makes the core print an error line on the protocol port.
  meshHasKey = preferences.isKey("meshkey") && preferences.getBytesLength("meshkey") == sizeof(meshKey) &&
               preferences.getBytes("meshkey", meshKey, sizeof(meshKey)) == sizeof(meshKey);
  String name = preferences.isKey("meshname") ? preferences.getString("meshname", "") : String("");
  preferences.end();
  name.toCharArray(meshName, sizeof(meshName));
}

// HMAC-SHA256 over the frame body and both addresses (so a frame cannot be moved to another path), truncated to 8 bytes.
static void meshTag(const uint8_t* body, size_t len, const uint8_t* src, const uint8_t* dst, uint8_t* out) {
  uint8_t full[32];
  mbedtls_md_context_t ctx;
  mbedtls_md_init(&ctx);
  mbedtls_md_setup(&ctx, mbedtls_md_info_from_type(MBEDTLS_MD_SHA256), 1);
  mbedtls_md_hmac_starts(&ctx, meshKey, sizeof(meshKey));
  mbedtls_md_hmac_update(&ctx, body, len);
  mbedtls_md_hmac_update(&ctx, src, 6);
  mbedtls_md_hmac_update(&ctx, dst, 6);
  mbedtls_md_hmac_finish(&ctx, full);
  mbedtls_md_free(&ctx);
  memcpy(out, full, MESH_TAG_LEN);
}

static bool meshEnsurePeer(const uint8_t* mac) {
  if (esp_now_is_peer_exist(mac)) return true;
  esp_now_peer_info_t peer = {};
  memcpy(peer.peer_addr, mac, 6);
  peer.channel = 0;          // follow the radio's current channel
  peer.encrypt = false;      // authenticity comes from the HMAC tag
  peer.ifidx = WIFI_IF_STA;
  return esp_now_add_peer(&peer) == ESP_OK;
}

static bool meshSend(const uint8_t* dst, uint8_t type, uint32_t field, const uint8_t* payload, size_t plen) {
  if (!meshRadioUp || !meshHasKey || plen + MESH_HDR_LEN + MESH_TAG_LEN > MESH_MAX_FRAME) return false;
  if (!meshEnsurePeer(dst)) return false;

  uint8_t frame[MESH_MAX_FRAME];
  frame[0] = 'C'; frame[1] = 'B'; frame[2] = type; frame[3] = 0;
  uint32_t seq = ++meshTxSeq;
  for (int i = 0; i < 4; i++) { frame[4 + i] = (seq >> (24 - 8 * i)) & 0xFF; frame[8 + i] = (field >> (24 - 8 * i)) & 0xFF; }
  if (plen) memcpy(frame + MESH_HDR_LEN, payload, plen);
  size_t body = MESH_HDR_LEN + plen;
  meshTag(frame, body, meshSelfMac, dst, frame + body);
  bool queued = esp_now_send(dst, frame, body + MESH_TAG_LEN) == ESP_OK;
  if (queued) meshTxQueued++; else meshTxLost++;
  return queued;
}

#if ESP_IDF_VERSION_MAJOR >= 5
static void meshOnSent(const wifi_tx_info_t*, esp_now_send_status_t status) {
#else
static void meshOnSent(const uint8_t*, esp_now_send_status_t status) {
#endif
  if (status == ESP_NOW_SEND_SUCCESS) meshTxDelivered++; else meshTxLost++;
}

static uint32_t meshReadU32(const uint8_t* p) { return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3]; }

static void meshSendHello(const uint8_t* dst, uint32_t challenge) {
  char payload[80];
  int n = snprintf(payload, sizeof(payload), "%s|%s|%s", ESP.getChipModel(), FIRMWARE_VERSION, meshName[0] ? meshName : "");
  meshSend(dst, MESH_HELLO, challenge, (const uint8_t*)payload, n);
}

static bool meshIsDenied(const char* cmd) {
  static const char* denied[] = {"AUTH", "WTOK", "WCFG", "WCFGX", "WSCAN", "OTAB", "OTAS", "MQC", "MQP", "MQS", "MQU", "MQR", "MQD",
                                 "MESH", "MESHON", "MESHOFF", "MESHKEY", "MESHNAME", "MESHCH", "MSCAN", "MRLY"};
  const char* sep = strchr(cmd, ':');
  size_t len = sep ? (size_t)(sep - cmd) : strlen(cmd);
  for (size_t i = 0; i < sizeof(denied) / sizeof(denied[0]); i++) {
    if (strlen(denied[i]) == len && strncasecmp(cmd, denied[i], len) == 0) return true;
  }
  return false;
}

// Runs a command that arrived over the air and sends the answer back.
static void meshExecute(MeshNode* node, uint32_t reqSeq, const char* cmd) {
  meshReply = "";
  if (meshIsDenied(cmd)) {
    meshReply = "ERR:MESH_DENIED";
  } else {
    int saved = activeResponseTarget;
    activeResponseTarget = -2;
    processCommand(cmd);
    activeResponseTarget = saved;
  }

  uint8_t payload[MESH_MAX_FRAME];
  for (int i = 0; i < 4; i++) payload[i] = (reqSeq >> (24 - 8 * i)) & 0xFF;
  size_t room = MESH_MAX_FRAME - MESH_HDR_LEN - MESH_TAG_LEN - 4;
  size_t n = meshReply.length() > room ? room : meshReply.length();
  if (n) memcpy(payload + 4, meshReply.c_str(), n);
  node->ourChal = meshRandomChallenge();
  meshSend(node->mac, MESH_RESP, node->ourChal, payload, 4 + n);
}

static void meshHandleFrame(const MeshRx& rx) {
  if (rx.len < MESH_HDR_LEN + MESH_TAG_LEN || rx.data[0] != 'C' || rx.data[1] != 'B' || !meshHasKey) return;
  meshRxFrames++;

  size_t body = rx.len - MESH_TAG_LEN;
  uint8_t expected[MESH_TAG_LEN];
  bool broadcast = false;
  meshTag(rx.data, body, rx.mac, meshSelfMac, expected);
  if (memcmp(expected, rx.data + body, MESH_TAG_LEN) != 0) {
    meshTag(rx.data, body, rx.mac, MESH_BROADCAST, expected);
    if (memcmp(expected, rx.data + body, MESH_TAG_LEN) != 0) { meshBadTags++; return; }   // not made with our key
    broadcast = true;
  }

  uint8_t type = rx.data[2];
  uint32_t field = meshReadU32(rx.data + 8);
  const uint8_t* payload = rx.data + MESH_HDR_LEN;
  size_t plen = body - MESH_HDR_LEN;
  unsigned long now = millis();
  meshLastHeard = now;

  MeshNode* node = meshFindNode(rx.mac, true);
  node->seen = now;

  if (type == MESH_HELLO_REQ) {
    node->ourChal = meshRandomChallenge();
    meshSendHello(rx.mac, node->ourChal);
  } else if (type == MESH_HELLO) {
    char text[80];
    size_t n = plen < sizeof(text) - 1 ? plen : sizeof(text) - 1;
    memcpy(text, payload, n);
    text[n] = '\0';
    char* second = strchr(text, '|');
    char* third = second ? strchr(second + 1, '|') : nullptr;
    if (second) *second = '\0';
    if (third) *third = '\0';
    strncpy(node->board, text, sizeof(node->board) - 1);
    if (second) strncpy(node->fw, second + 1, sizeof(node->fw) - 1);
    if (third) strncpy(node->name, third + 1, sizeof(node->name) - 1);
    if (!broadcast && field != 0) node->theirChal = field;
    meshLocked = true;
    // A beacon is answered once in a while with a unicast HELLO: that is how a node searching for the channel learns it found it.
    if (broadcast && now - node->lastAck > 2000) {
      node->lastAck = now;
      node->ourChal = meshRandomChallenge();
      meshSendHello(rx.mac, node->ourChal);
    }
  } else if (type == MESH_CMD && !meshWaiting) {
    char cmd[MESH_MAX_FRAME];
    size_t n = plen < sizeof(cmd) - 1 ? plen : sizeof(cmd) - 1;
    memcpy(cmd, payload, n);
    cmd[n] = '\0';
    if (node->ourChal == 0 || field != node->ourChal) {
      // wrong or already used challenge: hand out a fresh one and let the sender try again
      meshReply = "ERR:CHALLENGE";
      uint8_t body2[4 + 13];
      uint32_t reqSeq = meshReadU32(rx.data + 4);
      for (int i = 0; i < 4; i++) body2[i] = (reqSeq >> (24 - 8 * i)) & 0xFF;
      memcpy(body2 + 4, "ERR:CHALLENGE", 13);
      node->ourChal = meshRandomChallenge();
      meshSend(rx.mac, MESH_RESP, node->ourChal, body2, sizeof(body2));
    } else {
      node->ourChal = 0;   // single use
      meshExecute(node, meshReadU32(rx.data + 4), cmd);
    }
  } else if (type == MESH_RESP && meshPendingActive && memcmp(rx.mac, meshPendingMac, 6) == 0 && plen >= 4) {
    if (meshReadU32(payload) == meshPendingSeq) {
      node->theirChal = field;
      size_t n = plen - 4;
      meshPendingText = "";
      for (size_t i = 0; i < n; i++) meshPendingText += (char)payload[4 + i];
      meshPendingTruncated = (rx.data[3] & 1) != 0;
      meshPendingDone = true;
    }
  }
}

#if ESP_IDF_VERSION_MAJOR >= 5
static void meshOnRecv(const esp_now_recv_info_t* info, const uint8_t* data, int len) {
  const uint8_t* mac = info->src_addr;
#else
static void meshOnRecv(const uint8_t* mac, const uint8_t* data, int len) {
#endif
  meshRawRx++;
  if (!meshQueue || len <= 0 || len > MESH_MAX_FRAME) return;
  MeshRx rx;
  memcpy(rx.mac, mac, 6);
  rx.len = (uint8_t)len;
  memcpy(rx.data, data, len);
  xQueueSend(meshQueue, &rx, 0);   // a full queue drops the frame: ESP-NOW is best effort anyway
}

static void meshDrain() {
  if (!meshQueue) return;
  MeshRx rx;
  while (xQueueReceive(meshQueue, &rx, 0) == pdTRUE) meshHandleFrame(rx);
}

static uint8_t meshCurrentChannel() {
  uint8_t primary = 1;
  wifi_second_chan_t second;
  esp_wifi_get_channel(&primary, &second);
  return primary;
}

static bool meshStartRadio() {
  if (meshRadioUp) return true;
  if (!meshHasKey) return false;

  if (WiFi.getMode() == WIFI_OFF) WiFi.mode(WIFI_STA);   // ESP-NOW rides on the station interface; this does not join anything
  WiFi.macAddress(meshSelfMac);
  if (!meshQueue) meshQueue = xQueueCreate(MESH_RX_QUEUE, sizeof(MeshRx));
  if (esp_now_init() != ESP_OK) return false;
  esp_now_register_recv_cb(meshOnRecv);
  esp_now_register_send_cb(meshOnSent);
  esp_wifi_set_ps(WIFI_PS_NONE);   // modem sleep switches the radio off between router beacons and ESP-NOW frames would be missed
  meshEnsurePeer(MESH_BROADCAST);
  meshRadioUp = true;
  meshLocked = false;
  if (meshFixedChannel > 0 && !wifiConnected) {
    esp_wifi_set_channel(meshFixedChannel, WIFI_SECOND_CHAN_NONE);
    meshHopChannel = meshFixedChannel;
    meshLocked = true;
  }
  meshLastHeard = millis();
  meshLastBeacon = 0;
  return true;
}

static void meshStopRadio() {
  if (!meshRadioUp) return;
  esp_now_unregister_recv_cb();
  esp_now_deinit();
  meshRadioUp = false;
  meshLocked = false;
  memset(meshNodes, 0, sizeof(meshNodes));
}

// Called when the station joins the router: the power-save mode is renegotiated then.
void meshOnWifiUp() {
  if (meshRadioUp) esp_wifi_set_ps(WIFI_PS_NONE);
}

void meshBegin() {
  memset(meshNodes, 0, sizeof(meshNodes));
  meshLoadConfig();
  if (meshName[0] == '\0') {
    uint8_t mac[6];
    WiFi.macAddress(mac);
    snprintf(meshName, sizeof(meshName), "node-%02x%02x", mac[4], mac[5]);
  }
  if (meshEnabled && meshHasKey) meshStartRadio();
}

// Called from loop().
void meshService() {
  if (!meshRadioUp) return;
  meshDrain();

  unsigned long now = millis();
  if (meshLocked && meshFixedChannel == 0 && now - meshLastHeard > MESH_LOCK_TIMEOUT_MS && !wifiConnected) meshLocked = false;

  // Without Wi-Fi the channel is ours to choose: hop until a gateway answers a beacon, then stay.
  if (!wifiConnected && !meshLocked && now - meshLastHop > MESH_HOP_MS) {
    meshLastHop = now;
    meshHopChannel = meshHopChannel >= 13 ? 1 : meshHopChannel + 1;
    esp_wifi_set_channel(meshHopChannel, WIFI_SECOND_CHAN_NONE);
    meshSendHello(MESH_BROADCAST, 0);
    meshHopBeacons = 2;
  } else if (!wifiConnected && !meshLocked && meshHopBeacons > 0 && now - meshLastHop > (unsigned long)(MESH_HOP_MS / 3) * (3 - meshHopBeacons)) {
    meshHopBeacons--;
    meshSendHello(MESH_BROADCAST, 0);
  } else if (now - meshLastBeacon > MESH_BEACON_MS) {
    meshLastBeacon = now;
    meshSendHello(MESH_BROADCAST, 0);
  }
}

static void meshWait(unsigned long ms, bool (*done)()) {
  unsigned long start = millis();
  while (millis() - start < ms) {
    meshDrain();
    if (done && done()) return;
    delay(2);
  }
}

static bool meshPendingFinished() { return meshPendingDone; }

// MSCAN: ask everything in range to introduce itself and list what answered recently.
static void handleMeshScan() {
  if (!meshHasKey) { sendError("MESH_NOKEY"); return; }
  if (!meshRadioUp) { sendError("MESH_OFF"); return; }
  meshWaiting = true;
  meshSend(MESH_BROADCAST, MESH_HELLO_REQ, 0, nullptr, 0);
  meshWait(900, nullptr);
  meshWaiting = false;

  String out;
  unsigned long now = millis();
  for (int i = 0; i < MESH_MAX_NODES; i++) {
    MeshNode& n = meshNodes[i];
    if (!n.used || n.board[0] == '\0' || now - n.seen > MESH_NODE_TTL_MS) continue;
    if (out.length()) out += ";";
    out += meshMacString(n.mac) + "," + n.board + "," + n.fw + "," + n.name + "," + String((now - n.seen) / 1000);
  }
  sendOK(out.c_str());
}

// MRLY:<mac>:<command>  runs <command> on that node and returns its answer as RLY:<answer>.
static void handleMeshRelay(const char* params) {
  if (!meshHasKey) { sendError("MESH_NOKEY"); return; }
  if (!meshRadioUp) { sendError("MESH_OFF"); return; }

  uint8_t mac[6];
  if (!meshParseMac(params, mac) || params[12] != ':' || params[13] == '\0') { sendError("MESH_BAD_REQUEST"); return; }
  const char* cmd = params + 13;
  if (meshIsDenied(cmd)) { sendError("MESH_DENIED"); return; }

  MeshNode* node = meshFindNode(mac, true);
  meshWaiting = true;
  bool answered = false;

  for (int attempt = 0; attempt < 2 && !answered; attempt++) {
    if (node->theirChal == 0) {
      // no challenge yet: ask the node for one
      meshSend(mac, MESH_HELLO_REQ, 0, nullptr, 0);
      unsigned long t0 = millis();
      while (millis() - t0 < 500 && node->theirChal == 0) { meshDrain(); delay(2); }
      if (node->theirChal == 0) continue;
    }

    meshPendingActive = true;
    meshPendingDone = false;
    meshPendingTruncated = false;
    memcpy(meshPendingMac, mac, 6);
    meshPendingSeq = meshTxSeq + 1;   // the sequence number meshSend() is about to use
    uint32_t chal = node->theirChal;
    node->theirChal = 0;
    if (!meshSend(mac, MESH_CMD, chal, (const uint8_t*)cmd, strlen(cmd))) { meshPendingActive = false; break; }
    meshWait(MESH_RELAY_TIMEOUT_MS, meshPendingFinished);
    meshPendingActive = false;
    if (meshPendingDone && meshPendingText != "ERR:CHALLENGE") answered = true;
  }
  meshWaiting = false;

  if (!answered) { sendError(meshPendingDone ? "MESH_CHALLENGE" : "MESH_TIMEOUT"); return; }
  if (meshPendingTruncated) { sendError("MESH_RESPONSE_TOO_LONG"); return; }
  sendResponse(("RLY:" + meshPendingText).c_str());
}

// MESH / MESHON / MESHOFF / MESHKEY:<32 hex> / MESHNAME:<name> / MSCAN / MRLY:<mac>:<command>
static bool meshHandleCommand(const char* cmd, int cmdLen, const char* params) {
  auto is = [&](const char* name) { return (int)strlen(name) == cmdLen && strncmp(cmd, name, cmdLen) == 0; };

  if (is("MESH")) {
    char line[140];
    snprintf(line, sizeof(line), "%s,%s,%u,%d,%d,%s,%u,%u,%u,%u", meshEnabled ? "on" : "off", meshMacString(meshSelfMac).c_str(),
             meshRadioUp ? meshCurrentChannel() : 0, meshLocked || wifiConnected ? 1 : 0, meshHasKey ? 1 : 0, meshName,
             (unsigned)meshRawRx, (unsigned)meshBadTags, (unsigned)meshTxDelivered, (unsigned)meshTxLost);
    sendOK(line);
    return true;
  }
  if (is("MESHKEY")) {
    if (activeResponseTarget != -1) { sendError("MESHKEY is only accepted over USB"); return true; }
    if (strlen(params) == 0) {
      preferences.begin("codebridge", false);
      preferences.remove("meshkey");
      preferences.putBool("mesh", false);
      preferences.end();
      meshStopRadio();
      meshHasKey = false;
      meshEnabled = false;
      sendOK();
      return true;
    }
    if (strlen(params) != 32) { sendError("Mesh key must be 32 hex characters"); return true; }
    for (int i = 0; i < 32; i++) {
      char c = params[i];
      if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) { sendError("Mesh key must be hex"); return true; }
    }
    hexStringToBytes(params, meshKey, 16);
    preferences.begin("codebridge", false);
    preferences.putBytes("meshkey", meshKey, sizeof(meshKey));
    preferences.end();
    meshHasKey = true;
    meshStopRadio();                       // a new key restarts the radio with it
    if (meshEnabled) meshStartRadio();
    sendOK();
    return true;
  }
  if (is("MESHCH")) {
    // MESHCH:<1-13> pins the radio channel of a board without Wi-Fi (it then never searches); MESHCH:0 goes back to searching.
    if (activeResponseTarget != -1) { sendError("MESHCH is only accepted over USB"); return true; }
    int ch = atoi(params);
    if (ch < 0 || ch > 13) { sendError("Channel must be 0-13"); return true; }
    meshFixedChannel = (uint8_t)ch;
    preferences.begin("codebridge", false);
    preferences.putUChar("meshch", meshFixedChannel);
    preferences.end();
    if (meshRadioUp && !wifiConnected) {
      if (ch > 0) { esp_wifi_set_channel(ch, WIFI_SECOND_CHAN_NONE); meshHopChannel = ch; meshLocked = true; }
      else meshLocked = false;
    }
    sendOK();
    return true;
  }
  if (is("MESHNAME")) {
    if (activeResponseTarget != -1) { sendError("MESHNAME is only accepted over USB"); return true; }
    size_t n = strlen(params);
    if (n == 0 || n > 24) { sendError("Name must be 1-24 characters"); return true; }
    for (size_t i = 0; i < n; i++) {
      if (params[i] < 33 || params[i] > 126 || params[i] == '|' || params[i] == ',' || params[i] == ';') { sendError("Name contains invalid characters"); return true; }
    }
    strncpy(meshName, params, sizeof(meshName) - 1);
    preferences.begin("codebridge", false);
    preferences.putString("meshname", meshName);
    preferences.end();
    sendOK();
    return true;
  }
  if (is("MESHON") || is("MESHOFF")) {
    if (activeResponseTarget != -1) { sendError("The mesh is switched over USB"); return true; }
    bool on = is("MESHON");
    if (on && !meshHasKey) { sendError("MESH_NOKEY"); return true; }
    meshEnabled = on;
    preferences.begin("codebridge", false);
    preferences.putBool("mesh", on);
    preferences.end();
    if (on) { if (!meshStartRadio()) { sendError("ESP-NOW failed to start"); return true; } }
    else meshStopRadio();
    sendOK();
    return true;
  }
  if (is("MSCAN")) { handleMeshScan(); return true; }
  if (is("MRLY")) { handleMeshRelay(params); return true; }
  return false;
}

// ══════════════════════════════════════════════════════════════
//  COMMAND ROUTER
// ══════════════════════════════════════════════════════════════
void processCommand(const char* cmd) {
  const char* sep = strchr(cmd, ':');
  int cmdLen = sep ? (sep - cmd) : strlen(cmd);
  const char* params = sep ? sep + 1 : "";

  if (cmdLen == 4 && strncmp(cmd, "AUTH", 4) == 0) { handleAuth(params); return; }
  if (cmdLen == 4 && strncmp(cmd, "WTOK", 4) == 0) { handleSetToken(params); return; }
  if (activeResponseTarget >= 0 && !tcpAuthed[activeResponseTarget]) {
    sendError(strlen(apiToken) == 0 ? "NO_TOKEN: pair the board over USB first" : "AUTH_REQUIRED");
    return;
  }
  if (meshHandleCommand(cmd, cmdLen, params)) return;

  if (strncmp(cmd, "PM", cmdLen) == 0 && cmdLen == 2) {
    handlePinMode(params);
  } else if (strncmp(cmd, "DW", cmdLen) == 0 && cmdLen == 2) {
    handleDigitalWrite(params);
  } else if (strncmp(cmd, "DR", cmdLen) == 0 && cmdLen == 2) {
    handleDigitalRead(params);
  } else if (strncmp(cmd, "AR", cmdLen) == 0 && cmdLen == 2) {
    handleAnalogRead(params);
  } else if (strncmp(cmd, "PW", cmdLen) == 0 && cmdLen == 2) {
    handlePwmWrite(params);
  } else if (strncmp(cmd, "IS", cmdLen) == 0 && cmdLen == 2) {
    handleI2cScan(params);
  } else if (strncmp(cmd, "IW", cmdLen) == 0 && cmdLen == 2) {
    handleI2cWrite(params);
  } else if (strncmp(cmd, "IR", cmdLen) == 0 && cmdLen == 2) {
    handleI2cRead(params);
  } else if (strncmp(cmd, "IWR", cmdLen) == 0 && cmdLen == 3) {
    handleI2cWriteReg(params);
  } else if (strncmp(cmd, "IRR", cmdLen) == 0 && cmdLen == 3) {
    handleI2cReadReg(params);
  } else if (strncmp(cmd, "PING", cmdLen) == 0 && cmdLen == (sizeof("PING") - 1)) {
    handlePing();
  } else if (strncmp(cmd, "INFO", cmdLen) == 0 && cmdLen == (sizeof("INFO") - 1)) {
    handleInfo();
  } else if (strncmp(cmd, "VER", cmdLen) == 0 && cmdLen == (sizeof("VER") - 1)) {
    handleVersion();
  } else if (strncmp(cmd, "RST", cmdLen) == 0 && cmdLen == (sizeof("RST") - 1)) {
    handleReset();
  } else if (strncmp(cmd, "WCFG", cmdLen) == 0 && cmdLen == 4) {
    handleWifiConfig(params);
  } else if (strncmp(cmd, "WCFGX", cmdLen) == 0 && cmdLen == 5) {
    handleWifiConfigHex(params);
  } else if (strncmp(cmd, "WSTAT", cmdLen) == 0 && cmdLen == 5) {
    handleWifiStatus();
  } else if (strncmp(cmd, "WSCAN", cmdLen) == 0 && cmdLen == 5) {
    handleWifiScan();
  }
  // ── SPI Commands ──
  else if (strncmp(cmd, "ST", cmdLen) == 0 && cmdLen == 2) {
    handleSpiTransfer(params);
  } else if (strncmp(cmd, "SW", cmdLen) == 0 && cmdLen == 2) {
    handleSpiWrite(params);
  } else if (strncmp(cmd, "SR", cmdLen) == 0 && cmdLen == 2) {
    handleSpiRead(params);
  } else if (strncmp(cmd, "SC", cmdLen) == 0 && cmdLen == 2) {
    handleSpiConfig(params);
  }
  // ── OneWire Commands ──
  else if (strncmp(cmd, "OWS", cmdLen) == 0 && cmdLen == 3) {
    handleOwScan(params);
  } else if (strncmp(cmd, "OWR", cmdLen) == 0 && cmdLen == 3) {
    handleOwRead(params);
  } else if (strncmp(cmd, "OWW", cmdLen) == 0 && cmdLen == 3) {
    handleOwWrite(params);
  } else if (strncmp(cmd, "OWT", cmdLen) == 0 && cmdLen == 3) {
    handleOwTemp(params);
  }
  // ── Servo Commands ──
  else if (strncmp(cmd, "SA", cmdLen) == 0 && cmdLen == 2) {
    handleServoAttach(params);
  } else if (strncmp(cmd, "SV", cmdLen) == 0 && cmdLen == 2) {
    handleServoWrite(params);
  } else if (strncmp(cmd, "SVR", cmdLen) == 0 && cmdLen == 3) {
    handleServoRead(params);
  } else if (strncmp(cmd, "SU", cmdLen) == 0 && cmdLen == 2) {
    handleServoMicroseconds(params);
  } else if (strncmp(cmd, "SD", cmdLen) == 0 && cmdLen == 2) {
    handleServoDetach(params);
  }
  // ── NeoPixel Commands ──
  else if (strncmp(cmd, "NI", cmdLen) == 0 && cmdLen == 2) {
    handleNeoInit(params);
  } else if (strncmp(cmd, "NS", cmdLen) == 0 && cmdLen == 2) {
    handleNeoSet(params);
  } else if (strncmp(cmd, "NA", cmdLen) == 0 && cmdLen == 2) {
    handleNeoAll(params);
  } else if (strncmp(cmd, "NH", cmdLen) == 0 && cmdLen == 2) {
    handleNeoShow();
  } else if (strncmp(cmd, "NC", cmdLen) == 0 && cmdLen == 2) {
    handleNeoClear();
  } else if (strncmp(cmd, "NB", cmdLen) == 0 && cmdLen == 2) {
    handleNeoBrightness(params);
  } else if (strncmp(cmd, "NR", cmdLen) == 0 && cmdLen == 2) {
    handleNeoRange(params);
  }
  // ── Tone Commands ──
  else if (strncmp(cmd, "TN", cmdLen) == 0 && cmdLen == 2) {
    handleTone(params);
  } else if (strncmp(cmd, "NT", cmdLen) == 0 && cmdLen == 2) {
    handleNoTone(params);
  }
  // ── DHT Commands ──
  else if (strncmp(cmd, "DHTI", cmdLen) == 0 && cmdLen == 4) {
    handleDhtInit(params);
  } else if (strncmp(cmd, "DHTR", cmdLen) == 0 && cmdLen == 4) {
    handleDhtRead(params);
  }
  // ── Ultrasonic Command ──
  else if (strncmp(cmd, "USR", cmdLen) == 0 && cmdLen == 3) {
    handleUltrasonicRead(params);
  }
  // ── Motor Commands ──
  else if (strncmp(cmd, "MI", cmdLen) == 0 && cmdLen == 2) {
    handleMotorInit(params);
  } else if (strncmp(cmd, "MS", cmdLen) == 0 && cmdLen == 2) {
    handleMotorSpeed(params);
  } else if (strncmp(cmd, "MX", cmdLen) == 0 && cmdLen == 2) {
    handleMotorStop(params);
  }
  // ── Stepper Commands ──
  else if (strncmp(cmd, "STI", cmdLen) == 0 && cmdLen == 3) {
    handleStepperInit(params);
  } else if (strncmp(cmd, "STS", cmdLen) == 0 && cmdLen == 3) {
    handleStepperStep(params);
  }
  // ── OLED Commands ──
  else if (strncmp(cmd, "OI", cmdLen) == 0 && cmdLen == 2) {
    handleOledInit(params);
  } else if (strncmp(cmd, "OC", cmdLen) == 0 && cmdLen == 2) {
    handleOledClear();
  } else if (strncmp(cmd, "OT", cmdLen) == 0 && cmdLen == 2) {
    handleOledText(params);
  } else if (strncmp(cmd, "OP", cmdLen) == 0 && cmdLen == 2) {
    handleOledPixel(params);
  } else if (strncmp(cmd, "OL", cmdLen) == 0 && cmdLen == 2) {
    handleOledLine(params);
  } else if (strncmp(cmd, "OR", cmdLen) == 0 && cmdLen == 2) {
    handleOledRect(params);
  } else if (strncmp(cmd, "OE", cmdLen) == 0 && cmdLen == 2) {
    handleOledCircle(params);
  } else if (strncmp(cmd, "OF", cmdLen) == 0 && cmdLen == 2) {
    handleOledFlush();
  } else if (strncmp(cmd, "OB", cmdLen) == 0 && cmdLen == 2) {
    handleOledBrightness(params);
  }
  // ── LCD Commands ──
  else if (strncmp(cmd, "LI", cmdLen) == 0 && cmdLen == 2) {
    handleLcdInit(params);
  } else if (strncmp(cmd, "LC", cmdLen) == 0 && cmdLen == 2) {
    handleLcdClear();
  } else if (strncmp(cmd, "LT", cmdLen) == 0 && cmdLen == 2) {
    handleLcdText(params);
  } else if (strncmp(cmd, "LB", cmdLen) == 0 && cmdLen == 2) {
    handleLcdBacklight(params);
  } else if (strncmp(cmd, "LK", cmdLen) == 0 && cmdLen == 2) {
    handleLcdCursor(params);
  }
  // ── BME280 Commands ──
  else if (strncmp(cmd, "BMI", cmdLen) == 0 && cmdLen == 3) { handleBme280Init(params); }
  else if (strncmp(cmd, "BMR", cmdLen) == 0 && cmdLen == 3) { handleBme280Read(params); }
  // ── BH1750 Commands ──
  else if (strncmp(cmd, "BLI", cmdLen) == 0 && cmdLen == 3) { handleBh1750Init(params); }
  else if (strncmp(cmd, "BLR", cmdLen) == 0 && cmdLen == 3) { handleBh1750Read(params); }
  // ── MPU6050 Commands ──
  else if (strncmp(cmd, "MPI", cmdLen) == 0 && cmdLen == 3) { handleMpu6050Init(params); }
  else if (strncmp(cmd, "MPR", cmdLen) == 0 && cmdLen == 3) { handleMpu6050Read(params); }
  // ── TCS34725 Commands ──
  else if (strncmp(cmd, "TCI", cmdLen) == 0 && cmdLen == 3) { handleTcs34725Init(params); }
  else if (strncmp(cmd, "TCR", cmdLen) == 0 && cmdLen == 3) { handleTcs34725Read(params); }
  // ── INA219 Commands ──
  else if (strncmp(cmd, "INI", cmdLen) == 0 && cmdLen == 3) { handleIna219Init(params); }
  else if (strncmp(cmd, "INR", cmdLen) == 0 && cmdLen == 3) { handleIna219Read(params); }
  // ── GPIO Interrupt Commands ──
  else if (strncmp(cmd, "GINT", cmdLen) == 0 && cmdLen == 4) { handleInterruptAttach(params); }
  else if (strncmp(cmd, "GINTD", cmdLen) == 0 && cmdLen == 5) { handleInterruptDetach(params); }
  else if (strncmp(cmd, "GINTP", cmdLen) == 0 && cmdLen == 5) { handleInterruptPoll(); }
  // Buffered acquisition commands
  else if (strncmp(cmd, "SCFG", cmdLen) == 0 && cmdLen == 4) { handleSampleConfig(params); }
  else if (strncmp(cmd, "SRD", cmdLen) == 0 && cmdLen == 3) { handleSampleRead(params); }
  else if (strncmp(cmd, "SSTOP", cmdLen) == 0 && cmdLen == 5) { handleSampleStop(params); }
  // ── Watchdog Commands ──
  else if (strncmp(cmd, "WDI", cmdLen) == 0 && cmdLen == 3) { handleWatchdogInit(params); }
  else if (strncmp(cmd, "WDF", cmdLen) == 0 && cmdLen == 3) { handleWatchdogFeed(); }
  else if (strncmp(cmd, "WDD", cmdLen) == 0 && cmdLen == 3) { handleWatchdogDisable(); }
  // ── Deep Sleep Commands ──
  else if (strncmp(cmd, "DSL", cmdLen) == 0 && cmdLen == 3) { handleDeepSleep(params); }
  else if (strncmp(cmd, "DSLP", cmdLen) == 0 && cmdLen == 4) { handleDeepSleepPin(params); }
  // ── OTA Commands ──
  else if (strncmp(cmd, "OTAB", cmdLen) == 0 && cmdLen == 4) { handleOtaBegin(params); }
  else if (strncmp(cmd, "OTAS", cmdLen) == 0 && cmdLen == 4) { handleOtaStatus(); }
  // ── MQTT Commands ──
  else if (strncmp(cmd, "MQC", cmdLen) == 0 && cmdLen == 3) { handleMqttConnect(params); }
  else if (strncmp(cmd, "MQP", cmdLen) == 0 && cmdLen == 3) { handleMqttPublish(params); }
  else if (strncmp(cmd, "MQS", cmdLen) == 0 && cmdLen == 3) { handleMqttSubscribe(params); }
  else if (strncmp(cmd, "MQU", cmdLen) == 0 && cmdLen == 3) { handleMqttUnsubscribe(params); }
  else if (strncmp(cmd, "MQR", cmdLen) == 0 && cmdLen == 3) { handleMqttRead(); }
  else if (strncmp(cmd, "MQD", cmdLen) == 0 && cmdLen == 3) { handleMqttDisconnect(); }
  else {
    sendError("Unknown command");
  }
}

// ══════════════════════════════════════════════════════════════
//  GPIO HANDLERS
// ══════════════════════════════════════════════════════════════
void handlePinMode(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  int mode = getNextParam(ptr);

  if (pin < 0 || pin >= SOC_GPIO_PIN_COUNT) { sendError("Invalid pin"); return; }

  switch (mode) {
    case 0: pinMode(pin, INPUT); break;
    case 1: pinMode(pin, OUTPUT); break;
    case 2: pinMode(pin, INPUT_PULLUP); break;
    case 3: pinMode(pin, INPUT_PULLDOWN); break;
    case 4: break;
    default: sendError("Invalid mode"); return;
  }
  sendOK();
}

void handleDigitalWrite(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  int value = getNextParam(ptr);

  if (pin < 0 || pin >= SOC_GPIO_PIN_COUNT) { sendError("Invalid pin"); return; }

  digitalWrite(pin, value ? HIGH : LOW);
  sendOK();
}

void handleDigitalRead(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);

  if (pin < 0 || pin >= SOC_GPIO_PIN_COUNT) { sendError("Invalid pin"); return; }

  int value = digitalRead(pin);
  char buf[4];
  snprintf(buf, sizeof(buf), "%d", value);
  sendOK(buf);
}

void handleAnalogRead(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);

  if (pin < 0 || pin >= SOC_GPIO_PIN_COUNT) { sendError("Invalid pin"); return; }

  int value = analogRead(pin);
  char buf[8];
  snprintf(buf, sizeof(buf), "%d", value);
  sendOK(buf);
}

void handlePwmWrite(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  int duty = getNextParam(ptr);
  int freq = getNextParam(ptr);

  if (pin < 0 || pin >= SOC_GPIO_PIN_COUNT) { sendError("Invalid pin"); return; }
  if (duty < 0 || duty > 255) { sendError("Invalid duty"); return; }
  if (freq <= 0) freq = 5000;

  int channel = findOrCreatePwmChannel(pin);
  if (channel < 0) { sendError("No PWM channels available"); return; }

  ledcSetup(channel, freq, 8);
  ledcAttachPin(pin, channel);
  ledcWrite(channel, duty);
  sendOK();
}

// ══════════════════════════════════════════════════════════════
//  I2C HANDLERS
// ══════════════════════════════════════════════════════════════
void handleI2cScan(const char* params) {
  String result = "";
  int count = 0;

  for (byte addr = 1; addr < 127; addr++) {
    Wire.beginTransmission(addr);
    if (Wire.endTransmission() == 0) {
      if (count > 0) result += ",";
      result += String(addr);
      count++;
    }
  }

  sendOK(result.c_str());
}

void handleI2cWrite(const char* params) {
  const char* ptr = params;
  int addr = getNextParam(ptr);
  String hexData = getNextParamStr(ptr);

  if (addr < 1 || addr > 126) { sendError("Invalid I2C address"); return; }
  int len = hexData.length() / 2;
  if (hexData.length() % 2 != 0 || len < 1 || len > 128) { sendError("Invalid data length"); return; }
  uint8_t data[128];
  hexStringToBytes(hexData.c_str(), data, len);

  Wire.beginTransmission(addr);
  Wire.write(data, len);
  int err = Wire.endTransmission();

  if (err == 0) sendOK();
  else sendError("I2C write failed");
}

void handleI2cRead(const char* params) {
  const char* ptr = params;
  int addr = getNextParam(ptr);
  int len = getNextParam(ptr);

  if (addr < 1 || addr > 126) { sendError("Invalid I2C address"); return; }
  if (len < 1 || len > 128) { sendError("Invalid length"); return; }

  Wire.requestFrom(addr, len);
  String result = "";
  while (Wire.available()) {
    uint8_t b = Wire.read();
    char hex[3];
    snprintf(hex, sizeof(hex), "%02X", b);
    result += hex;
  }
  sendOK(result.c_str());
}

void handleI2cWriteReg(const char* params) {
  const char* ptr = params;
  int addr = getNextParam(ptr);
  int reg = getNextParam(ptr);
  String hexData = getNextParamStr(ptr);

  if (addr < 1 || addr > 126) { sendError("Invalid I2C address"); return; }
  int len = hexData.length() / 2;
  if (hexData.length() % 2 != 0 || len < 1 || len > 128) { sendError("Invalid data length"); return; }
  uint8_t data[128];
  hexStringToBytes(hexData.c_str(), data, len);

  Wire.beginTransmission(addr);
  Wire.write((uint8_t)reg);
  Wire.write(data, len);
  int err = Wire.endTransmission();

  if (err == 0) sendOK();
  else sendError("I2C register write failed");
}

void handleI2cReadReg(const char* params) {
  const char* ptr = params;
  int addr = getNextParam(ptr);
  int reg = getNextParam(ptr);
  int len = getNextParam(ptr);

  if (addr < 1 || addr > 126) { sendError("Invalid I2C address"); return; }
  if (len < 1 || len > 128) { sendError("Invalid length"); return; }

  Wire.beginTransmission(addr);
  Wire.write((uint8_t)reg);
  Wire.endTransmission(false);

  Wire.requestFrom(addr, len);
  String result = "";
  while (Wire.available()) {
    uint8_t b = Wire.read();
    char hex[3];
    snprintf(hex, sizeof(hex), "%02X", b);
    result += hex;
  }
  sendOK(result.c_str());
}

// ══════════════════════════════════════════════════════════════
//  SYSTEM HANDLERS
// ══════════════════════════════════════════════════════════════
void handlePing() {
  sendOK("PONG");
}

void handleInfo() {
  JsonDocument doc;
  doc["chip"] = ESP.getChipModel();
  doc["freq"] = ESP.getCpuFreqMHz();
  doc["heap"] = ESP.getFreeHeap();
  doc["flash"] = ESP.getFlashChipSize();
  doc["sdk"] = ESP.getSdkVersion();
  doc["auth"] = strlen(apiToken) > 0;

  if (wifiConnected) {
    doc["wifi_ip"] = WiFi.localIP().toString();
    doc["wifi_rssi"] = WiFi.RSSI();
    doc["wifi_ssid"] = WiFi.SSID();
  }

  String json;
  serializeJson(doc, json);
  sendOK(json.c_str());
}

void handleVersion() {
  sendOK(FIRMWARE_VERSION);
}

void handleReset() {
  sendOK("Resetting...");
  delay(100);
  ESP.restart();
}

// ══════════════════════════════════════════════════════════════
//  WiFi COMMAND HANDLERS
// ══════════════════════════════════════════════════════════════

// WCFG:SSID:PASSWORD — Configure and connect WiFi
void handleWifiConfig(const char* params) {
  const char* ptr = params;
  String ssid = getNextParamStr(ptr);
  String pass = String(ptr);  // everything after the first ':' so passwords may contain ':'

  applyWifiConfig(ssid, pass);
}

static int hexNibble(char c) {
  if (c >= '0' && c <= '9') return c - '0';
  if (c >= 'a' && c <= 'f') return c - 'a' + 10;
  if (c >= 'A' && c <= 'F') return c - 'A' + 10;
  return -1;
}

static bool hexToString(const String& hex, String& out) {
  if (hex.length() % 2 != 0) return false;
  out = "";
  for (unsigned int k = 0; k < hex.length(); k += 2) {
    int hi = hexNibble(hex[k]), lo = hexNibble(hex[k + 1]);
    if (hi < 0 || lo < 0) return false;
    out += (char)((hi << 4) | lo);
  }
  return true;
}

// WCFGX:<hex ssid>:<hex password> - same as WCFG but safe for any character in the name or password.
void handleWifiConfigHex(const char* params) {
  const char* ptr = params;
  String hexSsid = getNextParamStr(ptr);
  String hexPass = String(ptr);
  String ssid, pass;
  if (!hexToString(hexSsid, ssid) || !hexToString(hexPass, pass)) { sendError("Invalid hex"); return; }
  applyWifiConfig(ssid, pass);
}

static void applyWifiConfig(const String& ssid, const String& pass) {
  if (ssid.length() == 0 || ssid.length() > 32) {
    sendError("SSID must be 1-32 characters");
    return;
  }
  if (pass.length() > 63) {
    sendError("Password must be at most 63 characters");
    return;
  }

  // Keep the working network until the new one is proven: a typo in the password must not lock the board out of Wi-Fi.
  char previousSsid[sizeof(wifiSSID)];
  char previousPassword[sizeof(wifiPassword)];
  memcpy(previousSsid, wifiSSID, sizeof(previousSsid));
  memcpy(previousPassword, wifiPassword, sizeof(previousPassword));

  ssid.toCharArray(wifiSSID, sizeof(wifiSSID));
  pass.toCharArray(wifiPassword, sizeof(wifiPassword));

  if (wifiConnected) {
    WiFi.disconnect();
    stopNetworkServices();
  }

  connectWifi();

  if (wifiConnected) {
    saveWifiConfig();
    String ip = WiFi.localIP().toString();
    String result = ip + ":" + String(TCP_PORT);
    sendOK(result.c_str());
  } else {
    memcpy(wifiSSID, previousSsid, sizeof(wifiSSID));
    memcpy(wifiPassword, previousPassword, sizeof(wifiPassword));
    if (strlen(wifiSSID) > 0) {
      // go back to the saved network in the background
      WiFi.mode(WIFI_STA);
      WiFi.begin(wifiSSID, wifiPassword);
      wifiBootPending = true;
      wifiBootStartedAt = millis();
    }
    sendError("WiFi connection failed (the previous network was kept)");
  }
}

// WSTAT — Get WiFi status
void handleWifiStatus() {
  JsonDocument doc;
  doc["enabled"] = wifiEnabled;
  doc["connected"] = wifiConnected;

  if (wifiConnected) {
    doc["ssid"] = WiFi.SSID();
    doc["ip"] = WiFi.localIP().toString();
    doc["rssi"] = WiFi.RSSI();
    doc["port"] = TCP_PORT;
    doc["mac"] = WiFi.macAddress();
  }

  String json;
  serializeJson(doc, json);
  sendOK(json.c_str());
}

// WSCAN — Scan available WiFi networks
void handleWifiScan() {
  // A join attempt in progress (for example to a saved network that is not around) makes the scan fail and return nothing:
  // stop it for the scan and resume it afterwards.
  if (!wifiConnected) {
    wifiBootPending = false;
    WiFi.disconnect();
    delay(150);
  }
  WiFi.mode(WIFI_STA);
  int n = WiFi.scanNetworks();
  if (n < 0) {
    sendError("Wi-Fi scan failed");
    n = 0;
    if (!wifiConnected && strlen(wifiSSID) > 0) { WiFi.begin(wifiSSID, wifiPassword); wifiBootPending = true; wifiBootStartedAt = millis(); }
    return;
  }

  JsonDocument doc;
  JsonArray networks = doc["networks"].to<JsonArray>();

  for (int i = 0; i < n && i < 10; i++) {
    JsonObject net = networks.add<JsonObject>();
    net["ssid"] = WiFi.SSID(i);
    net["rssi"] = WiFi.RSSI(i);
    net["enc"] = WiFi.encryptionType(i) != WIFI_AUTH_OPEN;
  }

  WiFi.scanDelete();

  if (!wifiConnected && strlen(wifiSSID) > 0) {
    WiFi.begin(wifiSSID, wifiPassword);
    wifiBootPending = true;
    wifiBootStartedAt = millis();
  }

  String json;
  serializeJson(doc, json);
  sendOK(json.c_str());
}

// ══════════════════════════════════════════════════════════════
//  SPI HANDLERS
// ══════════════════════════════════════════════════════════════
void handleSpiConfig(const char* params) {
  const char* ptr = params;
  int speed = getNextParam(ptr);
  int mode = getNextParam(ptr);

  if (speed > 0) spiClockSpeed = speed;
  if (mode >= 0 && mode <= 3) {
    switch (mode) {
      case 0: spiMode = SPI_MODE0; break;
      case 1: spiMode = SPI_MODE1; break;
      case 2: spiMode = SPI_MODE2; break;
      case 3: spiMode = SPI_MODE3; break;
    }
  }
  sendOK();
}

void handleSpiTransfer(const char* params) {
  const char* ptr = params;
  int csPin = getNextParam(ptr);
  String hexData = getNextParamStr(ptr);

  int len = hexData.length() / 2;
  if (len == 0) { sendError("No data"); return; }

  uint8_t* txBuf = new uint8_t[len];
  uint8_t* rxBuf = new uint8_t[len];
  hexStringToBytes(hexData.c_str(), txBuf, len);

  pinMode(csPin, OUTPUT);
  digitalWrite(csPin, LOW);
  SPI.beginTransaction(SPISettings(spiClockSpeed, MSBFIRST, spiMode));
  for (int i = 0; i < len; i++) {
    rxBuf[i] = SPI.transfer(txBuf[i]);
  }
  SPI.endTransaction();
  digitalWrite(csPin, HIGH);

  String result = bytesToHexString(rxBuf, len);
  delete[] txBuf;
  delete[] rxBuf;
  sendOK(result.c_str());
}

void handleSpiWrite(const char* params) {
  const char* ptr = params;
  int csPin = getNextParam(ptr);
  String hexData = getNextParamStr(ptr);

  int len = hexData.length() / 2;
  if (len == 0) { sendError("No data"); return; }

  uint8_t* buf = new uint8_t[len];
  hexStringToBytes(hexData.c_str(), buf, len);

  pinMode(csPin, OUTPUT);
  digitalWrite(csPin, LOW);
  SPI.beginTransaction(SPISettings(spiClockSpeed, MSBFIRST, spiMode));
  SPI.transfer(buf, len);
  SPI.endTransaction();
  digitalWrite(csPin, HIGH);

  delete[] buf;
  sendOK();
}

void handleSpiRead(const char* params) {
  const char* ptr = params;
  int csPin = getNextParam(ptr);
  int len = getNextParam(ptr);

  if (len <= 0 || len > 256) { sendError("Invalid length"); return; }

  uint8_t* buf = new uint8_t[len];
  memset(buf, 0, len);

  pinMode(csPin, OUTPUT);
  digitalWrite(csPin, LOW);
  SPI.beginTransaction(SPISettings(spiClockSpeed, MSBFIRST, spiMode));
  for (int i = 0; i < len; i++) {
    buf[i] = SPI.transfer(0x00);
  }
  SPI.endTransaction();
  digitalWrite(csPin, HIGH);

  String result = bytesToHexString(buf, len);
  delete[] buf;
  sendOK(result.c_str());
}

// ══════════════════════════════════════════════════════════════
//  ONEWIRE HANDLERS
// ══════════════════════════════════════════════════════════════
void ensureOneWire(int pin) {
  if (owPin != pin || owBus == nullptr) {
    if (owBus) delete owBus;
    if (dallasSensors) delete dallasSensors;
    owBus = new OneWire(pin);
    dallasSensors = new DallasTemperature(owBus);
    dallasSensors->begin();
    owPin = pin;
  }
}

void handleOwScan(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);

  ensureOneWire(pin);

  uint8_t addr[8];
  String result = "";
  int count = 0;

  owBus->reset_search();
  while (owBus->search(addr)) {
    if (count > 0) result += ",";
    for (int i = 0; i < 8; i++) {
      char hex[3];
      snprintf(hex, sizeof(hex), "%02X", addr[i]);
      result += hex;
    }
    count++;
  }

  sendOK(result.c_str());
}

void handleOwRead(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  String addrHex = getNextParamStr(ptr);
  int len = getNextParam(ptr);

  ensureOneWire(pin);

  uint8_t addr[8];
  if (addrHex.length() >= 16) {
    hexStringToBytes(addrHex.c_str(), addr, 8);
  }

  owBus->reset();
  owBus->select(addr);

  uint8_t* buf = new uint8_t[len];
  for (int i = 0; i < len; i++) {
    buf[i] = owBus->read();
  }

  String result = bytesToHexString(buf, len);
  delete[] buf;
  sendOK(result.c_str());
}

void handleOwWrite(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  String addrHex = getNextParamStr(ptr);
  String dataHex = getNextParamStr(ptr);

  ensureOneWire(pin);

  uint8_t addr[8];
  if (addrHex.length() >= 16) {
    hexStringToBytes(addrHex.c_str(), addr, 8);
  }

  int len = dataHex.length() / 2;
  uint8_t* data = new uint8_t[len];
  hexStringToBytes(dataHex.c_str(), data, len);

  owBus->reset();
  owBus->select(addr);
  owBus->write_bytes(data, len);

  delete[] data;
  sendOK();
}

void handleOwTemp(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  String addrHex = getNextParamStr(ptr);

  ensureOneWire(pin);
  dallasSensors->requestTemperatures();

  float tempC;
  if (addrHex.length() >= 16) {
    uint8_t addr[8];
    hexStringToBytes(addrHex.c_str(), addr, 8);
    DeviceAddress devAddr;
    memcpy(devAddr, addr, 8);
    tempC = dallasSensors->getTempC(devAddr);
  } else {
    tempC = dallasSensors->getTempCByIndex(0);
  }

  if (tempC == DEVICE_DISCONNECTED_C) {
    sendError("Sensor not found");
  } else {
    char buf[16];
    snprintf(buf, sizeof(buf), "%.2f", tempC);
    sendOK(buf);
  }
}

// ══════════════════════════════════════════════════════════════
//  SERVO HANDLERS
// ══════════════════════════════════════════════════════════════
int findServo(int pin) {
  for (int i = 0; i < MAX_SERVOS; i++) {
    if (servoActive[i] && servoPins[i] == pin) return i;
  }
  return -1;
}

void handleServoAttach(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  int minUs = getNextParam(ptr);
  int maxUs = getNextParam(ptr);

  if (minUs <= 0) minUs = 500;
  if (maxUs <= 0) maxUs = 2500;

  int idx = findServo(pin);
  if (idx < 0) {
    if (servoCount >= MAX_SERVOS) { sendError("Max servos reached"); return; }
    idx = servoCount++;
    servoPins[idx] = pin;
    servoActive[idx] = true;
  }

  servos[idx].attach(pin, minUs, maxUs);
  sendOK();
}

void handleServoWrite(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  int angle = getNextParam(ptr);

  int idx = findServo(pin);
  if (idx < 0) { sendError("Servo not attached"); return; }

  angle = constrain(angle, 0, 180);
  servos[idx].write(angle);
  sendOK();
}

void handleServoRead(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);

  int idx = findServo(pin);
  if (idx < 0) { sendError("Servo not attached"); return; }

  int angle = servos[idx].read();
  char buf[8];
  snprintf(buf, sizeof(buf), "%d", angle);
  sendOK(buf);
}

void handleServoMicroseconds(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  int us = getNextParam(ptr);

  int idx = findServo(pin);
  if (idx < 0) { sendError("Servo not attached"); return; }

  servos[idx].writeMicroseconds(us);
  sendOK();
}

void handleServoDetach(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);

  int idx = findServo(pin);
  if (idx < 0) { sendError("Servo not attached"); return; }

  servos[idx].detach();
  servoActive[idx] = false;
  sendOK();
}

// ══════════════════════════════════════════════════════════════
//  NEOPIXEL HANDLERS
// ══════════════════════════════════════════════════════════════
void handleNeoInit(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  int count = getNextParam(ptr);

  if (count <= 0 || count > 1000) { sendError("Invalid LED count"); return; }

  if (neoStrip) delete neoStrip;

  neoStrip = new Adafruit_NeoPixel(count, pin, NEO_GRB + NEO_KHZ800);
  neoStrip->begin();
  neoStrip->clear();
  neoStrip->show();
  neoPin = pin;
  neoCount = count;
  sendOK();
}

void handleNeoSet(const char* params) {
  const char* ptr = params;
  int idx = getNextParam(ptr);
  int r = getNextParam(ptr);
  int g = getNextParam(ptr);
  int b = getNextParam(ptr);

  if (!neoStrip) { sendError("NeoPixel not initialized"); return; }
  if (idx < 0 || idx >= neoCount) { sendError("Invalid index"); return; }

  neoStrip->setPixelColor(idx, neoStrip->Color(r, g, b));
  sendOK();
}

void handleNeoAll(const char* params) {
  const char* ptr = params;
  int r = getNextParam(ptr);
  int g = getNextParam(ptr);
  int b = getNextParam(ptr);

  if (!neoStrip) { sendError("NeoPixel not initialized"); return; }

  uint32_t color = neoStrip->Color(r, g, b);
  for (int i = 0; i < neoCount; i++) {
    neoStrip->setPixelColor(i, color);
  }
  sendOK();
}

void handleNeoShow() {
  if (!neoStrip) { sendError("NeoPixel not initialized"); return; }
  neoStrip->show();
  sendOK();
}

void handleNeoClear() {
  if (!neoStrip) { sendError("NeoPixel not initialized"); return; }
  neoStrip->clear();
  neoStrip->show();
  sendOK();
}

void handleNeoBrightness(const char* params) {
  const char* ptr = params;
  int brightness = getNextParam(ptr);

  if (!neoStrip) { sendError("NeoPixel not initialized"); return; }

  brightness = constrain(brightness, 0, 255);
  neoStrip->setBrightness(brightness);
  sendOK();
}

void handleNeoRange(const char* params) {
  const char* ptr = params;
  int start = getNextParam(ptr);

  if (!neoStrip) { sendError("NeoPixel not initialized"); return; }

  // Read R,G,B triples until end of params
  while (*ptr) {
    int r = getNextParam(ptr);
    int g = getNextParam(ptr);
    int b = getNextParam(ptr);
    if (start >= 0 && start < neoCount) {
      neoStrip->setPixelColor(start, neoStrip->Color(r, g, b));
    }
    start++;
  }
  sendOK();
}

// ══════════════════════════════════════════════════════════════
//  TONE / BUZZER HANDLERS
// ══════════════════════════════════════════════════════════════
void handleTone(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  int freq = getNextParam(ptr);
  int durationMs = getNextParam(ptr);

  if (freq <= 0) { sendError("Invalid frequency"); return; }

  int ch = findOrCreatePwmChannel(pin);
  if (ch < 0) { sendError("No PWM channels"); return; }

  ledcSetup(ch, freq, 8);
  ledcAttachPin(pin, ch);
  ledcWrite(ch, 128); // 50% duty = square wave

  if (durationMs > 0) {
    delay(durationMs);
    ledcWrite(ch, 0);
  }
  sendOK();
}

void handleNoTone(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);

  int ch = findOrCreatePwmChannel(pin);
  if (ch >= 0) {
    ledcWrite(ch, 0);
  }
  sendOK();
}

// ══════════════════════════════════════════════════════════════
//  DHT SENSOR HANDLERS (uses raw GPIO timing on ESP32)
// ══════════════════════════════════════════════════════════════
// DHT read — bit-bang implementation for ESP32
void handleDhtInit(const char* params) {
  // DHT doesn't need init, but we accept for API consistency
  sendOK();
}

void handleDhtRead(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  int type = getNextParam(ptr); // 11=DHT11, 22=DHT22

  // DHT read bit-bang
  uint8_t data[5] = {0};

  // Pull low 20ms to start
  pinMode(pin, OUTPUT);
  digitalWrite(pin, LOW);
  delay(20);
  digitalWrite(pin, HIGH);
  delayMicroseconds(40);
  pinMode(pin, INPUT_PULLUP);

  // Wait for sensor response (low then high)
  unsigned long timeout = micros() + 1000;
  while (digitalRead(pin) == HIGH && micros() < timeout);
  timeout = micros() + 100;
  while (digitalRead(pin) == LOW && micros() < timeout);
  timeout = micros() + 100;
  while (digitalRead(pin) == HIGH && micros() < timeout);

  // Read 40 bits (5 bytes)
  for (int i = 0; i < 40; i++) {
    timeout = micros() + 100;
    while (digitalRead(pin) == LOW && micros() < timeout);
    unsigned long start = micros();
    timeout = micros() + 100;
    while (digitalRead(pin) == HIGH && micros() < timeout);
    unsigned long duration = micros() - start;

    data[i / 8] <<= 1;
    if (duration > 40) data[i / 8] |= 1;
  }

  // Verify checksum
  uint8_t checksum = data[0] + data[1] + data[2] + data[3];
  if (checksum != data[4]) {
    sendError("DHT checksum failed");
    return;
  }

  float temp, hum;
  if (type == 11) {
    hum = data[0];
    temp = data[2];
  } else { // DHT22/AM2302
    hum = ((data[0] << 8) | data[1]) * 0.1f;
    temp = (((data[2] & 0x7F) << 8) | data[3]) * 0.1f;
    if (data[2] & 0x80) temp = -temp;
  }

  char buf[32];
  snprintf(buf, sizeof(buf), "%.1f:%.1f", temp, hum);
  sendOK(buf);
}

// ══════════════════════════════════════════════════════════════
//  ULTRASONIC (HC-SR04) HANDLER
// ══════════════════════════════════════════════════════════════
void handleUltrasonicRead(const char* params) {
  const char* ptr = params;
  int trigPin = getNextParam(ptr);
  int echoPin = getNextParam(ptr);

  pinMode(trigPin, OUTPUT);
  pinMode(echoPin, INPUT);

  // Send 10µs trigger pulse
  digitalWrite(trigPin, LOW);
  delayMicroseconds(2);
  digitalWrite(trigPin, HIGH);
  delayMicroseconds(10);
  digitalWrite(trigPin, LOW);

  // Measure echo pulse duration
  unsigned long duration = pulseIn(echoPin, HIGH, 30000); // 30ms timeout

  if (duration == 0) {
    sendError("No echo");
    return;
  }

  // Speed of sound = 343m/s → distance = duration * 0.0343 / 2
  float distanceCm = duration * 0.0343f / 2.0f;

  char buf[16];
  snprintf(buf, sizeof(buf), "%.2f", distanceCm);
  sendOK(buf);
}

// ══════════════════════════════════════════════════════════════
//  MOTOR (H-BRIDGE) HANDLERS
// ══════════════════════════════════════════════════════════════
// MI:in1:in2:enPin → OK
void handleMotorInit(const char* params) {
  const char* ptr = params;
  int in1 = getNextParam(ptr);
  int in2 = getNextParam(ptr);
  int enPin = getNextParam(ptr);

  if (motorCount >= MAX_MOTORS) { sendError("Max motors reached"); return; }

  pinMode(in1, OUTPUT);
  pinMode(in2, OUTPUT);

  int idx = motorCount++;
  motors[idx].in1 = in1;
  motors[idx].in2 = in2;
  motors[idx].enPin = enPin;
  motors[idx].active = true;

  if (enPin >= 0) {
    int ch = findOrCreatePwmChannel(enPin);
    if (ch < 0) { sendError("No PWM channels"); return; }
    motors[idx].pwmChannel = ch;
    ledcSetup(ch, 5000, 8);
    ledcAttachPin(enPin, ch);
    ledcWrite(ch, 0);
  }

  digitalWrite(in1, LOW);
  digitalWrite(in2, LOW);
  sendOK();
}

// MS:in1:speed(-100..100) → OK
void handleMotorSpeed(const char* params) {
  const char* ptr = params;
  int in1 = getNextParam(ptr);
  int speed = getNextParam(ptr);

  // Find motor by in1 pin
  int idx = -1;
  for (int i = 0; i < motorCount; i++) {
    if (motors[i].active && motors[i].in1 == in1) { idx = i; break; }
  }
  if (idx < 0) { sendError("Motor not initialized"); return; }

  speed = constrain(speed, -100, 100);

  if (speed > 0) {
    digitalWrite(motors[idx].in1, HIGH);
    digitalWrite(motors[idx].in2, LOW);
  } else if (speed < 0) {
    digitalWrite(motors[idx].in1, LOW);
    digitalWrite(motors[idx].in2, HIGH);
  } else {
    digitalWrite(motors[idx].in1, LOW);
    digitalWrite(motors[idx].in2, LOW);
  }

  if (motors[idx].enPin >= 0 && motors[idx].pwmChannel >= 0) {
    int duty = map(abs(speed), 0, 100, 0, 255);
    ledcWrite(motors[idx].pwmChannel, duty);
  }

  sendOK();
}

// MX:in1 → OK (stop)
void handleMotorStop(const char* params) {
  const char* ptr = params;
  int in1 = getNextParam(ptr);

  int idx = -1;
  for (int i = 0; i < motorCount; i++) {
    if (motors[i].active && motors[i].in1 == in1) { idx = i; break; }
  }
  if (idx < 0) { sendError("Motor not initialized"); return; }

  digitalWrite(motors[idx].in1, LOW);
  digitalWrite(motors[idx].in2, LOW);
  if (motors[idx].enPin >= 0 && motors[idx].pwmChannel >= 0) {
    ledcWrite(motors[idx].pwmChannel, 0);
  }
  sendOK();
}

// ══════════════════════════════════════════════════════════════
//  STEPPER MOTOR HANDLERS (4-wire half-step)
// ══════════════════════════════════════════════════════════════
static const uint8_t STEPPER_SEQ[8][4] = {
  {1,0,0,0}, {1,1,0,0}, {0,1,0,0}, {0,1,1,0},
  {0,0,1,0}, {0,0,1,1}, {0,0,0,1}, {1,0,0,1}
};

int findStepper(int p1) {
  for (int i = 0; i < stepperCount; i++) {
    if (steppers[i].active && steppers[i].pins[0] == p1) return i;
  }
  return -1;
}

// STI:p1:p2:p3:p4:stepsPerRev → OK
void handleStepperInit(const char* params) {
  const char* ptr = params;
  int p1 = getNextParam(ptr);
  int p2 = getNextParam(ptr);
  int p3 = getNextParam(ptr);
  int p4 = getNextParam(ptr);
  int stepsPerRev = getNextParam(ptr);

  if (stepperCount >= MAX_STEPPERS) { sendError("Max steppers reached"); return; }
  if (stepsPerRev <= 0) stepsPerRev = 2048; // Default for 28BYJ-48

  int idx = stepperCount++;
  steppers[idx].pins[0] = p1;
  steppers[idx].pins[1] = p2;
  steppers[idx].pins[2] = p3;
  steppers[idx].pins[3] = p4;
  steppers[idx].stepsPerRev = stepsPerRev;
  steppers[idx].active = true;

  for (int i = 0; i < 4; i++) {
    pinMode(steppers[idx].pins[i], OUTPUT);
    digitalWrite(steppers[idx].pins[i], LOW);
  }
  sendOK();
}

// STS:p1:steps:speedRpm → OK
void handleStepperStep(const char* params) {
  const char* ptr = params;
  int p1 = getNextParam(ptr);
  int steps = getNextParam(ptr);
  int speedRpm = getNextParam(ptr);

  int idx = findStepper(p1);
  if (idx < 0) { sendError("Stepper not initialized"); return; }

  if (speedRpm <= 0) speedRpm = 10;

  // Calculate delay between steps in microseconds
  // delay = 60,000,000 / (stepsPerRev * rpm)
  unsigned long stepDelayUs = 60000000UL / ((unsigned long)steppers[idx].stepsPerRev * speedRpm);
  if (stepDelayUs < 800) stepDelayUs = 800; // Min ~800µs per step

  int direction = steps > 0 ? 1 : -1;
  int totalSteps = abs(steps);
  int seqIdx = 0;

  for (int s = 0; s < totalSteps; s++) {
    for (int p = 0; p < 4; p++) {
      digitalWrite(steppers[idx].pins[p], STEPPER_SEQ[seqIdx][p] ? HIGH : LOW);
    }
    seqIdx = (seqIdx + direction + 8) % 8;
    delayMicroseconds(stepDelayUs);
  }

  // Release coils to save power
  for (int p = 0; p < 4; p++) {
    digitalWrite(steppers[idx].pins[p], LOW);
  }
  sendOK();
}

// ══════════════════════════════════════════════════════════════
//  OLED (SSD1306) HANDLERS
// ══════════════════════════════════════════════════════════════
// OI:width:height:addr → OK
void handleOledInit(const char* params) {
  const char* ptr = params;
  int w = getNextParam(ptr);
  int h = getNextParam(ptr);
  int addr = getNextParam(ptr);

  if (w <= 0) w = 128;
  if (h <= 0) h = 64;
  if (addr <= 0) addr = 0x3C;

  if (oledDisplay) delete oledDisplay;
  oledDisplay = new Adafruit_SSD1306(w, h, &Wire, -1);
  oledWidth = w;
  oledHeight = h;

  if (!oledDisplay->begin(SSD1306_SWITCHCAPVCC, addr)) {
    delete oledDisplay;
    oledDisplay = nullptr;
    sendError("OLED init failed");
    return;
  }

  oledDisplay->clearDisplay();
  oledDisplay->setTextColor(SSD1306_WHITE);
  oledDisplay->setTextSize(1);
  oledDisplay->display();
  sendOK();
}

// OC → OK
void handleOledClear() {
  if (!oledDisplay) { sendError("OLED not initialized"); return; }
  oledDisplay->clearDisplay();
  sendOK();
}

// OT:x:y:size:text → OK
void handleOledText(const char* params) {
  if (!oledDisplay) { sendError("OLED not initialized"); return; }

  const char* ptr = params;
  int x = getNextParam(ptr);
  int y = getNextParam(ptr);
  int sz = getNextParam(ptr);
  String text = getNextParamStr(ptr);

  if (sz <= 0) sz = 1;

  oledDisplay->setTextSize(sz);
  oledDisplay->setCursor(x, y);
  oledDisplay->print(text);
  sendOK();
}

// OP:x:y:color → OK
void handleOledPixel(const char* params) {
  if (!oledDisplay) { sendError("OLED not initialized"); return; }

  const char* ptr = params;
  int x = getNextParam(ptr);
  int y = getNextParam(ptr);
  int color = getNextParam(ptr);

  oledDisplay->drawPixel(x, y, color ? SSD1306_WHITE : SSD1306_BLACK);
  sendOK();
}

// OL:x1:y1:x2:y2:color → OK
void handleOledLine(const char* params) {
  if (!oledDisplay) { sendError("OLED not initialized"); return; }

  const char* ptr = params;
  int x1 = getNextParam(ptr);
  int y1 = getNextParam(ptr);
  int x2 = getNextParam(ptr);
  int y2 = getNextParam(ptr);
  int color = getNextParam(ptr);

  oledDisplay->drawLine(x1, y1, x2, y2, color ? SSD1306_WHITE : SSD1306_BLACK);
  sendOK();
}

// OR:x:y:w:h:color:fill → OK
void handleOledRect(const char* params) {
  if (!oledDisplay) { sendError("OLED not initialized"); return; }

  const char* ptr = params;
  int x = getNextParam(ptr);
  int y = getNextParam(ptr);
  int w = getNextParam(ptr);
  int h = getNextParam(ptr);
  int color = getNextParam(ptr);
  int fill = getNextParam(ptr);

  uint16_t c = color ? SSD1306_WHITE : SSD1306_BLACK;
  if (fill) oledDisplay->fillRect(x, y, w, h, c);
  else oledDisplay->drawRect(x, y, w, h, c);
  sendOK();
}

// OE:cx:cy:r:color → OK
void handleOledCircle(const char* params) {
  if (!oledDisplay) { sendError("OLED not initialized"); return; }

  const char* ptr = params;
  int cx = getNextParam(ptr);
  int cy = getNextParam(ptr);
  int r = getNextParam(ptr);
  int color = getNextParam(ptr);

  oledDisplay->drawCircle(cx, cy, r, color ? SSD1306_WHITE : SSD1306_BLACK);
  sendOK();
}

// OF → OK (push buffer to display)
void handleOledFlush() {
  if (!oledDisplay) { sendError("OLED not initialized"); return; }
  oledDisplay->display();
  sendOK();
}

// OB:brightness → OK (0=dim, 255=bright)
void handleOledBrightness(const char* params) {
  if (!oledDisplay) { sendError("OLED not initialized"); return; }

  const char* ptr = params;
  int brightness = getNextParam(ptr);

  oledDisplay->ssd1306_command(SSD1306_SETCONTRAST);
  oledDisplay->ssd1306_command(constrain(brightness, 0, 255));
  sendOK();
}

// ══════════════════════════════════════════════════════════════
//  LCD (I2C HD44780) HANDLERS
// ══════════════════════════════════════════════════════════════
// LI:addr:cols:rows → OK
void handleLcdInit(const char* params) {
  const char* ptr = params;
  int addr = getNextParam(ptr);
  int cols = getNextParam(ptr);
  int rows = getNextParam(ptr);

  if (addr <= 0) addr = 0x27;
  if (cols <= 0) cols = 16;
  if (rows <= 0) rows = 2;

  if (lcdDisplay) delete lcdDisplay;
  lcdDisplay = new LiquidCrystal_I2C(addr, cols, rows);
  lcdCols = cols;
  lcdRows = rows;

  lcdDisplay->init();
  lcdDisplay->backlight();
  lcdDisplay->clear();
  sendOK();
}

// LC → OK
void handleLcdClear() {
  if (!lcdDisplay) { sendError("LCD not initialized"); return; }
  lcdDisplay->clear();
  sendOK();
}

// LT:row:col:text → OK
void handleLcdText(const char* params) {
  if (!lcdDisplay) { sendError("LCD not initialized"); return; }

  const char* ptr = params;
  int row = getNextParam(ptr);
  int col = getNextParam(ptr);
  String text = getNextParamStr(ptr);

  lcdDisplay->setCursor(col, row);
  lcdDisplay->print(text);
  sendOK();
}

// LB:0|1 → OK
void handleLcdBacklight(const char* params) {
  if (!lcdDisplay) { sendError("LCD not initialized"); return; }

  const char* ptr = params;
  int on = getNextParam(ptr);

  if (on) lcdDisplay->backlight();
  else lcdDisplay->noBacklight();
  sendOK();
}

// LK:row:col → OK
void handleLcdCursor(const char* params) {
  if (!lcdDisplay) { sendError("LCD not initialized"); return; }

  const char* ptr = params;
  int row = getNextParam(ptr);
  int col = getNextParam(ptr);

  lcdDisplay->setCursor(col, row);
  sendOK();
}

// ══════════════════════════════════════════════════════════════
//  BME280 HANDLERS
// ══════════════════════════════════════════════════════════════
void handleBme280Init(const char* params) {
  const char* ptr = params;
  int addr = getNextParam(ptr);
  if (addr <= 0) addr = 0x76;

  if (bme280) delete bme280;
  bme280 = new Adafruit_BME280();
  bme280Ready = bme280->begin(addr, &Wire);

  if (!bme280Ready) {
    delete bme280;
    bme280 = nullptr;
    sendError("BME280 not found");
    return;
  }
  sendOK();
}

void handleBme280Read(const char* params) {
  if (!bme280 || !bme280Ready) { sendError("BME280 not initialized"); return; }

  float temp = bme280->readTemperature();
  float humidity = bme280->readHumidity();
  float pressure = bme280->readPressure() / 100.0F; // Pa → hPa

  char buf[64];
  snprintf(buf, sizeof(buf), "%.2f,%.2f,%.2f", temp, humidity, pressure);
  sendOK(buf);
}

// ══════════════════════════════════════════════════════════════
//  BH1750 HANDLERS
// ══════════════════════════════════════════════════════════════
void handleBh1750Init(const char* params) {
  const char* ptr = params;
  int addr = getNextParam(ptr);
  if (addr <= 0) addr = 0x23;

  if (bh1750) delete bh1750;
  bh1750 = new BH1750((uint8_t)addr);
  bh1750Ready = bh1750->begin(BH1750::CONTINUOUS_HIGH_RES_MODE, (uint8_t)addr, &Wire);

  if (!bh1750Ready) {
    delete bh1750;
    bh1750 = nullptr;
    sendError("BH1750 not found");
    return;
  }
  sendOK();
}

void handleBh1750Read(const char* params) {
  if (!bh1750 || !bh1750Ready) { sendError("BH1750 not initialized"); return; }

  float lux = bh1750->readLightLevel();
  char buf[32];
  snprintf(buf, sizeof(buf), "%.2f", lux);
  sendOK(buf);
}

// ══════════════════════════════════════════════════════════════
//  MPU6050 HANDLERS
// ══════════════════════════════════════════════════════════════
void handleMpu6050Init(const char* params) {
  const char* ptr = params;
  int addr = getNextParam(ptr);
  if (addr <= 0) addr = 0x68;

  if (mpu6050) delete mpu6050;
  mpu6050 = new Adafruit_MPU6050();
  mpu6050Ready = mpu6050->begin((uint8_t)addr, &Wire);

  if (!mpu6050Ready) {
    delete mpu6050;
    mpu6050 = nullptr;
    sendError("MPU6050 not found");
    return;
  }

  // Set default ranges
  mpu6050->setAccelerometerRange(MPU6050_RANGE_8_G);
  mpu6050->setGyroRange(MPU6050_RANGE_500_DEG);
  mpu6050->setFilterBandwidth(MPU6050_BAND_21_HZ);

  sendOK();
}

void handleMpu6050Read(const char* params) {
  if (!mpu6050 || !mpu6050Ready) { sendError("MPU6050 not initialized"); return; }

  sensors_event_t a, g, temp;
  mpu6050->getEvent(&a, &g, &temp);

  // ax,ay,az (m/s²), gx,gy,gz (rad/s), temp (°C)
  char buf[128];
  snprintf(buf, sizeof(buf), "%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%.2f",
    a.acceleration.x, a.acceleration.y, a.acceleration.z,
    g.gyro.x, g.gyro.y, g.gyro.z,
    temp.temperature);
  sendOK(buf);
}

// ══════════════════════════════════════════════════════════════
//  TCS34725 HANDLERS
// ══════════════════════════════════════════════════════════════
void handleTcs34725Init(const char* params) {
  const char* ptr = params;
  int integrationTime = getNextParam(ptr);
  int gain = getNextParam(ptr);

  // Defaults: 50ms integration, 4x gain
  uint8_t it = TCS34725_INTEGRATIONTIME_50MS;
  if (integrationTime == 24) it = TCS34725_INTEGRATIONTIME_24MS;
  else if (integrationTime == 101) it = TCS34725_INTEGRATIONTIME_101MS;
  else if (integrationTime == 154) it = TCS34725_INTEGRATIONTIME_154MS;
  else if (integrationTime == 600) it = TCS34725_INTEGRATIONTIME_600MS;

  tcs34725Gain_t g = TCS34725_GAIN_4X;
  if (gain == 1) g = TCS34725_GAIN_1X;
  else if (gain == 16) g = TCS34725_GAIN_16X;
  else if (gain == 60) g = TCS34725_GAIN_60X;

  if (tcs34725) delete tcs34725;
  tcs34725 = new Adafruit_TCS34725(it, g);
  tcs34725Ready = tcs34725->begin();

  if (!tcs34725Ready) {
    delete tcs34725;
    tcs34725 = nullptr;
    sendError("TCS34725 not found");
    return;
  }
  sendOK();
}

void handleTcs34725Read(const char* params) {
  if (!tcs34725 || !tcs34725Ready) { sendError("TCS34725 not initialized"); return; }

  uint16_t r, g, b, c;
  tcs34725->getRawData(&r, &g, &b, &c);
  uint16_t colorTemp = tcs34725->calculateColorTemperature_dn40(r, g, b, c);
  uint16_t lux = tcs34725->calculateLux(r, g, b);

  // Normalize to 0-255
  float scale = (c > 0) ? 255.0f / c : 1.0f;
  uint8_t r8 = min(255, (int)(r * scale));
  uint8_t g8 = min(255, (int)(g * scale));
  uint8_t b8 = min(255, (int)(b * scale));

  char buf[64];
  snprintf(buf, sizeof(buf), "%d,%d,%d,%d,%d,%d", r8, g8, b8, c, colorTemp, lux);
  sendOK(buf);
}

// ══════════════════════════════════════════════════════════════
//  INA219 HANDLERS
// ══════════════════════════════════════════════════════════════
void handleIna219Init(const char* params) {
  const char* ptr = params;
  int addr = getNextParam(ptr);
  if (addr <= 0) addr = 0x40;

  if (ina219) delete ina219;
  ina219 = new Adafruit_INA219((uint8_t)addr);
  ina219Ready = ina219->begin();

  if (!ina219Ready) {
    delete ina219;
    ina219 = nullptr;
    sendError("INA219 not found");
    return;
  }
  sendOK();
}

void handleIna219Read(const char* params) {
  if (!ina219 || !ina219Ready) { sendError("INA219 not initialized"); return; }

  float busVoltage = ina219->getBusVoltage_V();
  float shuntVoltage = ina219->getShuntVoltage_mV();
  float current = ina219->getCurrent_mA();
  float power = ina219->getPower_mW();
  float loadVoltage = busVoltage + (shuntVoltage / 1000.0f);

  char buf[96];
  snprintf(buf, sizeof(buf), "%.4f,%.4f,%.4f,%.4f", current, loadVoltage, power, shuntVoltage);
  sendOK(buf);
}

// ══════════════════════════════════════════════════════════════
//  PHASE 7: GPIO INTERRUPTS
// ══════════════════════════════════════════════════════════════
// ============================================================================
//  PHASE 8: BUFFERED ACQUISITION
// ============================================================================
void handleSampleConfig(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  int analog = getNextParam(ptr);
  int mode = getNextParam(ptr);
  int sampleRateHz = getNextParam(ptr);
  int capacity = getNextParam(ptr);
  int backpressure = getNextParam(ptr);
  int batchSize = getNextParam(ptr);

  if (pin < 0 || pin >= SOC_GPIO_PIN_COUNT) { sendError("Invalid pin"); return; }
  if (mode < 0 || mode > 2) { sendError("Invalid sampling mode"); return; }
  if (sampleRateHz <= 0 || sampleRateHz > 10000) { sendError("Sample rate 1-10000 Hz"); return; }
  if (capacity <= 0 || capacity > MAX_SAMPLE_BUFFER_CAPACITY) { sendError("Buffer capacity 1-4096"); return; }
  if (backpressure < 0 || backpressure > 3) { sendError("Invalid backpressure"); return; }
  if (batchSize <= 0) { sendError("Invalid batch size"); return; }

  int slot = -1;
  for (int i = 0; i < MAX_SAMPLE_CHANNELS; i++) {
    if (sampleChannels[i].active && sampleChannels[i].pin == pin) {
      slot = i;
      break;
    }
  }
  if (slot < 0) {
    for (int i = 0; i < MAX_SAMPLE_CHANNELS; i++) {
      if (!sampleChannels[i].active) {
        slot = i;
        break;
      }
    }
  }
  if (slot < 0) { sendError("Max sample channels reached"); return; }

  if (sampleChannels[slot].buffer != nullptr) {
    free(sampleChannels[slot].buffer);
    sampleChannels[slot].buffer = nullptr;
  }

  SampleValue* buffer = (SampleValue*)malloc(sizeof(SampleValue) * capacity);
  if (buffer == nullptr) { sendError("Not enough heap for sample buffer"); return; }

  pinMode(pin, analog ? INPUT : INPUT_PULLUP);

  SampleChannel& channel = sampleChannels[slot];
  channel.active = true;
  channel.pin = pin;
  channel.analog = analog != 0;
  channel.mode = mode;
  channel.backpressure = backpressure;
  channel.sampleRateHz = sampleRateHz;
  channel.periodMicros = max(1UL, 1000000UL / (uint32_t)sampleRateHz);
  channel.lastSampleMicros = micros();
  channel.sequence = 0;
  channel.capacity = (uint16_t)capacity;
  channel.head = 0;
  channel.tail = 0;
  channel.count = 0;
  channel.dropped = 0;
  channel.buffer = buffer;

  char id[8];
  snprintf(id, sizeof(id), "S%d", slot);
  sendOK(id);
}

void handleSampleRead(const char* params) {
  const char* ptr = params;
  String channelId = getNextParamStr(ptr);
  int maxFrames = getNextParam(ptr);
  int slot = parseSampleChannelId(channelId);

  if (slot < 0 || slot >= MAX_SAMPLE_CHANNELS || !sampleChannels[slot].active) {
    sendError("Sample channel not found");
    return;
  }
  if (maxFrames <= 0) { sendError("Invalid frame count"); return; }

  SampleChannel& channel = sampleChannels[slot];
  if (channel.count == 0) {
    sendOK("NONE");
    return;
  }

  String result = "";
  int emitted = 0;
  while (channel.count > 0 && emitted < maxFrames) {
    SampleValue& sample = channel.buffer[channel.tail];
    if (emitted > 0) result += ";";
    result += String(sample.sequence);
    result += ",";
    result += String(sample.elapsedMicros);
    result += ",";
    result += String(sample.value);

    channel.tail = (channel.tail + 1) % channel.capacity;
    channel.count--;
    emitted++;
  }

  sendOK(result.c_str());
}

void handleSampleStop(const char* params) {
  String channelId = String(params);
  channelId.trim();
  int slot = parseSampleChannelId(channelId);

  if (slot < 0 || slot >= MAX_SAMPLE_CHANNELS || !sampleChannels[slot].active) {
    sendError("Sample channel not found");
    return;
  }

  SampleChannel& channel = sampleChannels[slot];
  if (channel.buffer != nullptr) {
    free(channel.buffer);
  }
  channel = {false, -1, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, nullptr};
  sendOK();
}

void serviceSampleChannels() {
  uint32_t now = micros();
  for (int i = 0; i < MAX_SAMPLE_CHANNELS; i++) {
    SampleChannel& channel = sampleChannels[i];
    if (!channel.active || channel.buffer == nullptr) continue;

    if ((uint32_t)(now - channel.lastSampleMicros) < channel.periodMicros) continue;

    channel.lastSampleMicros += channel.periodMicros;
    int value = channel.analog ? analogRead(channel.pin) : digitalRead(channel.pin);
    pushSample(channel, now, value);
  }
}

void pushSample(SampleChannel& channel, uint32_t elapsedMicros, int value) {
  if (channel.count == channel.capacity) {
    if (channel.backpressure == 1) {
      channel.dropped++;
      return;
    }

    channel.tail = (channel.tail + 1) % channel.capacity;
    channel.count--;
    channel.dropped++;
  }

  SampleValue& sample = channel.buffer[channel.head];
  sample.sequence = channel.sequence++;
  sample.elapsedMicros = elapsedMicros;
  sample.value = value;
  channel.head = (channel.head + 1) % channel.capacity;
  channel.count++;
}

int parseSampleChannelId(const String& channelId) {
  if (channelId.length() < 2 || channelId[0] != 'S') return -1;
  return channelId.substring(1).toInt();
}

void handleInterruptAttach(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  int edge = getNextParam(ptr); // 1=RISING, 2=FALLING, 3=CHANGE

  if (pin < 0 || pin >= SOC_GPIO_PIN_COUNT) { sendError("Invalid pin"); return; }
  if (edge < 1 || edge > 3) { sendError("Invalid edge (1=RISING,2=FALLING,3=CHANGE)"); return; }

  // Check if already attached
  for (int i = 0; i < intPinCount; i++) {
    if (intPins[i].pin == pin) {
      detachInterrupt(digitalPinToInterrupt(pin));
      intPins[i].edge = edge;
      intPins[i].count = 0;
      intPins[i].triggered = false;
      int mode = (edge == 1) ? RISING : (edge == 2) ? FALLING : CHANGE;
      attachInterrupt(digitalPinToInterrupt(pin), isrHandlers[i], mode);
      sendOK();
      return;
    }
  }

  if (intPinCount >= MAX_INTERRUPT_PINS) { sendError("Max interrupts reached"); return; }

  int idx = intPinCount;
  intPins[idx].pin = pin;
  intPins[idx].edge = edge;
  intPins[idx].count = 0;
  intPins[idx].lastTrigger = 0;
  intPins[idx].triggered = false;

  pinMode(pin, INPUT_PULLUP);
  int mode = (edge == 1) ? RISING : (edge == 2) ? FALLING : CHANGE;
  attachInterrupt(digitalPinToInterrupt(pin), isrHandlers[idx], mode);
  intPinCount++;
  sendOK();
}

void handleInterruptDetach(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);

  for (int i = 0; i < intPinCount; i++) {
    if (intPins[i].pin == pin) {
      detachInterrupt(digitalPinToInterrupt(pin));
      // Shift remaining entries
      for (int j = i; j < intPinCount - 1; j++) {
        intPins[j] = intPins[j + 1];
        // Re-attach with correct handler index
        detachInterrupt(digitalPinToInterrupt(intPins[j].pin));
        int mode = (intPins[j].edge == 1) ? RISING : (intPins[j].edge == 2) ? FALLING : CHANGE;
        attachInterrupt(digitalPinToInterrupt(intPins[j].pin), isrHandlers[j], mode);
      }
      intPinCount--;
      sendOK();
      return;
    }
  }
  sendError("Pin not attached");
}

void handleInterruptPoll() {
  // Returns: pin1:count1:edge1,pin2:count2:edge2,...
  String result = "";
  bool first = true;
  for (int i = 0; i < intPinCount; i++) {
    if (intPins[i].triggered) {
      if (!first) result += ",";
      result += String(intPins[i].pin) + ":" + String(intPins[i].count) + ":" + String(intPins[i].edge);
      intPins[i].triggered = false;
      intPins[i].count = 0;
      first = false;
    }
  }
  if (result.length() == 0) result = "NONE";
  sendOK(result.c_str());
}

// ══════════════════════════════════════════════════════════════
//  PHASE 7: WATCHDOG TIMER
// ══════════════════════════════════════════════════════════════
void handleWatchdogInit(const char* params) {
  const char* ptr = params;
  int timeoutMs = getNextParam(ptr);
  if (timeoutMs < 1000 || timeoutMs > 120000) { sendError("Timeout 1000-120000ms"); return; }

  int timeoutSec = timeoutMs / 1000;
  if (wdtEnabled) {
    esp_task_wdt_delete(NULL);
    esp_task_wdt_deinit();
  }
  esp_err_t err = esp_task_wdt_init(timeoutSec, true);
  if (err != ESP_OK) { sendError("WDT init failed"); return; }
  esp_task_wdt_add(NULL);
  wdtEnabled = true;
  sendOK();
}

void handleWatchdogFeed() {
  if (!wdtEnabled) { sendError("WDT not enabled"); return; }
  esp_task_wdt_reset();
  sendOK();
}

void handleWatchdogDisable() {
  if (!wdtEnabled) { sendError("WDT not enabled"); return; }
  esp_task_wdt_delete(NULL);
  esp_task_wdt_deinit();
  wdtEnabled = false;
  sendOK();
}

// ══════════════════════════════════════════════════════════════
//  PHASE 7: DEEP SLEEP
// ══════════════════════════════════════════════════════════════
void handleDeepSleep(const char* params) {
  const char* ptr = params;
  int seconds = getNextParam(ptr);
  if (seconds < 1 || seconds > 86400) { sendError("Seconds 1-86400"); return; }

  sendOK("SLEEPING");
  delay(100); // Give time for response to be sent
  esp_sleep_enable_timer_wakeup((uint64_t)seconds * 1000000ULL);
  esp_deep_sleep_start();
}

void handleDeepSleepPin(const char* params) {
  const char* ptr = params;
  int pin = getNextParam(ptr);
  int level = getNextParam(ptr); // 0=LOW, 1=HIGH

  if (pin < 0 || pin >= SOC_GPIO_PIN_COUNT) { sendError("Invalid pin"); return; }

  // Only RTC GPIOs can wake (ESP32: 0,2,4,12-15,25-27,32-39; ESP32-C3: 0-5)
  uint64_t mask = 1ULL << pin;
#if SOC_PM_SUPPORT_EXT1_WAKEUP
  if (esp_sleep_enable_ext1_wakeup(mask, level ? ESP_EXT1_WAKEUP_ANY_HIGH : ESP_EXT1_WAKEUP_ALL_LOW) != ESP_OK) {
    sendError("This pin cannot wake the chip");
    return;
  }
#elif SOC_GPIO_SUPPORT_DEEPSLEEP_WAKEUP
  if (esp_deep_sleep_enable_gpio_wakeup(mask, level ? ESP_GPIO_WAKEUP_GPIO_HIGH : ESP_GPIO_WAKEUP_GPIO_LOW) != ESP_OK) {
    sendError("This pin cannot wake the chip");
    return;
  }
#else
  sendError("Wake-up by pin is not supported on this chip");
  return;
#endif

  sendOK("SLEEPING");
  delay(100);
  esp_deep_sleep_start();
}

// ══════════════════════════════════════════════════════════════
//  PHASE 7: OTA UPDATE
// ══════════════════════════════════════════════════════════════
void handleOtaBegin(const char* params) {
  if (!wifiConnected) { sendError("WiFi not connected"); return; }

  String url = String(params);
  url.trim();
  if (url.length() == 0) { sendError("URL required"); return; }
  if (!url.startsWith("http://") && !url.startsWith("https://")) { sendError("Only http(s) URLs are accepted"); return; }

  otaStatus = "downloading";
  otaProgress = 0;

  HTTPClient http;
  http.setConnectTimeout(8000);
  http.setTimeout(8000);
  http.begin(url);
  int httpCode = http.GET();

  if (httpCode != HTTP_CODE_OK) {
    otaStatus = "error";
    char buf[64];
    snprintf(buf, sizeof(buf), "HTTP %d", httpCode);
    sendError(buf);
    http.end();
    return;
  }

  int contentLength = http.getSize();
  if (contentLength <= 0) {
    otaStatus = "error";
    sendError("Invalid content length");
    http.end();
    return;
  }

  if (!Update.begin(contentLength)) {
    otaStatus = "error";
    sendError("Not enough space");
    http.end();
    return;
  }

  WiFiClient* stream = http.getStreamPtr();
  otaStatus = "flashing";

  uint8_t otaBuf[1024];
  int written = 0;
  unsigned long lastProgressAt = millis();
  bool failed = false;
  while (http.connected() && written < contentLength) {
    int available = stream->available();
    if (available > 0) {
      int readBytes = stream->readBytes(otaBuf, min(available, (int)sizeof(otaBuf)));
      if (readBytes <= 0 || Update.write(otaBuf, readBytes) != (size_t)readBytes) { failed = true; break; }
      written += readBytes;
      otaProgress = (written * 100) / contentLength;
      lastProgressAt = millis();
    } else if (millis() - lastProgressAt > 15000) {
      failed = true;  // the server stalled: do not block the board forever
      break;
    }
    delay(1);
  }

  if (failed || written != contentLength) {
    Update.abort();
    otaStatus = "error";
    sendError("OTA download failed");
    http.end();
    return;
  }

  if (Update.end(true) && Update.isFinished()) {
    otaStatus = "complete";
    sendOK("OTA_COMPLETE");
    delay(500);
    ESP.restart();
  } else {
    otaStatus = "error";
    sendError("OTA failed");
  }
  http.end();
}

void handleOtaStatus() {
  char buf[64];
  snprintf(buf, sizeof(buf), "%s,%d", otaStatus.c_str(), otaProgress);
  sendOK(buf);
}

// ══════════════════════════════════════════════════════════════
//  PHASE 7: MQTT CLIENT
// ══════════════════════════════════════════════════════════════
void handleMqttConnect(const char* params) {
  if (!wifiConnected) { sendError("WiFi not connected"); return; }

  const char* ptr = params;
  String broker = getNextParamStr(ptr);
  int port = getNextParam(ptr);
  String clientId = getNextParamStr(ptr);

  if (broker.length() == 0) { sendError("Broker required"); return; }
  if (port <= 0) port = 1883;
  if (clientId.length() == 0) clientId = "codebridge_" + String(random(1000, 9999));

  mqttClient.setServer(broker.c_str(), port);
  mqttClient.setCallback(mqttCallback);
  mqttClient.setBufferSize(1024);

  if (mqttClient.connect(clientId.c_str())) {
    mqttConnected = true;
    sendOK();
  } else {
    mqttConnected = false;
    char buf[32];
    snprintf(buf, sizeof(buf), "MQTT state: %d", mqttClient.state());
    sendError(buf);
  }
}

void handleMqttPublish(const char* params) {
  if (!mqttConnected || !mqttClient.connected()) { sendError("MQTT not connected"); return; }

  const char* ptr = params;
  String topic = getNextParamStr(ptr);
  String message = String(ptr); // Rest is the message

  if (topic.length() == 0) { sendError("Topic required"); return; }

  if (mqttClient.publish(topic.c_str(), message.c_str())) {
    sendOK();
  } else {
    sendError("Publish failed");
  }
}

void handleMqttSubscribe(const char* params) {
  if (!mqttConnected || !mqttClient.connected()) { sendError("MQTT not connected"); return; }

  String topic = String(params);
  topic.trim();
  if (topic.length() == 0) { sendError("Topic required"); return; }

  if (mqttClient.subscribe(topic.c_str())) {
    sendOK();
  } else {
    sendError("Subscribe failed");
  }
}

void handleMqttUnsubscribe(const char* params) {
  if (!mqttConnected || !mqttClient.connected()) { sendError("MQTT not connected"); return; }

  String topic = String(params);
  topic.trim();
  if (topic.length() == 0) { sendError("Topic required"); return; }

  if (mqttClient.unsubscribe(topic.c_str())) {
    sendOK();
  } else {
    sendError("Unsubscribe failed");
  }
}

void handleMqttRead() {
  if (!mqttConnected) { sendError("MQTT not connected"); return; }

  // Process any pending messages
  mqttClient.loop();

  if (mqttMsgHead == mqttMsgTail) {
    sendOK("NONE");
    return;
  }

  // Return one message from the queue
  MqttMsg& msg = mqttMsgQueue[mqttMsgTail];
  String result = msg.topic + "|" + msg.payload;
  mqttMsgTail = (mqttMsgTail + 1) % MQTT_MSG_QUEUE_SIZE;
  sendOK(result.c_str());
}

void handleMqttDisconnect() {
  if (mqttConnected) {
    mqttClient.disconnect();
    mqttConnected = false;
    mqttMsgHead = 0;
    mqttMsgTail = 0;
  }
  sendOK();
}

// ══════════════════════════════════════════════════════════════
//  RESPONSE ROUTING (Serial or TCP)
// ══════════════════════════════════════════════════════════════
void sendResponse(const char* response) {
  if (activeResponseTarget == -2) {            // relayed over ESP-NOW: keep the answer for the reply frame
    if (meshReply.length()) meshReply += "\n";
    meshReply += response;
    return;
  }
  if (activeResponseTarget < 0) {
    Serial.println(response);
  } else {
    int idx = activeResponseTarget;
    if (idx < MAX_TCP_CLIENTS && tcpClients[idx] && tcpClients[idx].connected()) {
      tcpClients[idx].println(response);
    }
  }
}

void sendOK(const char* data) {
  String response;
  if (strlen(data) > 0) {
    response = "OK:" + String(data);
  } else {
    response = "OK";
  }
  sendResponse(response.c_str());
}

void sendError(const char* msg) {
  String response = "ERR:" + String(msg);
  sendResponse(response.c_str());
}

// ══════════════════════════════════════════════════════════════
//  UTILITIES
// ══════════════════════════════════════════════════════════════
int getNextParam(const char* &ptr) {
  int value = atoi(ptr);
  const char* next = strchr(ptr, ':');
  if (next) ptr = next + 1;
  else ptr = ptr + strlen(ptr);
  return value;
}

String getNextParamStr(const char* &ptr) {
  const char* next = strchr(ptr, ':');
  String result;
  if (next) {
    result = String(ptr).substring(0, next - ptr);
    ptr = next + 1;
  } else {
    result = String(ptr);
    ptr = ptr + strlen(ptr);
  }
  return result;
}

int findOrCreatePwmChannel(int pin) {
  for (int i = 0; i < MAX_PWM_CHANNELS; i++) {
    if (pwmChannels[i].active && pwmChannels[i].pin == pin) {
      return pwmChannels[i].channel;
    }
  }

  if (nextPwmChannel >= MAX_PWM_CHANNELS) return -1;

  int ch = nextPwmChannel++;
  pwmChannels[ch].pin = pin;
  pwmChannels[ch].active = true;
  return ch;
}

uint8_t hexCharToNibble(char c) {
  if (c >= '0' && c <= '9') return c - '0';
  if (c >= 'A' && c <= 'F') return c - 'A' + 10;
  if (c >= 'a' && c <= 'f') return c - 'a' + 10;
  return 0;
}

void hexStringToBytes(const char* hex, uint8_t* bytes, int len) {
  for (int i = 0; i < len; i++) {
    bytes[i] = (hexCharToNibble(hex[i*2]) << 4) | hexCharToNibble(hex[i*2+1]);
  }
}

String bytesToHexString(const uint8_t* data, int len) {
  String result = "";
  result.reserve(len * 2);
  for (int i = 0; i < len; i++) {
    char hex[3];
    snprintf(hex, sizeof(hex), "%02X", data[i]);
    result += hex;
  }
  return result;
}
