#nullable enable
using System;
using System.Collections.Generic;

namespace CodeBridge.VisualStudio.Editor
{
    /// <summary>Beginner help for the sensor, module and helper blocks (see <see cref="ModuleBlocksHelp"/>).</summary>
    internal static class ModuleBlocksHelp
    {
        internal static readonly Dictionary<string, BlockHelpEntry> Entries = new Dictionary<string, BlockHelpEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["sensor.dht"] = new BlockHelpEntry(
                "Reads a DHT11 or DHT22 sensor: how warm it is and how humid the air is.",
                "Three wires: power (3.3 V), ground and one data pin. The DHT22 is more accurate than the blue DHT11. Wait two seconds between reads in a loop.",
                DemoKind.Temperature,
                "trigger", "Read the sensor when this fires.",
                "temperature", "Temperature in degrees Celsius.",
                "humidity", "Relative humidity, 0 to 100 %."),

            ["sensor.ultrasonic"] = new BlockHelpEntry(
                "Measures the distance to an object with an HC-SR04: it sends a sound pulse and times the echo.",
                "The HC-SR04 works at 5 V: use a voltage divider on the echo wire (it must not send 5 V to the ESP32). It sees objects from 2 cm to about 4 m.",
                DemoKind.Distance,
                "trigger", "Measure when this fires.",
                "distance", "Distance in centimetres."),

            ["sensor.ds18b20"] = new BlockHelpEntry(
                "Reads a DS18B20 temperature probe (the waterproof one with a long cable).",
                "Needs a 4.7 kΩ resistor between the data wire and 3.3 V. Good for liquids, soil and outdoors.",
                DemoKind.Temperature,
                "trigger", "Read the probe when this fires.",
                "temperature", "Temperature in degrees Celsius."),

            ["sensor.bme280"] = new BlockHelpEntry(
                "A tiny weather station: temperature, humidity and air pressure from one I2C chip.",
                "I2C modules share two wires (SDA and SCL), so several can sit on the same pins. If it is not found, try the other address.",
                DemoKind.Temperature,
                "trigger", "Read the sensor when this fires.",
                "temperature", "Temperature in degrees Celsius.",
                "humidity", "Relative humidity, 0 to 100 %.",
                "pressure", "Air pressure in hectopascals (about 1013 at sea level)."),

            ["sensor.bh1750"] = new BlockHelpEntry(
                "Measures how bright it is, in lux (a dark room is about 5, an office 400, sunlight 10000 or more).",
                "More accurate than a light-dependent resistor. Use a Threshold block to switch a lamp on when it gets dark.",
                DemoKind.Light,
                "trigger", "Read the sensor when this fires.",
                "lux", "Light level in lux."),

            ["sensor.mpu6050"] = new BlockHelpEntry(
                "Feels movement and tilt: acceleration on three axes (including gravity) and rotation speed on three axes.",
                "Lying flat, az is about 9.8 (gravity). Tilt the board and ax or ay change. Great for levels, shake detection and balancing robots.",
                DemoKind.Tilt,
                "trigger", "Read the sensor when this fires.",
                "ax", "Acceleration along X in m/s².",
                "ay", "Acceleration along Y in m/s².",
                "az", "Acceleration along Z in m/s² (about 9.8 when lying flat).",
                "gx", "Rotation speed around X in rad/s.",
                "gy", "Rotation speed around Y in rad/s.",
                "gz", "Rotation speed around Z in rad/s."),

            ["sensor.ina219"] = new BlockHelpEntry(
                "Measures the voltage and current of whatever passes through it, and the power it uses.",
                "Put it in series with the load (the current flows through it). Perfect for battery monitors and for finding out what your project really consumes.",
                DemoKind.Power,
                "trigger", "Measure when this fires.",
                "voltage", "Voltage in volts.",
                "current", "Current in milliamps.",
                "power", "Power in milliwatts."),

