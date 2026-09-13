using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CityMCP
{
    public class BridgeHttpServer
    {
        private HttpListener? m_Listener;
        private CancellationTokenSource? m_Cts;
        private readonly int m_Port;

        public BridgeHttpServer(int port = 2828)
        {
            m_Port = port;
        }

        public void Start()
        {
            try
            {
                m_Listener = new HttpListener();
                m_Listener.Prefixes.Add($"http://127.0.0.1:{m_Port}/");
                m_Listener.Prefixes.Add($"http://localhost:{m_Port}/");
                m_Listener.Start();

                m_Cts = new CancellationTokenSource();
                Task.Run(() => ListenLoop(m_Cts.Token));

                Mod.Log.Info($"CityMCP HTTP Server listening on port {m_Port}");
            }
            catch (Exception ex)
            {
                Mod.Log.Error($"Failed to start CityMCP HTTP server on port {m_Port}: {ex}");
            }
        }

        public void Stop()
        {
            try
            {
                m_Cts?.Cancel();
                m_Listener?.Stop();
                m_Listener?.Close();
            }
            catch { }
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && m_Listener != null && m_Listener.IsListening)
            {
                try
                {
                    var context = await m_Listener.GetContextAsync();
                    ProcessRequest(context);
                }
                catch (Exception)
                {
                    if (token.IsCancellationRequested) break;
                }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;

            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 200;
                res.Close();
                return;
            }

            string path = req.Url?.AbsolutePath.ToLowerInvariant() ?? "/";
            string responseText = "{\"error\": \"unknown_endpoint\"}";
            int statusCode = 404;

            try
            {
                if (path == "/" || path == "/api/status")
                {
                    if (BridgeSystem.Instance != null)
                    {
                        responseText = BridgeSystem.Instance.GetStatusJson();
                        statusCode = 200;
                    }
                    else
                    {
                        responseText = "{\"error\": \"bridge_system_not_ready\"}";
                        statusCode = 503;
                    }
                }
                else if (path == "/api/services")
                {
                    if (BridgeSystem.Instance != null)
                    {
                        responseText = BridgeSystem.Instance.GetServicesJson();
                        statusCode = 200;
                    }
                    else
                    {
                        responseText = "{\"error\": \"bridge_system_not_ready\"}";
                        statusCode = 503;
                    }
                }
                else if (path == "/api/traffic")
                {
                    if (BridgeSystem.Instance != null)
                    {
                        responseText = BridgeSystem.Instance.GetTrafficJson();
                        statusCode = 200;
                    }
                    else
                    {
                        responseText = "{\"error\": \"bridge_system_not_ready\"}";
                        statusCode = 503;
                    }
                }
                else if (path == "/api/clean" && req.HttpMethod == "POST")
                {
                    if (BridgeSystem.Instance != null)
                    {
                        int cleaned = 0;
                        var waitEvent = new ManualResetEventSlim(false);
                        BridgeSystem.Instance.EnqueueAction(() =>
                        {
                            try { cleaned = BridgeSystem.Instance.CleanRuins(); }
                            finally { waitEvent.Set(); }
                        });
                        waitEvent.Wait(2000);
                        responseText = $"{{\"status\": \"ok\", \"ruinsCleaned\": {cleaned}}}";
                        statusCode = 200;
                    }
                    else
                    {
                        responseText = "{\"error\": \"bridge_system_not_ready\"}";
                        statusCode = 503;
                    }
                }
                else if (path == "/api/money" && req.HttpMethod == "POST")
                {
                    string? amountStr = req.QueryString["amount"];
                    if (int.TryParse(amountStr, out int amount) && BridgeSystem.Instance != null)
                    {
                        BridgeSystem.Instance.EnqueueAction(() => BridgeSystem.Instance.AddMoney(amount));
                        responseText = $"{{\"status\": \"ok\", \"fundsAdded\": {amount}}}";
                        statusCode = 200;
                    }
                    else
                    {
                        responseText = "{\"error\": \"invalid_amount\"}";
                        statusCode = 400;
                    }
                }
                else if (path == "/api/devpoints" && req.HttpMethod == "POST")
                {
                    string? ptsStr = req.QueryString["amount"];
                    if (int.TryParse(ptsStr, out int pts) && BridgeSystem.Instance != null)
                    {
                        BridgeSystem.Instance.EnqueueAction(() => BridgeSystem.Instance.AddDevPoints(pts));
                        responseText = $"{{\"status\": \"ok\", \"devPointsAdded\": {pts}}}";
                        statusCode = 200;
                    }
                    else
                    {
                        responseText = "{\"error\": \"invalid_amount\"}";
                        statusCode = 400;
                    }
                }
                else if (path == "/api/time" && req.HttpMethod == "POST")
                {
                    string? hourStr = req.QueryString["hour"];
                    if (float.TryParse(hourStr, out float hour) && BridgeSystem.Instance != null)
                    {
                        BridgeSystem.Instance.EnqueueAction(() => BridgeSystem.Instance.SetTimeOfDay(hour));
                        responseText = $"{{\"status\": \"ok\", \"hour\": {hour:F1}}}";
                        statusCode = 200;
                    }
                    else
                    {
                        responseText = "{\"error\": \"invalid_hour_value\"}";
                        statusCode = 400;
                    }
                }
                else if (path == "/api/speed" && req.HttpMethod == "POST")
                {
                    string? valStr = req.QueryString["value"];
                    if (float.TryParse(valStr, out float speed) && BridgeSystem.Instance != null)
                    {
                        BridgeSystem.Instance.EnqueueAction(() => BridgeSystem.Instance.SetSpeed(speed));
                        responseText = $"{{\"status\": \"ok\", \"speed\": {speed}}}";
                        statusCode = 200;
                    }
                    else
                    {
                        responseText = "{\"error\": \"invalid_speed_value\"}";
                        statusCode = 400;
                    }
                }
                else if (path == "/api/screenshot" && req.HttpMethod == "POST")
                {
                    string targetFile = @"C:\Users\Owner\.gemini\antigravity\scratch\cs2_cam.png";
                    if (BridgeSystem.Instance != null)
                    {
                        BridgeSystem.Instance.EnqueueAction(() => BridgeSystem.Instance.CaptureScreenshot(targetFile));
                        responseText = $"{{\"status\": \"ok\", \"savedTo\": \"{targetFile.Replace("\\", "\\\\")}\"}}";
                        statusCode = 200;
                    }
                    else
                    {
                        responseText = "{\"error\": \"bridge_system_not_ready\"}";
                        statusCode = 503;
                    }
                }
                else if (path == "/api/camera" && req.HttpMethod == "POST")
                {
                    float.TryParse(req.QueryString["x"], out float x);
                    float.TryParse(req.QueryString["y"], out float y);
                    float.TryParse(req.QueryString["z"], out float z);
                    float.TryParse(req.QueryString["zoom"], out float zoom);

                    if (BridgeSystem.Instance != null)
                    {
                        BridgeSystem.Instance.EnqueueAction(() => BridgeSystem.Instance.MoveCamera(x, y, z, zoom));
                        responseText = $"{{\"status\": \"ok\", \"camera\": {{\"x\": {x}, \"y\": {y}, \"z\": {z}, \"zoom\": {zoom}}}}}";
                        statusCode = 200;
                    }
                }
            }
            catch (Exception ex)
            {
                responseText = $"{{\"error\": \"{ex.Message}\"}}";
                statusCode = 500;
            }

            byte[] buffer = Encoding.UTF8.GetBytes(responseText);
            res.ContentType = "application/json";
            res.ContentLength64 = buffer.Length;
            res.StatusCode = statusCode;
            res.OutputStream.Write(buffer, 0, buffer.Length);
            res.OutputStream.Close();
        }
    }
}
