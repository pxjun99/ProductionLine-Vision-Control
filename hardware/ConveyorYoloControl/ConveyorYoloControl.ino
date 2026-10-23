// Arduino Mega 2560 only. PUL D5 (Timer3), DIR D6, NeoPixel D7.
// DHT11 D30, MQ-9 module AO A0, positional servo D11, NC E-stop D12/GND.
// Buzzer D8, red LED D40, green LED D42 (gas alarm + YOLO result lamps).
// E-stop is latched in firmware. A physical power-cut circuit is still required
// for an actual emergency stop; this sketch is not a safety controller.
#include <string.h>
#include <stdlib.h>
#include <math.h>
#include <avr/interrupt.h>
#include "Adafruit_NeoPixel.h"
#include "DHT.h"
#include "Servo.h"

const int PUL_PIN = 5, DIR_PIN = 6, LED_PIN = 7;
const int DHT_PIN = 30, GAS_PIN = A0, SERVO_PIN = 11, ESTOP_PIN = 12;
const int LED_COUNT = 24;
// 경보: 가스 A0 600 이상 또는 온도 31℃ 이상이면 빨간 LED 깜빡임 + 부저 200Hz 0.5초 울림/0.5초 쉼 반복
// 해제: 가스 580 미만이면서 온도 30.5℃ 미만(경계값에서 켜졌다 꺼졌다 반복 방지)
// 결과 표시: PC가 RESULT 1(불량)이면 빨강, RESULT 0(정상)이면 초록을 2초 동안 켬. 가스 경보 중에는 경보가 우선
const int BUZZER_PIN = 8, RED_LED_PIN = 40, GREEN_LED_PIN = 42;
const int GAS_ALARM_ON = 600, GAS_ALARM_OFF = 580;
const float TEMP_ALARM_ON = 31.0, TEMP_ALARM_OFF = 30.5;
const unsigned int BUZZER_HZ = 200;
const unsigned long ALARM_TOGGLE_MS = 500, GAS_CHECK_MS = 100, RESULT_LAMP_MS = 2000;
const byte LED_BRIGHTNESS = 64;
const unsigned int PULSE_DELAY = 100;
const unsigned int BOOST_PULSE_TICKS = 100; // Timer3 0.5us tick: 50us half-period, 2x normal pulse frequency.
const byte NORMAL_MOTION_RATE = 100, BOOST_MOTION_RATE = 200;
const unsigned long BOOST_DURATION_MS = 3000;
const unsigned long PC_TIMEOUT_MS = 3000;
const unsigned long GAS_WARMUP_MS = 60000; // display unconfirmed during initial warmup
const unsigned long CLIMATE_INTERVAL_MS = 2000;
const byte CLIMATE_FAILURES_BEFORE_RESET = 3;
Adafruit_NeoPixel inspectionLight(LED_COUNT, LED_PIN, NEO_GRB + NEO_KHZ800);
DHT climate(DHT_PIN, DHT11);
Servo sorter;
bool lightOn = true;
volatile bool conveyorRunning = false;
volatile bool emergencyStopped = false;
bool emergencyHandled = false;
unsigned long lastPcMessage = 0, lastClimateRead = 0, switchClosedSince = 0;
float temperatureCelsius = NAN, humidityPercent = NAN;
byte climateReadFailures = 0;
bool gasAlarm = false, gasHigh = false, tempHigh = false, alarmPhaseOn = false;
unsigned long lastGasCheck = 0, alarmToggledAt = 0;
char resultLamp = 0; // 0 = 꺼짐, 'N' = 정상(초록), 'D' = 불량(빨강)
unsigned long resultLampAt = 0;
int sorterAngle = 0;
bool sorting = false;
volatile bool speedBoosted = false;
unsigned long boostEnds = 0;
// Commanded distance units: normal=100 units/ms, boost=200, matching Timer3 periods.
// Store speed changes so a camera frame's age includes any earlier acceleration.
struct MotionPoint { unsigned long at, position; byte rate; };
MotionPoint motionHistory[64];
byte motionHead = 0, motionCount = 0, motionRate = 0;
unsigned long motionPosition = 0, motionAt = 0;
void advanceMotion(unsigned long now) {
  if ((long)(now - motionAt) < 0) return; // A cached loop timestamp must not rewind a newer speed-change timestamp.
  motionPosition += (now - motionAt) * motionRate;
  motionAt = now;
}
void setMotionRate(byte rate) {
  unsigned long now = millis(); advanceMotion(now);
  if (!motionCount || rate != motionRate) {
    motionHistory[motionHead] = {now, motionPosition, rate};
    motionHead = (motionHead + 1) % 64;
    if (motionCount < 64) ++motionCount;
  }
  motionRate = rate;
}
bool positionAtAge(unsigned long age, unsigned long* position) {
  uint8_t saved = SREG; cli();
  unsigned long now = millis(), at = now - age;
  for (byte i = 0; i < motionCount; ++i) {
    MotionPoint point = motionHistory[(motionHead + 63 - i) % 64];
    if ((long)(at - point.at) >= 0) {
      *position = point.position + (at - point.at) * point.rate; SREG = saved; return true;
    }
  }
  SREG = saved; return false;
}
unsigned long sortingEnds = 0;
struct SortJob { unsigned long starts, ends, target; };
SortJob jobs[8];
byte jobHead = 0, jobCount = 0;
char recentIds[16][33] = {};
byte recentNext = 0;
char commandBuffer[96];
byte commandLength = 0;
bool commandOverflow = false;

