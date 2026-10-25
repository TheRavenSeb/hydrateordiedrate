using System.Collections.Generic;
using System.Linq;
using Alta;
using Alta.Console;
using Alta.Liquid;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using Alta.Networking.Servers;
using Alta.Serialization;
using Alta.StatSystem;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;
using Stream = Alta.Serialization.Stream;

[assembly: MelonInfo(typeof(Hydrateordierate.Mod), "Hydrate or Dierate", "0.0.3", "TheRavenSeb")]
[assembly: MelonGame("Alta", "A Township Tale")]

namespace Hydrateordierate
{
    public class Mod : MelonMod
    {
        public const int Type = 40;
        static bool isServer;

        public override void OnInitializeMelon()
        {
            isServer = CommandLineArguments.Contains("/start_server");
            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("theravenseb.hydrateordierate");
            if (isServer) MelonLogger.Msg("Hydrate or Dierate:loaded on server Initialized.");

            if (isServer)
            {
                harmony.Patch(AccessTools.Method(typeof(PlayerDrinkingMouth), "LiquidAmountChanged"),
                    prefix: new HarmonyMethod(typeof(ServerSide).GetMethod("BeforeDrink")));
                harmony.Patch(AccessTools.Method(typeof(PlayerDrinkingMouth), "Initialize"),
                    postfix: new HarmonyMethod(typeof(ServerSide).GetMethod("AfterMouthReady")));
                MelonEvents.OnApplicationLateStart.Subscribe(RegisterCommands, 0, false);
            }

            else
                harmony.Patch(AccessTools.Method(typeof(ConnectToServerUtility), "SetupConnectionToServer"),
                    postfix: new HarmonyMethod(typeof(ClientSide).GetMethod("OnConnection")));
        }

        static void RegisterCommands()
        {
            CommandService.CommandCollection.Collect(typeof(ThirstCommands).Assembly);
            MelonLogger.Msg("Hydrate or Dierate: thirst console commands registered.");
        }

        public override void OnUpdate()
        {
            if (isServer) ServerSide.Tick();
            else ClientSide.Tick();
        }

        public static void Codec(Stream s, ref uint kind, ref uint level)
        {
            s.SerializeBits(ref kind, 8);
            s.SerializeBits(ref level, 16);
        }

        public static void Send(Connection c, uint kind, uint level)
        {
            c.Send(null, (MessageType)Type, delegate (Connection x, Stream s) { Codec(s, ref kind, ref level); });
        }
    }

    public static class HydrateApi
    {
        static readonly Dictionary<uint, float> thirstPerUnitByLiquidHash = new Dictionary<uint, float>();
        static readonly object gate = new object();

        public static bool RegisterLiquidThirst(uint liquidHash, float thirstPerUnit, string sourceModId = null, bool overwrite = true)
        {
            if (liquidHash == 0u) return false;
            if (thirstPerUnit < 0f) thirstPerUnit = 0f;

            lock (gate)
            {
                if (!overwrite && thirstPerUnitByLiquidHash.ContainsKey(liquidHash)) return false;
                thirstPerUnitByLiquidHash[liquidHash] = thirstPerUnit;
            }

            MelonLogger.Msg("Hydrate or Dierate: registered liquid " + liquidHash + " thirst/unit=" + thirstPerUnit.ToString("0.##") +
                (string.IsNullOrEmpty(sourceModId) ? string.Empty : " from " + sourceModId));
            return true;
        }

        public static bool RegisterLiquidThirst(LiquidDefinition liquid, float thirstPerUnit = 20f, string sourceModId = null, bool overwrite = true)
        {
            return liquid != null && RegisterLiquidThirst(liquid.Hash, thirstPerUnit, sourceModId, overwrite);
        }

        public static bool TryGetLiquidThirst(uint liquidHash, out float thirstPerUnit)
        {
            lock (gate)
            {
                return thirstPerUnitByLiquidHash.TryGetValue(liquidHash, out thirstPerUnit);
            }
        }

        public static bool TryGetLiquidThirst(LiquidDefinition liquid, out float thirstPerUnit)
        {
            if (liquid == null)
            {
                thirstPerUnit = 0f;
                return false;
            }

            return TryGetLiquidThirst(liquid.Hash, out thirstPerUnit);
        }
    }

    public static class ServerSide
    {
        static ISocket socket;
        static readonly Dictionary<Connection, float> thirst = new Dictionary<Connection, float>();
        static readonly Dictionary<int, float> saved = new Dictionary<int, float>();
        static float lastTick;
        static float lastHurt;
        static LiquidData water;
        public static float DrainSeconds = 5400f;

