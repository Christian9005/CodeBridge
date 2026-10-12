using System.Net.Sockets;

namespace CodeBridge.HomeAssistant;

/// <summary>The outcome of <see cref="HomeAssistantBrokerCheck"/>: <see cref="Reason"/> is one of <c>ok</c>, <c>auth</c>, <c>refused</c>, <c>not-found</c>, <c>timeout</c>, <c>error</c>.</summary>
public sealed record BrokerCheckResult(bool Ok, string Reason, string Message);

/// <summary>Tries the MQTT broker settings once and explains in plain words why they do not work.</summary>
public static class HomeAssistantBrokerCheck
{
    public static async Task<BrokerCheckResult> RunAsync(HomeAssistantOptions options, IMqttLink? link = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var own = link is null;
        link ??= new MqttNetLink();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));

            await link.ConnectAsync(options, "codebridge-test-" + Guid.NewGuid().ToString("N")[..6], options.BaseTopic + "/test/availability", "offline", limit.Token);
            await link.DisconnectAsync(CancellationToken.None);
            return new BrokerCheckResult(true, "ok", $"Connected to the MQTT broker at {options.Broker}:{options.Port}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Classify(ex, options);
        }
        finally
        {
            if (own)
                await link.DisposeAsync();
        }
    }

    /// <summary>Turns an MQTT connection failure into something the user can act on.</summary>
    internal static BrokerCheckResult Classify(Exception ex, HomeAssistantOptions options)
    {
        var where = $"{options.Broker}:{options.Port}";
        if (ex is OperationCanceledException)
            return new BrokerCheckResult(false, "timeout", $"{where} did not answer. Check the address and that this PC and Home Assistant are on the same network.");

        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return new BrokerCheckResult(false, "refused",
                    $"The machine is there, but nothing listens for MQTT on port {options.Port}. Home Assistant needs an MQTT broker: " +
                    "with Home Assistant OS install the 'Mosquitto broker' add-on; with Docker (Container) run an eclipse-mosquitto container. " +
                    "Then add the MQTT integration in Home Assistant.");
            }

            if (e is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData })
                return new BrokerCheckResult(false, "not-found", $"'{options.Broker}' could not be found. Check the address.");

            if (e is SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable })
                return new BrokerCheckResult(false, "timeout", $"{where} did not answer. Check the address and that this PC and Home Assistant are on the same network.");
        }

        var text = ex.ToString();
        if (text.Contains("BadUserNameOrPassword", StringComparison.OrdinalIgnoreCase) || text.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase))
        {
            return new BrokerCheckResult(false, "auth", string.IsNullOrEmpty(options.Username)
                ? "The broker requires a user name and password."
                : "The broker rejected the user name or password.");
        }

        return new BrokerCheckResult(false, "error", ex.Message);
    }
}