void stopPulseOutput() {
  uint8_t saved = SREG; cli();
  TCCR3A = 0; TCCR3B = 0; TIMSK3 = 0;
  setMotionRate(0); speedBoosted = false; boostEnds = 0;
  conveyorRunning = false;
  digitalWrite(PUL_PIN, HIGH);
  SREG = saved;
}

bool applySpeed(bool boosted) {
  uint8_t saved = SREG; cli();
  if (!conveyorRunning || emergencyStopped || (PINB & _BV(PB6))) { SREG = saved; return false; }
  setMotionRate(boosted ? BOOST_MOTION_RATE : NORMAL_MOTION_RATE);
  speedBoosted = boosted;
  TCNT3 = 0;
  OCR3A = boosted ? BOOST_PULSE_TICKS - 1 : (F_CPU / 8000000UL) * PULSE_DELAY - 1;
  SREG = saved;
  return true;
}

// Mega D12 is PB6/PCINT6. React without PC/USB polling.
ISR(PCINT0_vect) {
  if (PINB & _BV(PB6)) { emergencyStopped = true; stopPulseOutput(); }
}

void homeSorter() {
  if (emergencyStopped) return;
  if (!sorter.attached()) { sorter.write(0); sorter.attach(SERVO_PIN); }
  sorter.write(0); sorterAngle = 0;
}

void cancelSorting(bool emergency) {
  jobCount = 0; jobHead = 0; sorting = false;
  if (emergency) sorter.detach();
  else homeSorter();
}

void conveyorStop() { stopPulseOutput(); cancelSorting(emergencyStopped); }

bool conveyorForward() {
  uint8_t saved = SREG; cli();
  if (emergencyStopped || (PINB & _BV(PB6))) {
    emergencyStopped = true; stopPulseOutput(); SREG = saved; return false;
  }
  digitalWrite(DIR_PIN, LOW);
  // Hardware toggling keeps pulses stable while reading DHT/serial/LED data.
  if (!conveyorRunning) {
    setMotionRate(NORMAL_MOTION_RATE); speedBoosted = false;
    TCNT3 = 0;
    OCR3A = (F_CPU / 8000000UL) * PULSE_DELAY - 1;
    TIMSK3 = 0;
    TCCR3A = _BV(COM3A0);
    TCCR3B = _BV(WGM32) | _BV(CS31);
    conveyorRunning = true;
  }
  SREG = saved;
  return true;
}

void printState() { Serial.println(conveyorRunning ? "RUNNING" : "STOPPED"); }
void printLightState() { Serial.println(lightOn ? "LED_ON" : "LED_OFF"); }
void setLight(bool enabled) {
  lightOn = enabled;
  inspectionLight.fill(enabled ? inspectionLight.Color(255, 255, 255) : 0);
  inspectionLight.show();
}