        public static void Tick()
        {
            if (socket == null)
            {
                ISocket current = Socket.Current;
                if (current == null || !current.IsServer || current.TransportSocket == null) return;
                socket = current;
                current.ConnectionCreated += Listen;
                foreach (Connection c in current.Connections) Listen(c);
            }

            if (Time.time - lastTick < 1f) return;
            float dt = Time.time - lastTick;
            lastTick = Time.time;
            bool hurtNow = Time.time - lastHurt >= 15f;
            if (hurtNow) lastHurt = Time.time;

            lock (thirst)
            {
                foreach (Connection c in new List<Connection>(thirst.Keys))
                {
                    Player p = c.Player as Player;
                    if (p == null || p.PlayerController == null) continue;
                    float t = Mathf.Max(0f, thirst[c] - dt * 100f / DrainSeconds);
                    thirst[c] = t;
                    if (t <= 0f && hurtNow) Hurt(p);
                    Mod.Send(c, 10u, (uint)(t * 10f));
                }
            }
        }

        static void Listen(Connection c)
        {
            c.SetHandler((MessageType)Mod.Type, OnHello);
            c.Disconnected += Forget;
            MelonLogger.Msg("Hydrate or Dierate:listening for connections.");
        }

        static void Forget(Connection c)
        {
            lock (thirst)
            {
                float t;
                int id = IdOf(c);
                if (id != 0 && thirst.TryGetValue(c, out t)) saved[id] = t;
                thirst.Remove(c);
            }
        }

        static void OnHello(Connection c, Stream s)
        {
            uint kind = 0, level = 0;
            Mod.Codec(s, ref kind, ref level);
            lock (thirst)
            {
                if (thirst.ContainsKey(c)) return;
                float t;
                thirst[c] = saved.TryGetValue(IdOf(c), out t) ? t : 100f;
            }
        }

        static int IdOf(Connection c)
        {
            return c.UserInfo == null ? 0 : c.UserInfo.Identifier;
        }

        static void Hurt(Player p)
        {
            Stat health;
            if (!p.PlayerController.Health.StatManager.GetStat("health", out health)) return;
            const float step = 0.1f;
            const float floor = 0.2f;
            if (health.Base <= floor) return;
            float next = Mathf.Max(health.Base - step, floor);
            health.ChangeData = next - health.Base;
            health.Base = next;
            MelonLogger.Msg("Hydrate or Dierate: " + NameOf(p) + " is dehydrated, health " + next.ToString("0.00"));
        }

        public static void BeforeDrink(PlayerDrinkingMouth __instance, ILiquidContainer container, int oldLevel, int newLevel)
        {
            if (newLevel <= oldLevel || container.ContainedLiquid == null) return;
            ConsumptionManager cm = Traverse.Create(__instance).Field("consumer").GetValue<ConsumptionManager>();
            if (cm == null || cm.Player == null) return;
            Player player = cm.Player.NetworkPlayer;
            if (player == null) return;

            int units = newLevel - oldLevel;
            float gain = PerUnit(container.ContainedLiquid) * units;

            lock (thirst)
            {
                foreach (Connection c in new List<Connection>(thirst.Keys))
                    if (object.ReferenceEquals(c.Player, player)) thirst[c] = Mathf.Min(100f, thirst[c] + gain);
            }
        }


        static float PerUnit(LiquidContent c)
        {
            float custom;
            if (c.Liquid != null && HydrateApi.TryGetLiquidThirst(c.Liquid.Hash, out custom))
                return custom;

            if (c.Liquid == null)
                return c.Data != null && c.Data.ChunkVisuals.Any() ? 25f : 10f;

            return c.Liquid.Hash == 44872u ? 40f : 10f;
        }

        public static void AfterMouthReady(PlayerDrinkingMouth __instance)
        {
            ILiquidContainer mouth = __instance.GetComponent<ILiquidContainer>();
            if (mouth == null) return;
            mouth.AddFilter += delegate (LiquidData data, ref bool result)
            {
                if (water == null) water = HashedGeneralValue<LiquidDefinition>.Get(44872u).Data;
                if (!result || data == null || data != water) return;
                Player player = PlayerOf(__instance);
                if (player != null && ThirstOf(player) >= 100f) result = false;
            };
        }

        static Player PlayerOf(PlayerDrinkingMouth mouth)
        {
            ConsumptionManager cm = Traverse.Create(mouth).Field("consumer").GetValue<ConsumptionManager>();
            return cm == null || cm.Player == null ? null : cm.Player.NetworkPlayer;
        }

        static float ThirstOf(Player player)
        {
            lock (thirst)
            {
                foreach (KeyValuePair<Connection, float> e in thirst)
                    if (object.ReferenceEquals(e.Key.Player, player)) return e.Value;
            }
            return 0f;
        }

