using System;
using System.Collections.Concurrent;
using System.Collections.Generic; // Added for List<>
using System.IO;                  // Added for File writing
using System.Linq;                // Added for JSON formatting
using System.Net;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
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

        // Flag to ensure we only dump the database once
        private bool _hasDumped = false;

        private ConfigEntry<int> _serverPort;

        void Awake()
        {
            // 1. INITIALIZE CONFIGURATION
            _serverPort = Config.Bind("General",
                                      "ServerPort",
                                      4444,
                                      "The port the HTTP server listens on.");

            Logger.LogInfo($"Real Life Loot Mod Started! Listening on Port: {_serverPort.Value}");

            _serverThread = new Thread(StartServer);
            _serverThread.Start();
        }

        void Update()
        {
            // 2. CHECK FOR DUMP (Runs once when ObjectDB is ready)
            if (!_hasDumped && ObjectDB.instance != null && ObjectDB.instance.m_items.Count > 0)
            {
                DumpItemDatabase();
                _hasDumped = true;
            }

            // 3. PROCESS QUEUE (Server actions)
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
            _listener.Prefixes.Add($"http://*:{_serverPort.Value}/");

            try
            {
                _listener.Start();
                Logger.LogInfo($"Server started on port {_serverPort.Value}...");
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
            if (Player.m_localPlayer == null) return;

            GameObject prefab = ObjectDB.instance.GetItemPrefab(prefabName);
            if (prefab == null)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, $"<color=red>Unknown Item:</color> {prefabName}");
                return;
            }

            // Smart Spawning: Inventory or Ground
            if (Player.m_localPlayer.m_inventory.AddItem(prefab, amount))
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, $"Received {amount}x {prefabName}");
            }
            else
            {
                Vector3 spawnPos = Player.m_localPlayer.transform.position +
                                   (Player.m_localPlayer.transform.forward * 2f) +
                                   Vector3.up;

                UnityEngine.Object.Instantiate(prefab, spawnPos, Quaternion.identity);
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Inventory Full! Item dropped.");
            }
        }

        // 4. THE DUMP LOGIC
        private void DumpItemDatabase()
        {
            Logger.LogInfo("Dumping item database...");

            // 1. Define the Whitelist
            // We only include items that match these specific types
            var allowedTypes = new HashSet<ItemDrop.ItemData.ItemType>
    {
        ItemDrop.ItemData.ItemType.Material,
        ItemDrop.ItemData.ItemType.Consumable,
        ItemDrop.ItemData.ItemType.Ammo,
        ItemDrop.ItemData.ItemType.Trophy // Note: Valheim devs spell it 'Trophie' in the code!
    };

            var exportList = new List<ItemExport>();

            foreach (GameObject itemPrefab in ObjectDB.instance.m_items)
            {
                var itemDrop = itemPrefab.GetComponent<ItemDrop>();

                // 2. The Filter Check
                // If it's not null AND it's in our allowed list...
                if (itemDrop != null && allowedTypes.Contains(itemDrop.m_itemData.m_shared.m_itemType))
                {
                    string rawName = itemDrop.m_itemData.m_shared.m_name;
                    string localizedName = Localization.instance.Localize(rawName);

                    // Skip items with broken/empty names (often internal test items)
                    if (string.IsNullOrEmpty(localizedName) || localizedName.StartsWith("$"))
                        continue;

                    exportList.Add(new ItemExport
                    {
                        name = localizedName,
                        prefab = itemPrefab.name,
                        category = itemDrop.m_itemData.m_shared.m_itemType.ToString()
                    });
                }
            }

            // Sort the list alphabetically by name to make it nicer to read
            exportList = exportList.OrderBy(x => x.name).ToList();

            var jsonLines = exportList.Select(i =>
                $"\t{{ \"name\": \"{i.name}\", \"prefab\": \"{i.prefab}\", \"category\": \"{i.category}\" }}");

            string json = "[\n" + string.Join(",\n", jsonLines.ToArray()) + "\n]";

            string path = Path.Combine(Paths.ConfigPath, "valheim_items.json");
            File.WriteAllText(path, json);

            Logger.LogInfo($"Database dumped to: {path}");

            if (Player.m_localPlayer != null)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Item Database Dumped (Filtered)!");
            }
        }

        // 5. HELPER CLASS FOR JSON
        [Serializable]
        public class ItemExport
        {
            public string name;      // The human readable name (e.g., "Wood")
            public string prefab;    // The code name (e.g., "Wood")
            public string category;  // Weapon, Material, Consumable, etc.
        }
    }
}