            ["sensor.color"] = new BlockHelpEntry(
                "Reads the colour of the object in front of a TCS34725 sensor as red, green and blue from 0 to 255.",
                "Hold the object a few millimetres away and keep the ambient light steady. Use the LED on the module to light it evenly.",
                DemoKind.ColorSense,
                "trigger", "Read the colour when this fires.",
                "red", "Red, 0 to 255.",
                "green", "Green, 0 to 255.",
                "blue", "Blue, 0 to 255."),

            ["sensor.pir"] = new BlockHelpEntry(
                "True while a PIR sensor sees someone move in front of it.",
                "The HC-SR501 needs about a minute to warm up after power-on and holds the signal for a few seconds. Two small potentiometers set the delay and the range.",
                DemoKind.Motion,
                "trigger", "Check for motion when this fires.",
                "motion", "True while there is motion."),

            ["sensor.button"] = new BlockHelpEntry(
                "True while a push button is pressed. It filters the contact bounce for you.",
                "Wire one side of the button to the pin and the other to GND: the board's internal pull-up does the rest, no resistor needed.",
                DemoKind.Press,
                "trigger", "Check the button when this fires.",
                "pressed", "True while the button is pressed."),

            ["sensor.analog-percent"] = new BlockHelpEntry(
                "Reads any analog sensor (light sensor, soil probe, potentiometer) as a percentage from 0 to 100.",
                "Soil moisture probes read high when dry: turn Invert on to get 100 % when wet. With Wi-Fi on, prefer ADC1 pins.",
                DemoKind.Level,
                "trigger", "Read the pin when this fires.",
                "percent", "The reading as 0 to 100 %.",
                "raw", "The raw reading before scaling."),

            ["module.relay"] = new BlockHelpEntry(
                "Switches a relay module on or off, so the board can control a lamp, a pump or any mains device through it.",
                "Most relay boards switch on when the pin is LOW (Active low is on by default). Never touch mains wiring: use a relay module with an enclosure.",
                DemoKind.Relay,
                "trigger", "Switch when this fires.",
                "state", "Optional: true = on, false = off. Overrides the State property. (Also reports the state after switching.)",
                "done", "Fires after the relay was switched."),

            ["module.buzzer"] = new BlockHelpEntry(
                "Plays a tone on a passive buzzer or small speaker for the time you choose.",
                "Wire the buzzer between the pin and GND. A higher frequency is a higher note: 440 Hz is the A above middle C. Active buzzers (with built-in oscillator) only click.",
                DemoKind.Sound,
                "trigger", "Play when this fires.",
                "frequency", "Optional: the tone in hertz. Overrides the Frequency property.",
                "done", "Fires after the tone ended."),

            ["module.rgb-led"] = new BlockHelpEntry(
                "Lights an RGB LED in any colour by mixing red, green and blue (0 to 255 each).",
                "Put a resistor (about 220 Ω) on each colour. Wire a Map block into a colour input to make the colour follow a sensor.",
                DemoKind.RgbLed,
                "trigger", "Set the colour when this fires.",
                "red", "Optional: red, 0 to 255.",
                "green", "Optional: green, 0 to 255.",
                "blue", "Optional: blue, 0 to 255.",
                "done", "Fires after the colour was set."),

            ["module.neopixel"] = new BlockHelpEntry(
                "Lights a strip or ring of NeoPixel (WS2812) LEDs: all of them, or a single pixel.",
                "Power long strips from an external 5 V supply that shares ground with the board. Each pixel draws up to 60 mA at full white, so keep the brightness low.",
                DemoKind.Pixels,
                "trigger", "Light the pixels when this fires.",
                "red", "Optional: red, 0 to 255.",
                "green", "Optional: green, 0 to 255.",
                "blue", "Optional: blue, 0 to 255.",
                "done", "Fires after the strip was updated."),

