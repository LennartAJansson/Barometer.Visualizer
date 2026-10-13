namespace Barometer.Visualizer.Models;

/// <summary>
/// Ett mätvärde från BME280, uppackat ur ESP32:ns kuvert på "sensors.bme280.raw":
/// {"type":"bme280","sender":"MAC=..,IP=..","value":"{\"hpa\":..,\"temp\":..,\"humidity\":..}"}
/// </summary>
public record BarometerReading(
    DateTime Timestamp,     // Lokal tid (JetStream-tidsstämpel, annars mottagningstid)
    double PressureHpa,
    double TemperatureC,
    double HumidityPercent,
    string Sender);
