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
                    string status = "";
                    string services = "";
                    try
                    {
                        using var completed = new ManualResetEventSlim(false);
                        bridge.EnqueueAction(() =>
                        {
                            try
                            {
                                status = bridge.GetStatusJson();
                                services = bridge.GetServicesJson();
                            }
                            catch { }
                            finally { completed.Set(); }
                        });
                        completed.Wait(3000);
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(status))
                    {
                        string payload = "{\"type\":\"telemetry\",\"status\":" +
                                         status + ",\"services\":" + services + "}";
                        await SendTextAsync(socket, payload, token);
                    }
                }

                await Task.Delay(5000, token);
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
                    // ── EXISTING ─────────────────────
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

                    // ── ZONE COMMANDS ─────────────────
                    case "zone":
                    {
                        string zoneType = ExtractJsonString(message, "zoneType");
                        float radius = ExtractFloatOptional(message, "radius", 500f);
                        int painted = 0;
                        RunOnSimulationThread(bridge, () => painted = bridge.PaintZone(zoneType, radius));
                        result = $"{{\"painted\":{painted},\"zoneType\":\"{EscapeJson(zoneType)}\",\"radius\":{radius.ToString(CultureInfo.InvariantCulture)}}}";
                        break;
                    }
                    case "dezone":
                    {
                        float radius = ExtractFloatOptional(message, "radius", 500f);
                        int removed = 0;
                        RunOnSimulationThread(bridge, () => removed = bridge.Dezone(radius));
                        result = $"{{\"removed\":{removed},\"radius\":{radius.ToString(CultureInfo.InvariantCulture)}}}";
                        break;
                    }
                    case "getzones":
                    {
                        float radius = ExtractFloatOptional(message, "radius", 1000f);
                        RunOnSimulationThread(bridge, () => result = bridge.GetZones(radius));
                        break;
                    }

                    // ── BULLDOZE COMMANDS ─────────────
                    case "bulldoze":
                    {
                        float radius = ExtractFloatOptional(message, "radius", 200f);
                        int demolished = 0;
                        RunOnSimulationThread(bridge, () => demolished = bridge.Bulldoze(radius));
                        result = $"{{\"demolished\":{demolished},\"radius\":{radius.ToString(CultureInfo.InvariantCulture)}}}";
                        break;
                    }

                    // ── BUDGET / TAX COMMANDS ──────────
                    case "getbudget":
                    {
                        RunOnSimulationThread(bridge, () => result = bridge.GetBudget());
                        break;
                    }
                    case "settax":
                    {
                        int index = ExtractInt(message, "index");
                        int rate = ExtractInt(message, "rate");
                        RunOnSimulationThread(bridge, () => result = bridge.SetTax(index, rate));
                        break;
                    }
                    case "setfee":
                    {
                        string resource = ExtractJsonString(message, "resource");
                        float fee = ExtractFloat(message, "fee");
                        RunOnSimulationThread(bridge, () => result = bridge.SetServiceFee(resource, fee));
                        break;
                    }

                    // ── POLICY COMMANDS ────────────────
                    case "getpolicies":
                    {
                        RunOnSimulationThread(bridge, () => result = bridge.GetPolicies());
                        break;
                    }
                    case "policy":
                    {
                        string policyName = ExtractJsonString(message, "name");
                        RunOnSimulationThread(bridge, () => result = bridge.TogglePolicy(policyName));
                        break;
                    }

                    // ── INFO COMMANDS ──────────────────
                    case "getbuildings":
                    {
                        float radius = ExtractFloatOptional(message, "radius", 1000f);
                        int limit = ExtractIntOptional(message, "limit", 50);
                        RunOnSimulationThread(bridge, () => result = bridge.GetBuildings(radius, limit));
                        break;
                    }
                    case "getzoneblocks":
                    {
                        float radius = ExtractFloatOptional(message, "radius", 1000f);
                        int limit = ExtractIntOptional(message, "limit", 50);
                        RunOnSimulationThread(bridge, () => result = bridge.GetZoneBlocks(radius, limit));
                        break;
                    }
                    case "getroads":
                    {
                        float radius = ExtractFloatOptional(message, "radius", 1000f);
                        int limit = ExtractIntOptional(message, "limit", 50);
                        RunOnSimulationThread(bridge, () => result = bridge.GetRoads(radius, limit));
                        break;
                    }

                    // ── BUILDING PLACEMENT ─────────────
                    case "place":
                    {
                        string prefabName = ExtractJsonString(message, "prefab");
                        float px = ExtractFloat(message, "x");
                        float py = ExtractFloat(message, "y");
                        float pz = ExtractFloat(message, "z");
                        float rot = ExtractFloatOptional(message, "rotation", 0f);
                        RunOnSimulationThread(bridge, () => result = bridge.PlaceBuilding(prefabName, px, py, pz, rot));
                        break;
                    }

                    // ── ROAD BUILDING ──────────────────
                    case "road":
                    {
                        string prefabName = ExtractJsonString(message, "prefab");
                        float x1 = ExtractFloat(message, "x1");
                        float y1 = ExtractFloat(message, "y1");
                        float z1 = ExtractFloat(message, "z1");
                        float x2 = ExtractFloat(message, "x2");
                        float y2 = ExtractFloat(message, "y2");
                        float z2 = ExtractFloat(message, "z2");
                        RunOnSimulationThread(bridge, () => result = bridge.BuildRoad(prefabName, x1, y1, z1, x2, y2, z2));
                        break;
                    }

                    // ── PREFAB LISTING ─────────────────
                    case "listprefabs":
                    {
                        string type = ExtractJsonString(message, "type");
                        int limit = ExtractIntOptional(message, "limit", 100);
                        RunOnSimulationThread(bridge, () => result = bridge.ListPrefabs(type, limit));
                        break;
                    }

                    // ── WEATHER COMMANDS ────────────────
                    case "getweather":
                    {
                        RunOnSimulationThread(bridge, () => result = bridge.GetWeather());
                        break;
                    }
                    case "setweather":
                    {
                        string prop = ExtractJsonString(message, "property");
                        float val = ExtractFloat(message, "value");
                        RunOnSimulationThread(bridge, () => result = bridge.SetWeather(prop, val));
                        break;
                    }

                    // ── SERVICE BUDGET COMMANDS ─────────
                    case "getservicebudgets":
                    {
                        RunOnSimulationThread(bridge, () => result = bridge.GetServiceBudgets());
                        break;
                    }
                    case "setservicebudget":
                    {
                        string service = ExtractJsonString(message, "service");
                        int budget = ExtractInt(message, "budget");
                        RunOnSimulationThread(bridge, () => result = bridge.SetServiceBudget(service, budget));
                        break;
                    }

                    // ── DISTRICT COMMANDS ───────────────
                    case "getdistricts":
                    {
                        RunOnSimulationThread(bridge, () => result = bridge.GetDistricts());
                        break;
                    }
                    case "getdistrictpolicies":
                    {
                        int idx = ExtractInt(message, "index");
                        RunOnSimulationThread(bridge, () => result = bridge.GetDistrictPolicies(idx));
                        break;
                    }

                    // ── POLLUTION COMMANDS ──────────────
                    case "getpollution":
                    {
                        float px = ExtractFloat(message, "x");
                        float pz = ExtractFloat(message, "z");
                        RunOnSimulationThread(bridge, () => result = bridge.GetPollution(px, pz));
                        break;
                    }

                    // ── NATURAL RESOURCES ───────────────
                    case "getresources":
                    {
                        float rx = ExtractFloat(message, "x");
                        float rz = ExtractFloat(message, "z");
                        RunOnSimulationThread(bridge, () => result = bridge.GetResources(rx, rz));
                        break;
                    }

                    // ── TRANSPORT LINE COMMANDS ─────────
                    case "gettransportlines":
                    {
                        RunOnSimulationThread(bridge, () => result = bridge.GetTransportLines());
                        break;
                    }

                    // ── BUILDING DETAIL COMMANDS ────────
                    case "getbuildinginfo":
                    {
                        float bx = ExtractFloat(message, "x");
                        float by = ExtractFloat(message, "y");
                        float bz = ExtractFloat(message, "z");
                        RunOnSimulationThread(bridge, () => result = bridge.GetBuildingInfo(bx, by, bz));
                        break;
                    }
                    case "demolishbuilding":
                    {
                        float dx = ExtractFloat(message, "x");
                        float dy = ExtractFloat(message, "y");
                        float dz = ExtractFloat(message, "z");
                        RunOnSimulationThread(bridge, () => result = bridge.DemolishBuilding(dx, dy, dz));
                        break;
                    }

                    // ── TERRAIN COMMANDS ────────────────
                    case "setheight":
                    {
                        float hx = ExtractFloat(message, "x");
                        float hz = ExtractFloat(message, "z");
                        float hr = ExtractFloat(message, "radius");
                        float hs = ExtractFloat(message, "strength");
                        RunOnSimulationThread(bridge, () => result = bridge.SetTerrainHeight(hx, hz, hr, hs));
                        break;
                    }
                    case "flatten":
                    {
                        float fx = ExtractFloat(message, "x");
                        float fz = ExtractFloat(message, "z");
                        float fr = ExtractFloat(message, "radius");
                        float fh = ExtractFloat(message, "height");
                        RunOnSimulationThread(bridge, () => result = bridge.FlattenTerrain(fx, fz, fr, fh));
                        break;
                    }

                    // ── VEHICLE SPAWN ───────────────────
                    case "spawnvehicle":
                    {
                        string vPrefab = ExtractJsonString(message, "prefab");
                        float vx = ExtractFloat(message, "x");
                        float vy = ExtractFloat(message, "y");
                        float vz = ExtractFloat(message, "z");
                        RunOnSimulationThread(bridge, () => result = bridge.SpawnVehicle(vPrefab, vx, vy, vz));
                        break;
                    }
                    case "spawnparkedvehicle":
                    {
                        string vpPrefab = ExtractJsonString(message, "prefab");
                        float vpx = ExtractFloat(message, "x");
                        float vpy = ExtractFloat(message, "y");
                        float vpz = ExtractFloat(message, "z");
                        RunOnSimulationThread(bridge, () => result = bridge.SpawnParkedVehicle(vpPrefab, vpx, vpy, vpz));
                        break;
                    }

                    // ── CITIZEN SPAWN ───────────────────
                    case "spawncitizens":
                    {
                        int scCount = ExtractInt(message, "count");
                        RunOnSimulationThread(bridge, () => result = bridge.SpawnCitizens(scCount));
                        break;
                    }

                    // ── ZONE DENSITY ────────────────────
                    case "setdensity":
                    {
                        float denx = ExtractFloat(message, "x");
                        float denz = ExtractFloat(message, "z");
                        float denr = ExtractFloat(message, "radius");
                        float denv = ExtractFloat(message, "density");
                        RunOnSimulationThread(bridge, () => result = bridge.SetDensity(denx, denz, denr, denv));
                        break;
                    }

                    // ── BUILDING UPGRADES ───────────────
                    case "installupgrade":
                    {
                        float ux = ExtractFloat(message, "x");
                        float uy = ExtractFloat(message, "y");
                        float uz = ExtractFloat(message, "z");
                        string uName = ExtractJsonString(message, "upgrade");
                        RunOnSimulationThread(bridge, () => result = bridge.InstallUpgrade(ux, uy, uz, uName));
                        break;
                    }

                    // ── ROAD SPEED ──────────────────────
                    case "setroadspeed":
                    {
                        float rsx = ExtractFloat(message, "x");
                        float rsz = ExtractFloat(message, "z");
                        float rsr = ExtractFloat(message, "radius");
                        float rss = ExtractFloat(message, "speed");
                        RunOnSimulationThread(bridge, () => result = bridge.SetRoadSpeed(rsx, rsz, rsr, rss));
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

            if (!completed.Wait(5000))
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

        private static int ExtractIntOptional(string json, string property, int defaultValue)
        {
            try { return ExtractInt(json, property); }
            catch { return defaultValue; }
        }

        private static float ExtractFloatOptional(string json, string property, float defaultValue)
        {
            try { return ExtractFloat(json, property); }
            catch { return defaultValue; }
        }

        private static string FormatCode(string code) =>
            code.Length == 6 ? code.Substring(0, 3) + "-" + code.Substring(3) : code;

        private static string EscapeJson(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