            ["module.lcd"] = new BlockHelpEntry(
                "Writes a line of text on a 16x2 or 20x4 character LCD with an I2C backpack.",
                "Write {value} in the text to show the wired number, for example  Temp: {value} C . The line is padded so old letters never stay behind.",
                DemoKind.Screen,
                "trigger", "Write when this fires.",
                "value", "Optional: the number or text shown where {value} is.",
                "done", "Fires after the text was written."),

            ["module.oled"] = new BlockHelpEntry(
                "Draws text on a small OLED display (SSD1306, 128x64 or 128x32).",
                "Write {value} in the text to show the wired number. Size 2 fits about 10 letters per line. Keep Clear on so each update replaces the previous one.",
                DemoKind.Screen,
                "trigger", "Draw when this fires.",
                "value", "Optional: the number or text shown where {value} is.",
                "done", "Fires after the text was drawn."),

            ["module.dc-motor"] = new BlockHelpEntry(
                "Runs a DC motor forward or in reverse through an H-bridge (L298N, L9110...). Speed goes from -100 to 100.",
                "Never connect a motor straight to the board's pins. Power it from its own supply that shares ground with the board. Wire a Map block into speed to control it with a sensor.",
                DemoKind.Motor,
                "trigger", "Set the speed when this fires.",
                "speed", "Optional: -100 (full reverse) to 100 (full forward). Overrides the Speed property.",
                "done", "Fires after the speed was set."),

            ["module.stepper"] = new BlockHelpEntry(
                "Turns a stepper motor (28BYJ-48 with ULN2003) an exact number of steps. 2048 steps is one full turn.",
                "Positive steps turn one way, negative the other. Slow speeds (10 rpm) are strongest. Power it from 5 V, not from the board's 3.3 V pin.",
                DemoKind.Stepper,
                "trigger", "Move when this fires.",
                "steps", "Optional: the number of steps. Overrides the Steps property.",
                "done", "Fires after the motor moved."),

            ["iot.bmp280"] = new BlockHelpEntry(
                "Reads a BMP280: air pressure, temperature and the altitude that pressure means.",
                "Altitude is worked out from the pressure at sea level today (about 1013 hPa; look it up for your town for a precise value). Not the same as the BME280: this one has no humidity.",
                DemoKind.Level,
                "trigger", "Read the sensor when this fires.",
                "pressure", "Air pressure in hectopascals.",
                "temperature", "Temperature in degrees Celsius.",
                "altitude", "Altitude in metres, from the sea-level pressure property."),

            ["iot.bme280"] = new BlockHelpEntry(
                "A small weather station: temperature, humidity, air pressure and the altitude that pressure means, from one BME280 chip.",
                "The same chip as the Weather (BME280) block, read with the .NET IoT driver, plus the altitude. Altitude is worked out from the pressure at sea level today (about 1013 hPa).",
                DemoKind.Temperature,
                "trigger", "Read the sensor when this fires.",
                "temperature", "Temperature in degrees Celsius.",
                "humidity", "Relative humidity, 0 to 100 %.",
                "pressure", "Air pressure in hectopascals.",
                "altitude", "Altitude in metres, from the sea-level pressure property."),

            ["iot.lm75"] = new BlockHelpEntry(
                "Reads an LM75 (or a TMP75 or PCT2075): a simple digital thermometer on two wires.",
                "Three address pins let eight of them share the same two wires, for example to measure several rooms.",
                DemoKind.Temperature,
                "trigger", "Read the sensor when this fires.",
                "temperature", "Temperature in degrees Celsius."),

            ["iot.sht4x"] = new BlockHelpEntry(
                "Reads an SHT40, SHT41 or SHT45: temperature and humidity in one measurement.",
                "The newest and most accurate of the SHT family. It has a fixed address that depends on the variant printed on the chip.",
                DemoKind.Temperature,
                "trigger", "Read the sensor when this fires.",
                "temperature", "Temperature in degrees Celsius.",
                "humidity", "Relative humidity, 0 to 100 %."),

