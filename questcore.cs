using Il2CppAlta;
using Il2CppAlta.Console;
using Il2CppAlta.Core;
using Il2CppAlta.Liquid;
using Il2CppAlta.Networking;
using Il2CppAlta.Networking.Scripts.Player;
using Il2CppAlta.Networking.Servers;
using Il2CppAlta.Serialization;
using Il2CppAlta.StatSystem;
using HarmonyLib;


using MelonLoader;

using UnityEngine;
using Object = UnityEngine.Object;
using Stream = Il2CppAlta.Serialization.Stream;

[assembly: MelonInfo(typeof(hydrateordierate.Mod), "Hydrate or Dierate", "0.0.3", "TheRavenSeb")]
[assembly: MelonGame("Alta", "Att_Quest")]

namespace hydrateordierate
{
    public class Mod : MelonMod
    {
        public const int Type = 40;


        public override void OnInitializeMelon()
        {
           
            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("theravenseb.hydrateordierate");



            harmony.Patch(AccessTools.Method(typeof(ConnectToServerUtility), "SetupConnectionToServer"),
                postfix: new HarmonyMethod(typeof(ClientSide).GetMethod("OnConnection")));
        }



        public override void OnUpdate()
        {

            ClientSide.Tick();
        }

        public static void Codec(Stream s, ref uint kind, ref uint level)
        {
            s.SerializeBits(ref kind, 8);
            s.SerializeBits(ref level, 16);
        }

        public static void Send(Connection c, uint kind, uint level)
        {
            c.Send(null, (MessageType)Type,
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<SerializeConnectionMethod>(
                    new System.Action<Connection, Stream>((x, s) => Codec(s, ref kind, ref level))));
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
            connection.SetHandler((MessageType)Mod.Type, (SerializeConnectionMethod)OnState);
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
            if (level < 0f && helloTries < 5 && Time.time >= nextHello && server.IsApproved)
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
            if (mat == null || hunger == null || hunger.Material == null) return;
            mat.SetFloat("_FillProgress", level);
            mat.SetFloat("_NegativeChangeProgress", level);
            mat.SetFloat("_AlphaLimit", hunger.Material.GetFloat("_AlphaLimit"));
        }

        static void MakeBar()
        {
            foreach (StatBar b in Object.FindObjectsOfType<StatBar>())
            {
                StatDefinition def = b.trackedStatDefinition;
                if (def == null || def.Name != "hunger" || b.Material == null) continue;
                hunger = b;
                bar = Object.Instantiate(b.gameObject, b.transform.parent);
                bar.name = "player_statCylinder Thirst";
                Object.DestroyImmediate(bar.GetComponent<StatBar>());
                mat = new Material(b.Material);
                mat.SetColor("_FillColor", new UnityEngine.Color(0.1f, 0.55f, 0.81f));
                mat.SetFloat("_PositiveChangeProgress", 0f);
                Renderer renderer = bar.GetComponentInChildren<Renderer>(true);
                if (renderer == null)
                {
                    MelonLogger.Error("Hydrate or Dierate: Could not find a renderer on the cloned thirst bar.");
                    Object.Destroy(bar);
                    bar = null;
                    mat = null;
                    return;
                }
                renderer.sharedMaterial = mat;
                bar.transform.localPosition = b.transform.localPosition + new UnityEngine.Vector3(-0.016f, 0f, 0f);
                MelonLogger.Msg("Hydrate or Dierate: Thirst bar created.");
                break;
            }
        }
    }
}