void refreshClimate() {
  if ((unsigned long)(millis() - lastClimateRead) < CLIMATE_INTERVAL_MS) return;
  lastClimateRead = millis();

  // Perform one physical DHT11 transaction, then read temperature from the
  // same cached packet. Keep the last valid sample when one read is disturbed
  // by motor/servo noise instead of erasing the graph with null values.
  float humidity = climate.readHumidity(true);
  float temperature = climate.readTemperature(false);
  if (!isnan(humidity) && !isnan(temperature)) {
    humidityPercent = humidity;
    temperatureCelsius = temperature;
    climateReadFailures = 0;
    return;
  }

  if (climateReadFailures < 255) ++climateReadFailures;
  if (climateReadFailures >= CLIMATE_FAILURES_BEFORE_RESET) {
    // Re-arm D30 after repeated timeouts. A persistent failure still means the
    // DHT11 DATA/VCC/GND wiring or power must be checked.
    climate.begin(65);
    climateReadFailures = 0;
  }
}

void showResultLamp() {
  digitalWrite(RED_LED_PIN, resultLamp == 'D' ? HIGH : LOW);
  digitalWrite(GREEN_LED_PIN, resultLamp == 'N' ? HIGH : LOW);
}

// delay() 없이 시간만 비교하므로 벨트·서보·긴급 정지·시리얼 처리를 막지 않습니다.
void updateGasAlarm(unsigned long now) {
  if ((unsigned long)(now - lastGasCheck) >= GAS_CHECK_MS) {
    lastGasCheck = now;
    int gas = analogRead(GAS_PIN);
    if (gas >= GAS_ALARM_ON) gasHigh = true; else if (gas < GAS_ALARM_OFF) gasHigh = false;
    // 온도를 못 읽은 동안(NAN)은 직전 판단을 유지합니다.
    if (!isnan(temperatureCelsius)) {
      if (temperatureCelsius >= TEMP_ALARM_ON) tempHigh = true; else if (temperatureCelsius < TEMP_ALARM_OFF) tempHigh = false;
    }
    bool alarm = gasHigh || tempHigh;
    if (!gasAlarm && alarm) {
      gasAlarm = true; alarmPhaseOn = false; alarmToggledAt = now - ALARM_TOGGLE_MS;
      digitalWrite(GREEN_LED_PIN, LOW);
    } else if (gasAlarm && !alarm) {
      gasAlarm = false; noTone(BUZZER_PIN); showResultLamp();
    }
  }
  if (gasAlarm && (unsigned long)(now - alarmToggledAt) >= ALARM_TOGGLE_MS) {
    alarmToggledAt = now; alarmPhaseOn = !alarmPhaseOn;
    if (alarmPhaseOn) { tone(BUZZER_PIN, BUZZER_HZ); digitalWrite(RED_LED_PIN, HIGH); }
    else { noTone(BUZZER_PIN); digitalWrite(RED_LED_PIN, LOW); }
  }
}

void printStatus() {
  // Read only after the complete STATUS packet is received, before its reply.
  // The PC serializes requests and waits for this reply. Background DHT reads
  // disable AVR interrupts and can drop incoming UART bytes mid-command.
  refreshClimate();
  Serial.print("HW {\"connected\":true,\"temperatureCelsius\":");
  if (isnan(temperatureCelsius)) Serial.print("null"); else Serial.print(temperatureCelsius, 1);
  Serial.print(",\"humidityPercent\":");
  if (isnan(humidityPercent)) Serial.print("null"); else Serial.print(humidityPercent, 1);
  // AO is an uncalibrated raw input. Never invent a gas alarm threshold or ppm.
  Serial.print(",\"gasDetected\":null,\"gasAnalogValue\":"); Serial.print(analogRead(GAS_PIN));
  Serial.print(",\"gasWarmingUp\":"); Serial.print(millis() < GAS_WARMUP_MS ? "true" : "false");
  Serial.print(",\"emergencyStopped\":"); Serial.print(emergencyStopped ? "true" : "false");
  Serial.print(",\"beltRunning\":"); Serial.print(conveyorRunning ? "true" : "false");
  Serial.print(",\"speedBoosted\":"); Serial.print(speedBoosted ? "true" : "false");
  Serial.print(",\"sorterAngle\":"); Serial.print(sorterAngle);
  Serial.print(",\"sorting\":"); Serial.print((sorting || jobCount > 0) ? "true" : "false");
  // 긴급 정지 버튼이 원위치돼 0.2초 이상 지났는지(= RESET_ESTOP을 받을 수 있는지). 앱이 [잠금 해제] 버튼 활성화에 사용
  Serial.print(",\"emergencySwitchReady\":");
  Serial.print(digitalRead(ESTOP_PIN) == LOW && switchClosedSince && millis() - switchClosedSince >= 200 ? "true" : "false");
  Serial.println("}");
}