            ["iot.aht20"] = new BlockHelpEntry(
                "Reads an AHT20 (also sold as AHT21): temperature and humidity from a very small, cheap chip.",
                "Good for a room or a plant. Leave a second between reads: the chip needs time to measure.",
                DemoKind.Temperature,
                "trigger", "Read the sensor when this fires.",
                "temperature", "Temperature in degrees Celsius.",
                "humidity", "Relative humidity, 0 to 100 %."),

            ["iot.scd4x"] = new BlockHelpEntry(
                "Measures carbon dioxide with an SCD40 or SCD41, and the temperature and humidity beside it.",
                "Above about 1000 ppm a closed room feels stuffy: use a Threshold block to light a LED or open a window. It measures every 5 seconds, so the first run after power-on says there is no reading yet.",
                DemoKind.Level,
                "trigger", "Read the sensor when this fires.",
                "co2", "Carbon dioxide in parts per million (outdoors is about 420).",
                "temperature", "Temperature in degrees Celsius.",
                "humidity", "Relative humidity, 0 to 100 %."),

            ["iot.max44009"] = new BlockHelpEntry(
                "Measures how bright it is with a MAX44009, in lux, from moonlight to full sun.",
                "Very low power and a wide range. Use a Threshold block to switch a lamp on when it gets dark.",
                DemoKind.Light,
                "trigger", "Read the sensor when this fires.",
                "lux", "Light level in lux."),

            ["iot.hmc5883l"] = new BlockHelpEntry(
                "A magnetic compass (HMC5883L): the direction the board points to, in degrees from north.",
                "Keep it away from motors, magnets and wires with current: they bend the field. Many cheap boards sold as HMC5883L carry a QMC5883L, a different chip that this block does not read.",
                DemoKind.Tilt,
                "trigger", "Read the compass when this fires.",
                "heading", "Direction in degrees, 0 to 360 (0 = north).",
                "x", "Magnetic field on X.",
                "y", "Magnetic field on Y.",
                "z", "Magnetic field on Z."),

            ["iot.vl53l1x"] = new BlockHelpEntry(
                "Measures distance with a VL53L1X laser, up to about 4 metres: twice the range of the VL53L0X.",
                "The first measurement takes about a second while the chip starts. Dark or shiny surfaces shorten its range.",
                DemoKind.Distance,
                "trigger", "Measure when this fires.",
                "distance", "Distance in centimetres.",
                "millimetres", "Distance in millimetres."),

            ["iot.ds1307"] = new BlockHelpEntry(
                "Reads a DS1307 real-time clock: the date and time as text. It keeps time with a coin cell when the board is off.",
                "The DS1307 drifts more than the DS3231 (a minute or so a month). The clock must be set once with another tool; this block only reads it.",
                DemoKind.Screen,
                "trigger", "Read the clock when this fires.",
                "time", "Date and time as yyyy-MM-dd HH:mm:ss."),

            ["iot.pcf8563"] = new BlockHelpEntry(
                "Reads a PCF8563 real-time clock: the date and time as text. It keeps time with a coin cell when the board is off.",
                "A low-power clock found on many small modules. The clock must be set once with another tool; this block only reads it.",
                DemoKind.Screen,
                "trigger", "Read the clock when this fires.",
                "time", "Date and time as yyyy-MM-dd HH:mm:ss."),

            ["iot.ht16k33-7seg"] = new BlockHelpEntry(
                "Shows a number on a 4-digit 7-segment display driven by an HT16K33 (the Adafruit and many clone backpacks).",
                "It fits four digits: 23.456 shows as 23.46 and a number too big shows ----. Wire a sensor block into value to make a thermometer.",
                DemoKind.Screen,
                "trigger", "Show the number when this fires.",
                "value", "Optional: the number shown. Overrides the Value property.",
                "done", "Fires after the number was shown."),

