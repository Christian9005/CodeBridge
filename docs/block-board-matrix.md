# Blocks on every board

The editor offers **65 blocks on an ESP32** (ESP32 DevKit, ESP32-S3 DevKit, XIAO ESP32-S3, ESP32-S2 Mini, ESP32-C3 DevKit) and **24 blocks on an Arduino** (Uno, Nano, Nano with the old bootloader, Mega 2560). The Arduino boards offer fewer blocks, never different ones: the I2C chips, the LCD, the OLED, the NeoPixel strip, the buzzer and the DC motor need the ESP32 firmware.

The table is checked on every build (`tests/CodeBridge.Matrix.Tests`). For each block on each board, the block is dropped into a flow with what the editor fills in (a trigger or a constant for an input, and the first pin of the board's own list when the block has no default pin), and the flow is validated, run on the virtual board of that board and exported to C# that is compiled.

| Result | What it means |
|---|---|
| works | the block is valid, runs on the virtual board and the C# it exports compiles |
| needs the chip | the same, but the block reads an I2C chip and, with nothing attached, stops with `Check the wiring, the power and the address of the chip.` |
| plan only | Sample Channel on an Arduino: the buffered acquisition lives in the ESP32 firmware, so on an Arduino the block hands its sampling plan to the next block |
| - | the board does not offer the block |
| BROKEN | invalid, crashes or does not export: the build fails if any cell says this |

The virtual board checks the flow, the pins and the commands; it does not prove that a sensor reads well. For that, see the section on real boards below.

<!-- matrix:start -->
<!-- Written by the tests of tests/CodeBridge.Matrix.Tests. Do not edit by hand: run them with CODEBRIDGE_UPDATE_MATRIX=1 to write it again. -->

| Block | Category | ESP32 DevKit | ESP32-S3 DevKit | XIAO ESP32-S3 | ESP32-S2 Mini | ESP32-C3 DevKit | Arduino Uno | Arduino Nano | Arduino Nano (old bootloader) | Arduino Mega 2560 |
|---|---|---|---|---|---|---|---|---|---|---|
| `acquisition.interrupt-input` | Acquisition | works | works | works | works | works | works | works | works | works |
| `acquisition.sample-channel` | Acquisition | works | works | works | works | works | plan only | plan only | plan only | plan only |
| `iot.pca9685` | Actuators | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `servo.write` | Actuators | works | works | works | works | works | works | works | works | works |
| `dashboard.stream` | Dashboard | works | works | works | works | works | works | works | works | works |
| `debug.log` | Debug | works | works | works | works | works | works | works | works | works |
| `core.timer` | Flow | works | works | works | works | works | works | works | works | works |
| `flow.manual-trigger` | Flow | works | works | works | works | works | works | works | works | works |
| `gpio.analog-read` | GPIO | works | works | works | works | works | works | works | works | works |
| `gpio.blink-led` | GPIO | works | works | works | works | works | works | works | works | works |
| `gpio.digital-read` | GPIO | works | works | works | works | works | works | works | works | works |
| `gpio.digital-write` | GPIO | works | works | works | works | works | works | works | works | works |
| `gpio.pin-mode` | GPIO | works | works | works | works | works | works | works | works | works |
| `gpio.pwm-write` | GPIO | works | works | works | works | works | works | works | works | works |
| `gpio.set-output` | GPIO | works | works | works | works | works | works | works | works | works |
| `logic.compare` | Logic | works | works | works | works | works | works | works | works | works |
| `logic.constant-boolean` | Logic | works | works | works | works | works | works | works | works | works |
| `logic.constant-number` | Logic | works | works | works | works | works | works | works | works | works |
| `logic.gate` | Logic | works | works | works | works | works | works | works | works | works |
| `logic.threshold` | Logic | works | works | works | works | works | works | works | works | works |
| `math.calc` | Math | works | works | works | works | works | works | works | works | works |
| `math.map` | Math | works | works | works | works | works | works | works | works | works |
| `iot.ht16k33-7seg` | Modules | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.i2c-read` | Modules | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.i2c-scan` | Modules | works | works | works | works | works | - | - | - | - |
| `iot.i2c-write` | Modules | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.pcf8574-read` | Modules | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.pcf8574-write` | Modules | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `module.buzzer` | Modules | works | works | works | works | works | - | - | - | - |
| `module.dc-motor` | Modules | works | works | works | works | works | - | - | - | - |
| `module.lcd` | Modules | works | works | works | works | works | - | - | - | - |
| `module.neopixel` | Modules | works | works | works | works | works | - | - | - | - |
| `module.oled` | Modules | works | works | works | works | works | - | - | - | - |
| `module.relay` | Modules | works | works | works | works | works | works | works | works | works |
| `module.rgb-led` | Modules | works | works | works | works | works | - | - | - | - |
| `module.stepper` | Modules | works | works | works | works | works | - | - | - | - |
| `iot.ads1115` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.aht20` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.bme280` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.bmp280` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.ds1307` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.ds3231` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.hmc5883l` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.lm75` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.max44009` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.mcp9808` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.mlx90614` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.mpr121` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.pcf8563` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.scd4x` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.sht3x` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.sht4x` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.vl53l0x` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `iot.vl53l1x` | Sensors | needs the chip | needs the chip | needs the chip | needs the chip | needs the chip | - | - | - | - |
| `sensor.analog-percent` | Sensors | works | works | works | works | works | works | works | works | works |
| `sensor.bh1750` | Sensors | works | works | works | works | works | - | - | - | - |
| `sensor.bme280` | Sensors | works | works | works | works | works | - | - | - | - |
| `sensor.button` | Sensors | works | works | works | works | works | works | works | works | works |
| `sensor.color` | Sensors | works | works | works | works | works | - | - | - | - |
| `sensor.dht` | Sensors | works | works | works | works | works | - | - | - | - |
| `sensor.ds18b20` | Sensors | works | works | works | works | works | - | - | - | - |
| `sensor.ina219` | Sensors | works | works | works | works | works | - | - | - | - |
| `sensor.mpu6050` | Sensors | works | works | works | works | works | - | - | - | - |
| `sensor.pir` | Sensors | works | works | works | works | works | - | - | - | - |
| `sensor.ultrasonic` | Sensors | works | works | works | works | works | - | - | - | - |

| Board | Blocks | works | needs the chip | plan only | BROKEN |
|---|---|---|---|---|---|
| ESP32 DevKit | 65 | 41 | 24 | 0 | 0 |
| ESP32-S3 DevKit | 65 | 41 | 24 | 0 | 0 |
| XIAO ESP32-S3 | 65 | 41 | 24 | 0 | 0 |
| ESP32-S2 Mini | 65 | 41 | 24 | 0 | 0 |
| ESP32-C3 DevKit | 65 | 41 | 24 | 0 | 0 |
| Arduino Uno | 24 | 23 | 0 | 1 | 0 |
| Arduino Nano | 24 | 23 | 0 | 1 | 0 |
| Arduino Nano (old bootloader) | 24 | 23 | 0 | 1 | 0 |
| Arduino Mega 2560 | 24 | 23 | 0 | 1 | 0 |

<!-- matrix:end -->

## On a real board

The virtual board gives plausible readings for the sensors the firmware reads itself (DHT, ultrasonic, BH1750, BME280, MPU6050, colour, INA219, DS18B20), so those blocks show `works` above. A real board with nothing wired to it says something else, and the same test can be run on one:

```
set CODEBRIDGE_HARDWARE_PORT=COM3
dotnet test tests/CodeBridge.Matrix.Tests --filter HardwareMatrixTests
```

It runs every block the board offers through the flow runtime and, after each one, sends `PING`: a block that hangs or resets the board fails the test. Without `CODEBRIDGE_HARDWARE_PORT` the test is skipped, so the build does not need a board. Unplug anything that must not move first: the blocks drive their default pins (a servo on GPIO 13, a stepper, a relay, a tone).

**Last run: ESP32 DevKit (CP210x, firmware 0.9.4), nothing wired to it, 65 blocks.** None hung or reset the board. 31 ran to the end and 34 stopped with a clear message because the part they read is not there. The whole run takes 5 seconds; with firmware 0.9.2 the blocks that read from an absent chip each waited about a second.

Ran to the end: `acquisition.interrupt-input`, `acquisition.sample-channel`, `core.timer`, `dashboard.stream`, `debug.log`, `flow.manual-trigger`, `gpio.analog-read`, `gpio.blink-led`, `gpio.digital-read`, `gpio.digital-write`, `gpio.pin-mode`, `gpio.pwm-write`, `gpio.set-output`, `iot.i2c-scan`, `logic.compare`, `logic.constant-boolean`, `logic.constant-number`, `logic.gate`, `logic.threshold`, `math.calc`, `math.map`, `module.buzzer`, `module.dc-motor`, `module.neopixel`, `module.relay`, `module.rgb-led`, `module.stepper`, `sensor.analog-percent`, `sensor.button`, `sensor.pir`, `servo.write`.

Needs the sensor or display (stops with a message such as `BME280 not found`, `LCD not found on I2C`, `OLED not found on I2C`, `DHT checksum failed`, `No echo` or `I2C write to 0x76 failed ... Check the wiring, the power and the address of the chip`): `module.lcd`, `module.oled`, `sensor.bh1750`, `sensor.bme280`, `sensor.color`, `sensor.dht`, `sensor.ds18b20`, `sensor.ina219`, `sensor.mpu6050`, `sensor.ultrasonic` and the 24 `iot.*` chip blocks (all but `iot.i2c-scan`).

What this showed:

- With firmware 0.9.2 `module.lcd` and `module.oled` ended without an error even with no display connected, and `IR`/`IRR` from an absent chip answered an empty `OK` after about a second. Firmware 0.9.4 checks that something answers at the address first (a few milliseconds) and the commands answer `ERR:I2C device did not answer`, `ERR:LCD not found on I2C` or `ERR:OLED not found on I2C`. Use an I2C Scan block to see what is on the bus.
- The other boards of the table (ESP32-S3, XIAO ESP32-S3, ESP32-S2 Mini, ESP32-C3 and the four Arduinos) have been run on the virtual board only; no such board was available to run the hardware test on.