bool readBoundedNumber(char* text, unsigned long maximum, unsigned long* result) {
  if (!text || !*text) return false;
  for (char* p = text; *p; ++p) if (*p < '0' || *p > '9') return false;
  char* end; unsigned long value = strtoul(text, &end, 10);
  if (*end || value > maximum) return false;
  *result = value; return true;
}

void queueSort(char* parameters) {
  if (emergencyStopped) { Serial.println("ERROR EMERGENCY_STOP"); return; }
  if (!conveyorRunning) { Serial.println("ERROR BELT_NOT_RUNNING"); return; }
  char* state;
  char* id = strtok_r(parameters, " ", &state);
  char* delayText = strtok_r(NULL, " ", &state);
  char* holdText = strtok_r(NULL, " ", &state);
  unsigned long delayMs, holdMs;
  if (!id || strlen(id) != 32 || !readBoundedNumber(delayText, 60000, &delayMs) ||
      !readBoundedNumber(holdText, 60000, &holdMs) || holdMs < 200 || strtok_r(NULL, " ", &state)) {
    Serial.println("ERROR INVALID_SORT"); return;
  }
  for (byte i = 0; i < 32; ++i)
    if (!((id[i] >= '0' && id[i] <= '9') || (id[i] >= 'a' && id[i] <= 'f'))) {
      Serial.println("ERROR INVALID_SORT_ID"); return;
    }
  for (byte i = 0; i < 16; ++i) if (strcmp(recentIds[i], id) == 0) {
    Serial.print("SORT_QUEUED "); Serial.println(id); return;
  }
  if (jobCount >= 8) { Serial.println("ERROR SORT_QUEUE_FULL"); return; }
  unsigned long now = millis(), starts = now + delayMs;
  unsigned long previousEnd = jobCount ? jobs[(jobHead + jobCount - 1) % 8].ends : sorting ? sortingEnds : now - 100;
  if ((long)(starts - previousEnd) < 100) { Serial.println("ERROR SORT_PRODUCTS_TOO_CLOSE"); return; }
  { uint8_t saved = SREG; cli(); advanceMotion(now);
    jobs[(jobHead + jobCount) % 8] = { starts, starts + holdMs, motionPosition + delayMs * (unsigned long)NORMAL_MOTION_RATE };
    SREG = saved;
  }
  ++jobCount;
  strcpy(recentIds[recentNext], id); recentNext = (recentNext + 1) % 16;
  lastPcMessage = now;
  Serial.print("SORT_QUEUED "); Serial.println(id);
}

// One atomic recognition action: schedule defect by distance, then boost.
// All previously queued products also follow the actual speed-integrated position.
void recognizeProduct(char* parameters) {
  if (emergencyStopped) { Serial.println("ERROR EMERGENCY_STOP"); return; }
  if (!conveyorRunning) { Serial.println("ERROR BELT_NOT_RUNNING"); return; }
  char* state;
  char* id = strtok_r(parameters, " ", &state);
  char* ageText = strtok_r(NULL, " ", &state);
  char* travelText = strtok_r(NULL, " ", &state);
  char* holdText = strtok_r(NULL, " ", &state);
  char* defectText = strtok_r(NULL, " ", &state);
  char* boostText = strtok_r(NULL, " ", &state);
  unsigned long age, travel, hold, defect, boostMs = BOOST_DURATION_MS;
  if (!id || strlen(id) != 32 || !readBoundedNumber(ageText, 60000, &age) ||
      !readBoundedNumber(travelText, 60000, &travel) || travel < 500 ||
      !readBoundedNumber(holdText, 60000, &hold) || hold < 200 ||
      !readBoundedNumber(defectText, 1, &defect) || (boostText && (!readBoundedNumber(boostText, 60000, &boostMs) || boostMs < 500)) || strtok_r(NULL, " ", &state)) {
    Serial.println("ERROR INVALID_PRODUCT"); return;
  }
  for (byte i = 0; i < 32; ++i)
    if (!((id[i] >= '0' && id[i] <= '9') || (id[i] >= 'a' && id[i] <= 'f'))) {
      Serial.println("ERROR INVALID_SORT_ID"); return;
    }
  for (byte i = 0; i < 16; ++i) if (strcmp(recentIds[i], id) == 0) {
    Serial.print("PRODUCT_OK "); Serial.println(id); return; // No boost extension for duplicate frames/retries.
  }
  unsigned long now = millis(), capturedPosition, currentPosition;
  { uint8_t saved = SREG; cli(); advanceMotion(now); currentPosition = motionPosition; SREG = saved; }
  if (defect) {
    if (!positionAtAge(age, &capturedPosition)) { Serial.println("ERROR PRODUCT_FRAME_TOO_OLD"); return; }
    unsigned long target = capturedPosition + travel * (unsigned long)NORMAL_MOTION_RATE;
    if ((long)(target - currentPosition) < (long)(50UL * BOOST_MOTION_RATE)) { Serial.println("ERROR SORT_TOO_LATE"); return; }
    if (jobCount >= 8) { Serial.println("ERROR SORT_QUEUE_FULL"); return; }
    if (jobCount) {
      SortJob previous = jobs[(jobHead + jobCount - 1) % 8];
      unsigned long previousHold = previous.ends - previous.starts;
      if ((long)(target - previous.target) < (long)((previousHold + 100) * (unsigned long)BOOST_MOTION_RATE)) {
        Serial.println("ERROR SORT_PRODUCTS_TOO_CLOSE"); return;
      }
    } else if (sorting && (long)(sortingEnds - now) > 0 &&
        (long)(target - currentPosition) < (long)((sortingEnds - now + 100) * (unsigned long)BOOST_MOTION_RATE)) {
      Serial.println("ERROR SORT_PRODUCTS_TOO_CLOSE"); return;
    }
    jobs[(jobHead + jobCount) % 8] = { now, now + hold, target }; ++jobCount;
  }
  if (!applySpeed(true)) { Serial.println("ERROR EMERGENCY_STOP"); return; }
  boostEnds = millis() + boostMs;
  strcpy(recentIds[recentNext], id); recentNext = (recentNext + 1) % 16;
  lastPcMessage = millis();
  Serial.print("PRODUCT_OK "); Serial.println(id);
}

