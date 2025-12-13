using System;
using System.Collections.Concurrent;
using System.Net; // Fixes HttpListener
using System.Threading;
using BepInEx;
using UnityEngine;

namespace RealLifeLootMod
{
    [BepInPlugin("com.yourname.reallifeloot", "Real Life Loot", "1.0.0")]
    public class RealLifeLootPlugin : BaseUnityPlugin
    {
        // Thread-safe queue to pass data from Server Thread -> Game Thread
        private static ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        private HttpListener _listener;
        private Thread _serverThread;
        private bool _isRunning = true;
        private const int PORT = 4444;

        void Awake()
        {
            Logger.LogInfo("Real Life Loot Mod Started!");
            _serverThread = new Thread(StartServer);
            _serverThread.Start();
        }

        void Update()
        {
            // Run queued actions on the main game thread
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                action.Invoke();
            }
        }

        void OnDestroy()
        {
            _isRunning = false;
            _listener?.Stop();
            _serverThread?.Abort();
        }

        private void StartServer()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{PORT}/");

            try
            {
                _listener.Start();
                Logger.LogInfo($"Listening on port {PORT}...");
            }
            catch (Exception e)
            {
                Logger.LogError($"Server Error: {e.Message}");
                return;
            }

            while (_isRunning)
            {
                try
                {
                    var context = _listener.GetContext();
                    ProcessRequest(context);
                }
                catch (Exception) { }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            string item = request.QueryString["item"];
            string amountStr = request.QueryString["amount"];
            int amount = 1;
            int.TryParse(amountStr, out amount);

            if (!string.IsNullOrEmpty(item))
            {
                _mainThreadQueue.Enqueue(() =>
                {
                    SpawnItem(item, amount);
                });

                string responseString = $"Queued {amount} of {item}";
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }

            response.Close();
        }

        private void SpawnItem(string prefabName, int amount)
        {
            // CHECK 1: Is the Player object loaded?
            if (Player.m_localPlayer == null) return;

            // CHECK 2: Does the item exist in the Game Database?
            GameObject prefab = ObjectDB.instance.GetItemPrefab(prefabName);
            if (prefab == null)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, $"Unknown Item: {prefabName}");
                return;
            }

            // CHECK 3: Add to inventory
            if (Player.m_localPlayer.m_inventory.AddItem(prefab, amount))
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, $"Received {amount} {prefabName}");
            }
        }
    }
}