            ["iot.pcf8574-write"] = new BlockHelpEntry(
                "Sets the 8 pins of a PCF8574 expander with one number from 0 to 255: each bit is one pin.",
                "A cheap way to get 8 more outputs for relays or LEDs. Many relay boards switch on with a 0, so 255 is all off and 254 switches the first one on.",
                DemoKind.Relay,
                "trigger", "Write when this fires.",
                "value", "Optional: the number, 0 to 255. Overrides the Value property.",
                "done", "Fires after the pins were set."),

            ["iot.pcf8574-read"] = new BlockHelpEntry(
                "Reads the 8 pins of a PCF8574 expander as one number from 0 to 255: each bit is one pin.",
                "A pin must have been written as 1 (with Expander Write) to be used as an input: the chip pulls it up and a button to GND reads 0.",
                DemoKind.Press,
                "trigger", "Read when this fires.",
                "value", "The pins as a number, 0 to 255."),

            ["iot.mpr121"] = new BlockHelpEntry(
                "Reads 12 capacitive touch keys (MPR121): touch a pad, a wire or a piece of foil with a finger.",
                "The number has one bit per key: key 0 is 1, key 1 is 2, key 2 is 4, so keys 0 and 2 together read 5. Touched says whether any key is.",
                DemoKind.Press,
                "trigger", "Read the keys when this fires.",
                "keys", "The touched keys as one number (bit 0 is key 0).",
                "touched", "True while any key is touched."),

            ["iot.i2c-scan"] = new BlockHelpEntry(
                "Lists the chips that answer on the I2C bus, as addresses like 0x44, 0x76.",
                "The quickest way to find out whether a sensor is wired and which address it has. Send it to a Debug block and press Run.",
                DemoKind.Screen,
                "trigger", "Scan when this fires.",
                "devices", "The addresses found, for example 0x44, 0x76.",
                "count", "How many chips answered."),

            ["iot.i2c-read"] = new BlockHelpEntry(
                "Reads 1 to 4 bytes from a register of any I2C chip and gives them as one number.",
                "For a chip that has no block yet: its datasheet lists the address and the registers. Pick the byte order the datasheet uses; most use the most significant byte first.",
                DemoKind.Number,
                "trigger", "Read when this fires.",
                "value", "The bytes as one number.",
                "hex", "The bytes as hexadecimal text, for example 1A2B."),

            ["iot.i2c-write"] = new BlockHelpEntry(
                "Writes a number (1 to 4 bytes) to a register of any I2C chip: start a measurement, set a mode, change a setting.",
                "For a chip that has no block yet: its datasheet lists the address and the registers. A wrong value can put some chips in an odd mode: cut the power to reset them.",
                DemoKind.Number,
                "trigger", "Write when this fires.",
                "value", "Optional: the number written. Overrides the Value property.",
                "done", "Fires after the register was written."),

            ["logic.threshold"] = new BlockHelpEntry(
                "True when a number is above (or below) a limit: turns a reading into an on/off decision.",
                "Wire a sensor into value and the result into a Relay or Digital Write. Example: a lamp that turns on when the light drops below a limit.",
                DemoKind.Compare,
                "value", "The number to check, for example a sensor reading.",
                "result", "True when the condition holds."),

            ["logic.gate"] = new BlockHelpEntry(
                "Combines two true/false values: AND, OR, XOR, NAND, NOR, or NOT of the first one.",
                "Use AND to switch something on only when two conditions hold (it is dark AND someone is there). Leave b unconnected for NOT.",
                DemoKind.Gate,
                "a", "The first true/false value.",
                "b", "The second true/false value (not needed for NOT).",
                "result", "The combined value."),