void handleCommand() {
  commandBuffer[commandLength] = '\0';
  if (strcmp(commandBuffer, "STOP") == 0) {
    conveyorStop(); lastPcMessage = millis(); printState();
  } else if (strcmp(commandBuffer, "RUN") == 0) {
    lastPcMessage = millis();
    if (conveyorForward()) printState(); else Serial.println("ERROR EMERGENCY_STOP");
  } else if (strcmp(commandBuffer, "PING") == 0) {
    lastPcMessage = millis(); printState();
  } else if (strcmp(commandBuffer, "HELLO") == 0) {
    lastPcMessage = millis(); Serial.print("PL_CONVEYOR_PUL_DIR_V7 "); printState();
  } else if (strcmp(commandBuffer, "STATUS") == 0) {
    lastPcMessage = millis(); printStatus();
  } else if (strcmp(commandBuffer, "RESET_ESTOP") == 0) {
    if (digitalRead(ESTOP_PIN) != LOW || !switchClosedSince || millis() - switchClosedSince < 200) {
      Serial.println("ERROR RELEASE_EMERGENCY_SWITCH"); return;
    }
    emergencyStopped = false; emergencyHandled = false;
    stopPulseOutput(); homeSorter(); lastPcMessage = millis();
    Serial.println("ESTOP_RESET"); // Never starts the conveyor.
  } else if (strncmp(commandBuffer, "SORT ", 5) == 0) {
    queueSort(commandBuffer + 5);
  } else if (strncmp(commandBuffer, "PRODUCT ", 8) == 0) {
    recognizeProduct(commandBuffer + 8);
  } else if (strcmp(commandBuffer, "LED_ON") == 0 || strcmp(commandBuffer, "LED_OFF") == 0) {
    lastPcMessage = millis(); setLight(strcmp(commandBuffer, "LED_ON") == 0); printLightState();
  } else if (strcmp(commandBuffer, "RESULT 0") == 0 || strcmp(commandBuffer, "RESULT 1") == 0) {
    lastPcMessage = millis();
    resultLamp = commandBuffer[7] == '1' ? 'D' : 'N'; resultLampAt = millis();
    if (!gasAlarm) showResultLamp();
    Serial.println(commandBuffer);
  } else if (strcmp(commandBuffer, "LED_STATUS") == 0) {
    lastPcMessage = millis(); printLightState();
  } else {
    Serial.print("ERROR UNKNOWN_COMMAND RXHEX=");
    // Hex preserves missing/corrupt bytes without injecting extra log lines.
    const char hex[] = "0123456789ABCDEF";
    for (byte i = 0; i < commandLength; ++i) {
      byte value = (byte)commandBuffer[i];
      char encoded[3] = { hex[value >> 4], hex[value & 15], '\0' };
      Serial.print(encoded);
    }
    Serial.println("");
  }
}

