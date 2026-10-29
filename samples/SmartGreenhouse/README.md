# Smart greenhouse

A flow that reads the air (BME280) and the soil (capacitive probe on GPIO 34), runs a fan when it is above 28 °C, waters when the soil is below 35 %, and shows both readings on a 16×2 LCD.

- Open `greenhouse.cbflow` in Visual Studio, choose **Simulator** as the port and press **Run**: no hardware needed.
- Or in a browser: `dotnet run --project src/CodeBridge.WebEditor -- --flow samples/SmartGreenhouse/greenhouse.cbflow --port simulator`
- Or keep it running: `CodeBridge.FlowHost serve --flow greenhouse.cbflow --port COM3 --interval 5000`

The full guide, with the parts, the wiring and how to grow it, is [docs/greenhouse.md](../../docs/greenhouse.md).