            // ---- .NET IoT bindings (CodeBridge.IotBindings): the host registers them, so they reach the toolbox like the built-in blocks
            ["iot.sht3x"] = new BlockHelpEntry(
                "Reads an SHT30, SHT31 or SHT35 over I2C: how warm it is and how humid the air is, more accurately than a DHT22.",
                "I2C modules share two wires (SDA and SCL), so several can sit on the same pins. If it is not found, try the other address.",
                DemoKind.Temperature,
                "trigger", "Read the sensor when this fires.",
                "temperature", "Temperature in degrees Celsius.",
                "humidity", "Relative humidity, 0 to 100 %."),

            ["iot.mcp9808"] = new BlockHelpEntry(
                "Reads an MCP9808: a precision digital thermometer, accurate to half a degree.",
                "Three address pins let you put up to eight on the same two wires. Good for thermostats and for checking another sensor.",
                DemoKind.Temperature,
                "trigger", "Read the sensor when this fires.",
                "temperature", "Temperature in degrees Celsius."),

            ["iot.mlx90614"] = new BlockHelpEntry(
                "Measures the temperature of an object without touching it (an infrared thermometer), and of the air around the sensor.",
                "Point the sensor at the object from a few centimetres away. The object reading depends on the surface: shiny metal reads low.",
                DemoKind.Temperature,
                "trigger", "Measure when this fires.",
                "object", "Temperature of what the sensor points at, in degrees Celsius.",
                "ambient", "Temperature of the sensor itself, in degrees Celsius."),

            ["iot.vl53l0x"] = new BlockHelpEntry(
                "Measures distance with a laser (VL53L0X time-of-flight sensor), up to about 2 metres, more precisely than an ultrasonic sensor.",
                "It is small and works at 3.3 V. The first measurement takes about a second while the chip starts. Dark or shiny surfaces shorten its range.",
                DemoKind.Distance,
                "trigger", "Measure when this fires.",
                "distance", "Distance in centimetres.",
                "millimetres", "Distance in millimetres."),

            ["iot.ads1115"] = new BlockHelpEntry(
                "Adds precise analog inputs: an ADS1115 has four inputs of 16 bits each and does not depend on Wi-Fi like the ESP32's own ADC2 pins.",
                "Pick the channel (A0 to A3). The voltage is from 0 to 4.096 V; never feed it more than its own supply voltage.",
                DemoKind.Level,
                "trigger", "Read when this fires.",
                "voltage", "Voltage on the channel, in volts.",
                "raw", "The raw 16-bit reading."),

            ["iot.ds3231"] = new BlockHelpEntry(
                "Reads a DS3231 real-time clock: the date and time as text, and the chip's own temperature. It keeps time with a coin cell when the board is off.",
                "The clock must be set once (with its time set from your code or another tool); this block only reads it.",
                DemoKind.Screen,
                "trigger", "Read the clock when this fires.",
                "time", "Date and time as yyyy-MM-dd HH:mm:ss.",
                "temperature", "Temperature of the chip in degrees Celsius."),

            ["iot.pca9685"] = new BlockHelpEntry(
                "Gives the board 16 more PWM outputs over two wires (a PCA9685): servos, LEDs or motors without using its own pins.",
                "For hobby servos keep 50 Hz: 2.5 % is one end, 7.5 % the middle and 12.5 % the other end. Power the servos from their own supply.",
                DemoKind.Servo,
                "trigger", "Set the duty cycle when this fires.",
                "duty", "Optional: the duty cycle, 0 to 100 %. Overrides the Duty property.",
                "done", "Fires after the output was set."),

            ["math.calc"] = new BlockHelpEntry(
                "Adds, subtracts, multiplies or divides two numbers, or takes the smaller or larger one.",
                "Convert units: multiply by 1.8 and add 32 to turn °C into °F (use two Calculate blocks). Division by zero stops the flow with a clear message.",
                DemoKind.Calc,
                "a", "The first number.",
                "b", "Optional: the second number. Overrides the b property.",
                "result", "The result.")
        };
    }
}