void setup() {
  pinMode(PUL_PIN, OUTPUT); pinMode(DIR_PIN, OUTPUT);
  pinMode(ESTOP_PIN, INPUT_PULLUP); pinMode(GAS_PIN, INPUT);
  pinMode(BUZZER_PIN, OUTPUT); pinMode(RED_LED_PIN, OUTPUT); pinMode(GREEN_LED_PIN, OUTPUT);
  noTone(BUZZER_PIN); showResultLamp();
  digitalWrite(DIR_PIN, LOW); stopPulseOutput();
  emergencyStopped = digitalRead(ESTOP_PIN) != LOW;
  // NC contact opens on press or broken wire; D12 rises HIGH.
  PCMSK0 |= _BV(PCINT6); PCIFR |= _BV(PCIF0); PCICR |= _BV(PCIE0);
  if (!emergencyStopped) homeSorter();
  climate.begin(65);
  lastClimateRead = millis() - CLIMATE_INTERVAL_MS;
  Serial.begin(9600);
  inspectionLight.begin(); inspectionLight.setBrightness(LED_BRIGHTNESS); setLight(true);
  lastPcMessage = millis();
  Serial.println("PL_CONVEYOR_PUL_DIR_V7 STOPPED");
}

void loop() {
  unsigned long now = millis();
  if (digitalRead(ESTOP_PIN) != LOW) {
    emergencyStopped = true; stopPulseOutput(); switchClosedSince = 0;
  } else if (!switchClosedSince) switchClosedSince = now;
  if (emergencyStopped && !emergencyHandled) {
    cancelSorting(true); emergencyHandled = true; Serial.println("EMERGENCY_STOP");
  }
  for (byte count = 0; count < 16 && Serial.available() > 0; ++count) {
    char value = (char)Serial.read();
    if (value == '\r') continue;
    if (value == '\n') {
      if (commandOverflow) Serial.println("ERROR COMMAND_TOO_LONG");
      else if (commandLength) handleCommand();
      commandLength = 0; commandOverflow = false;
    } else if (commandLength < sizeof(commandBuffer) - 1) commandBuffer[commandLength++] = value;
    else commandOverflow = true;
  }
  now = millis();
  // PC가 연결돼 있지 않을 때만 아두이노가 스스로 온습도를 읽습니다(2초 간격).
  // PC 연결 중에는 STATUS 요청 때 읽으므로 명령 수신 중에 DHT 읽기가 끼어들지 않습니다.
  if ((unsigned long)(now - lastPcMessage) >= PC_TIMEOUT_MS && commandLength == 0 && Serial.available() == 0) {
    refreshClimate(); now = millis();
  }
  updateGasAlarm(now);
  if (resultLamp && (unsigned long)(now - resultLampAt) >= RESULT_LAMP_MS) {
    resultLamp = 0;
    if (!gasAlarm) showResultLamp();
  }
  if (speedBoosted && (long)(now - boostEnds) >= 0) applySpeed(false);
  if (conveyorRunning && (unsigned long)(now - lastPcMessage) >= PC_TIMEOUT_MS) {
    conveyorStop(); Serial.println("WATCHDOG_STOP");
  }
  if (conveyorRunning && !emergencyStopped) {
    { uint8_t saved = SREG; cli(); advanceMotion(now); SREG = saved; }
    if (sorting && (long)(now - sortingEnds) >= 0) { homeSorter(); sorting = false; }
    if (!sorting && jobCount && (long)(motionPosition - jobs[jobHead].target) >= 0) {
      SortJob job = jobs[jobHead]; jobHead = (jobHead + 1) % 8; --jobCount;
      if ((long)(motionPosition - job.target) < 500UL * BOOST_MOTION_RATE) {
        uint8_t saved = SREG; cli();
        if (!emergencyStopped && conveyorRunning && !(PINB & _BV(PB6))) {
          sorter.write(55); sorterAngle = 55; sorting = true; sortingEnds = now + (job.ends - job.starts);
        }
        SREG = saved;
      } else { conveyorStop(); Serial.println("ERROR SORT_EXPIRED"); }
    }
  }
}