        public static bool SetThirst(IPlayer player, float value)
        {
            lock (thirst)
            {
                foreach (Connection c in new List<Connection>(thirst.Keys))
                    if (object.ReferenceEquals(c.Player, player)) { thirst[c] = Mathf.Clamp(value, 0f, 100f); return true; }
            }
            return false;
        }

        public static bool TryGetThirst(IPlayer player, out float value)
        {
            lock (thirst)
            {
                foreach (KeyValuePair<Connection, float> e in thirst)
                    if (object.ReferenceEquals(e.Key.Player, player)) { value = e.Value; return true; }
            }
            value = 0f;
            return false;
        }

        public static string NameOf(Player p)
        {
            return p == null || p.UserInfo == null ? "(unknown)" : p.UserInfo.Username;
        }

        public static List<string> ListThirst()
        {
            List<string> lines = new List<string>();
            lock (thirst)
            {
                foreach (KeyValuePair<Connection, float> e in thirst)
                {
                    Player p = e.Key.Player as Player;
                    lines.Add((p == null ? "(no player yet)" : NameOf(p)) + ": " + e.Value.ToString("0.0"));
                }
            }
            return lines;
        }
    }

    [Module("thirst", "Hydrate or Dierate test tools")]
    public static class ThirstCommands
    {
        [Command("set", "Set thirst 0 to 100, e.g. thirst set Bob 10")]
        public static string Set(PlayerList players, float value)
        {
            int n = 0;
            foreach (IPlayer p in players) if (ServerSide.SetThirst(p, value)) n++;
            return "thirst " + Mathf.Clamp(value, 0f, 100f) + " for " + n + " player(s)";
        }

        [Command("get", "Show one player's thirst")]
        public static string Get(Player player)
        {
            float value;
            if (!ServerSide.TryGetThirst(player, out value)) return ServerSide.NameOf(player) + " is not tracked (no Hydrate or Dierate client)";
            return ServerSide.NameOf(player) + ": " + value.ToString("0.0");
        }

        [Command("list", "Show every tracked player's thirst")]
        public static string List()
        {
            List<string> lines = ServerSide.ListThirst();
            return lines.Count == 0 ? "no tracked players" : string.Join("\n", lines.ToArray());
        }

        [Command("drain", "Minutes for a full bar to empty, default 90")]
        public static string Drain(float minutes)
        {
            minutes = Mathf.Max(0.1f, minutes);
            ServerSide.DrainSeconds = minutes * 60f;
            return "a full bar now empties in " + minutes + " min";
        }
    }

    public static class ClientSide
    {
        static Connection server;
        static int helloTries;
        static float nextHello;
        static float nextScan;
        static float level = -1f;
        static GameObject bar;
        static Material mat;
        static StatBar hunger;

        public static void OnConnection(Connection connection)
        {
            server = connection;
            helloTries = 0;
            nextHello = 0f;
            level = -1f;
            connection.SetHandler((MessageType)Mod.Type, OnState);
            MelonLogger.Msg("Hydrate or Dierate: Connected to server, waiting for thirst state...");
        }

        static void OnState(Connection c, Stream s)
        {
            uint kind = 0, lvl = 0;
            Mod.Codec(s, ref kind, ref lvl);
            level = lvl / 1000f;
        }

        public static void Tick()
        {
            if (server == null) return;
            if (level < 0f && helloTries < 5 && Time.time >= nextHello && server.IsApproved && Player.Current != null)
            {
                Mod.Send(server, 0u, 0u);
                helloTries++;
                nextHello = Time.time + 3f;
            }

            if (level < 0f) return;
            if ((bar == null || hunger == null) && Time.time >= nextScan)
            {
                nextScan = Time.time + 1f;
                MakeBar();
            }
            if (bar == null) return;
            mat.SetFloat("_FillProgress", level);
            mat.SetFloat("_NegativeChangeProgress", level);
            mat.SetFloat("_AlphaLimit", hunger.Material.GetFloat("_AlphaLimit"));
        }

        static void MakeBar()
        {
            foreach (StatBar b in Object.FindObjectsOfType<StatBar>())
            {
                StatDefinition def = Traverse.Create(b).Field("trackedStatDefinition").GetValue<StatDefinition>();
                if (def == null || def.Name != "hunger" || b.Material == null) continue;
                hunger = b;
                bar = Object.Instantiate(b.gameObject, b.transform.parent);
                bar.name = "player_statCylinder Thirst";
                Object.DestroyImmediate(bar.GetComponent<StatBar>());
                mat = new Material(b.Material);
                mat.SetColor("_FillColor", new Color(0.1f, 0.55f, 0.81f));
                mat.SetFloat("_PositiveChangeProgress", 0f);
                bar.GetComponent<Renderer>().sharedMaterial = mat;
                bar.transform.localPosition = b.transform.localPosition + new Vector3(-0.016f, 0f, 0f);
                break;
            }
        }
    }
}
