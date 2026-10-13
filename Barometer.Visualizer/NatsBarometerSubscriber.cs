using System.Text;
using System.Text.Json;
using Barometer.Visualizer.Models;
using NATS.Client;
using NATS.Client.JetStream;

namespace Barometer.Visualizer;

/// <summary>
/// Prenumererar på barometerns subject. Försöker först med en ordnad JetStream-konsument
/// som spelar upp all historik i streamen (sensors) och sedan fortsätter live.
/// Finns ingen JetStream faller den tillbaka på en vanlig core-NATS-prenumeration.
/// </summary>
public class NatsBarometerSubscriber : IDisposable
{
    private readonly string _url;
    private readonly string _subject;
    private IConnection? _connection;
    private IAsyncSubscription? _subscription;
    private bool _historyDone;

    public event Action<BarometerReading>? ReadingReceived;
    public event Action<string>? StatusChanged;
    public event Action<Exception>? ConnectionError;

    public NatsBarometerSubscriber(string url, string subject)
    {
        _url = url;
        _subject = subject;
    }

    public void Start()
    {
        try
        {
            var opts = ConnectionFactory.GetDefaultOptions();
            opts.Url = _url;
            opts.Name = "barometer-visualizer";
            opts.MaxReconnect = Options.ReconnectForever;
            opts.ReconnectWait = 2000;
            opts.DisconnectedEventHandler += (_, _) => StatusChanged?.Invoke("Frånkopplad, försöker återansluta...");
            opts.ReconnectedEventHandler += (_, _) => StatusChanged?.Invoke("Återansluten");

            _connection = new ConnectionFactory().CreateConnection(opts);

            try
            {
                var js = _connection.CreateJetStreamContext();
                var consumerConfig = ConsumerConfiguration.Builder()
                    .WithDeliverPolicy(DeliverPolicy.All)
                    .Build();
                var pso = PushSubscribeOptions.Builder()
                    .WithOrdered(true)
                    .WithConfiguration(consumerConfig)
                    .Build();

                _subscription = js.PushSubscribeAsync(_subject, OnMessage, false, pso);
                StatusChanged?.Invoke($"Ansluten via JetStream, laddar historik ({_subject})...");
            }
            catch (Exception jsEx)
            {
                // Ingen stream för subjectet – kör live utan historik
                _historyDone = true;
                _subscription = _connection.SubscribeAsync(_subject, OnMessage);
                StatusChanged?.Invoke($"Ansluten utan JetStream – bara live-data ({jsEx.Message})");
            }
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(ex);
        }
    }

    private void OnMessage(object? sender, MsgHandlerEventArgs args)
    {
        try
        {
            var msg = args.Message;
            var timestamp = DateTime.Now;

            if (msg.IsJetStream)
            {
                var meta = msg.MetaData;
                // Serverns lagringstid (UTC) – ger rätt tid även för uppspelad historik
                var ts = meta.Timestamp;
                timestamp = ts.Kind == DateTimeKind.Local
                    ? ts
                    : DateTime.SpecifyKind(ts, DateTimeKind.Utc).ToLocalTime();

                if (!_historyDone && meta.NumPending == 0)
                {
                    _historyDone = true;
                    StatusChanged?.Invoke("Historik laddad – tar emot live-data");
                }
            }

            var reading = Parse(Encoding.UTF8.GetString(msg.Data), timestamp);
            if (reading is not null)
            {
                ReadingReceived?.Invoke(reading);
            }
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(ex);
        }
    }

    /// <summary>
    /// Packar upp kuvertet. "value" är en JSON-sträng i dagens firmware,
    /// men ett inbäddat objekt accepteras också.
    /// </summary>
    internal static BarometerReading? Parse(string json, DateTime timestamp)
    {
        using var envelope = JsonDocument.Parse(json);
        var root = envelope.RootElement;

        if (root.TryGetProperty("type", out var type) && type.GetString() != "bme280")
            return null;

        var senderId = root.TryGetProperty("sender", out var s) ? s.GetString() ?? "" : "";

        if (!root.TryGetProperty("value", out var value))
            return null;

        JsonDocument? inner = null;
        try
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                inner = JsonDocument.Parse(value.GetString()!);
                value = inner.RootElement;
            }

            return new BarometerReading(
                timestamp,
                value.GetProperty("hpa").GetDouble(),
                value.GetProperty("temp").GetDouble(),
                value.GetProperty("humidity").GetDouble(),
                senderId);
        }
        finally
        {
            inner?.Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            _subscription?.Unsubscribe();
            _connection?.Close();
            _connection?.Dispose();
        }
        catch
        {
            // Stänger ner ändå
        }
    }
}
