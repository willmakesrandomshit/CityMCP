using System;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CityMCP
{
    public sealed class RelayClient
    {
        private const string RelayUrl = "wss://relay.wilbot.link/ws?role=host";
        private readonly object m_LifecycleLock = new object();
        private readonly SemaphoreSlim m_SendLock = new SemaphoreSlim(1, 1);

        private CancellationTokenSource? m_Cts;
        private ClientWebSocket? m_Socket;
        private Task? m_RunTask;

        public void Start()
        {
            lock (m_LifecycleLock)
            {
                if (m_RunTask != null && !m_RunTask.IsCompleted) return;
                m_Cts = new CancellationTokenSource();
                m_RunTask = Task.Run(() => RunAsync(m_Cts.Token));
            }
        }

        public void Reconnect()
        {
            Stop();
            Start();
        }

        public void Stop()
        {
            lock (m_LifecycleLock)
            {
                try { m_Cts?.Cancel(); } catch { }
                try { m_Socket?.Abort(); } catch { }
                try { m_Socket?.Dispose(); } catch { }
                m_Socket = null;
                m_RunTask = null;
                m_Cts?.Dispose();
                m_Cts = null;
            }
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                ClientWebSocket? socket = null;
                try
                {
                    Mod.Settings?.UpdatePairingCode("Connecting...");
                    socket = new ClientWebSocket();
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    m_Socket = socket;
                    await socket.ConnectAsync(new Uri(RelayUrl), token);

                    string registration = await ReceiveTextAsync(socket, token);
                    string displayCode = ExtractJsonString(registration, "displayCode");
                    if (string.IsNullOrEmpty(displayCode))
                        displayCode = FormatCode(ExtractJsonString(registration, "code"));

                    if (string.IsNullOrEmpty(displayCode))
                        throw new InvalidOperationException("Relay did not return a pairing code.");

                    Mod.Settings?.UpdatePairingCode(displayCode);
                    Mod.Log.Info($"CityMCP cloud relay registered. Link code: {displayCode}");

                    Task receiveTask = ReceiveLoopAsync(socket, token);
                    Task telemetryTask = TelemetryLoopAsync(socket, token);
                    await Task.WhenAny(receiveTask, telemetryTask);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn($"CityMCP relay disconnected: {ex.Message}");
                    Mod.Settings?.UpdatePairingCode("Relay offline - retrying...");
                }
                finally
                {
                    try { socket?.Abort(); } catch { }
                    try { socket?.Dispose(); } catch { }
                    if (ReferenceEquals(m_Socket, socket)) m_Socket = null;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task TelemetryLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                BridgeSystem? bridge = BridgeSystem.Instance;
                if (bridge != null)
                {
                    string payload = "{\"type\":\"telemetry\",\"status\":" +
                                     bridge.GetStatusJson() +
                                     ",\"services\":" +
                                     bridge.GetServicesJson() + "}";
                    await SendTextAsync(socket, payload, token);
                }

                await Task.Delay(1000, token);
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                string message = await ReceiveTextAsync(socket, token);
                if (ExtractJsonString(message, "type") != "command") continue;
                await HandleCommandAsync(socket, message, token);
            }
        }

        private async Task HandleCommandAsync(ClientWebSocket socket, string message, CancellationToken token)
        {
            string action = ExtractJsonString(message, "action");
            string id = ExtractJsonString(message, "id");
            string result = "{\"status\":\"ok\"}";
            bool ok = true;

            try
            {
                BridgeSystem bridge = BridgeSystem.Instance ?? throw new InvalidOperationException("City simulation is not ready.");
                switch (action)
                {
                    case "clean":
                    {
                        int cleaned = 0;
                        RunOnSimulationThread(bridge, () => cleaned = bridge.CleanRuins());
                        result = $"{{\"cleaned\":{cleaned}}}";
                        break;
                    }
                    case "money":
                    {
                        int amount = ExtractInt(message, "amount");
                        RunOnSimulationThread(bridge, () => bridge.AddMoney(amount));
                        result = $"{{\"fundsAdded\":{amount}}}";
                        break;
                    }
                    case "devpoints":
                    {
                        int amount = ExtractInt(message, "amount");
                        RunOnSimulationThread(bridge, () => bridge.AddDevPoints(amount));
                        result = $"{{\"devPointsAdded\":{amount}}}";
                        break;
                    }
                    case "time":
                    {
                        float hour = ExtractFloat(message, "hour");
                        RunOnSimulationThread(bridge, () => bridge.SetTimeOfDay(hour));
                        result = "{\"hour\":" + hour.ToString(CultureInfo.InvariantCulture) + "}";
                        break;
                    }
                    case "speed":
                    {
                        float value = ExtractFloat(message, "value");
                        RunOnSimulationThread(bridge, () => bridge.SetSpeed(value));
                        result = "{\"speed\":" + value.ToString(CultureInfo.InvariantCulture) + "}";
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"Unknown command: {action}");
                }
            }
            catch (Exception ex)
            {
                ok = false;
                result = "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
                Mod.Log.Warn($"Relay command '{action}' failed: {ex.Message}");
            }

            string response = "{\"type\":\"command_result\",\"id\":\"" + EscapeJson(id) +
                              "\",\"ok\":" + (ok ? "true" : "false") + ",\"result\":" + result + "}";
            await SendTextAsync(socket, response, token);
        }

        private static void RunOnSimulationThread(BridgeSystem bridge, Action action)
        {
            using var completed = new ManualResetEventSlim(false);
            Exception? error = null;
            bridge.EnqueueAction(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
                finally { completed.Set(); }
            });

            if (!completed.Wait(3000))
                throw new TimeoutException("The simulation did not process the command in time.");
            if (error != null)
                throw error;
        }

        private async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await m_SendLock.WaitAsync(token);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
            finally
            {
                m_SendLock.Release();
            }
        }

        private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("Relay closed the connection.");
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string ExtractJsonString(string json, string property)
        {
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*\\\"(?<v>(?:\\\\.|[^\\\"])*)\\\"");
            return match.Success ? Regex.Unescape(match.Groups["v"].Value) : string.Empty;
        }

        private static string ExtractJsonNumber(string json, string property)
        {
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*\\\"?(?<v>-?[0-9]+(?:\\.[0-9]+)?)\\\"?");
            if (!match.Success) throw new FormatException($"Missing numeric parameter '{property}'.");
            return match.Groups["v"].Value;
        }

        private static int ExtractInt(string json, string property) =>
            int.Parse(ExtractJsonNumber(json, property), NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static float ExtractFloat(string json, string property) =>
            float.Parse(ExtractJsonNumber(json, property), NumberStyles.Float, CultureInfo.InvariantCulture);

        private static string FormatCode(string code) =>
            code.Length == 6 ? code.Substring(0, 3) + "-" + code.Substring(3) : code;

        private static string EscapeJson(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
