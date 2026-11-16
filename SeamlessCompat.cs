using System;
using System.Linq;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace SkyCoop
{
    // Optional support for SeamlessInteriors, through its public SeamlessInteriorsApi only (looked up by reflection, so
    // SkyCoop has no dependency on it). With it, buildings are part of the region scene: the scene key of a player
    // inside one stays the region, and the building's content is created when the player comes near. Items lying in a
    // building are therefore shared per building (SceneItemsSync) and not with the region's own floor record.
    public static class SeamlessCompat
    {
        const int SupportedApi = 1;

        static MethodInfo s_InteriorOf = null;
        static MethodInfo s_PlayerInterior = null;
        static MethodInfo s_InteriorRoot = null;
        static MethodInfo s_InteriorOpen = null;
        static PropertyInfo s_InteriorsReady = null;

        public static bool Loaded { get { return s_InteriorOf != null; } }

        public static void Init()
        {
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SeamlessInteriors");
            Type api = asm?.GetType("SeamlessInteriors.SeamlessInteriorsApi");
            if (api == null)
            {
                return;
            }
            object version = api.GetField("ApiVersion")?.GetRawConstantValue();
            if (!(version is int v) || v != SupportedApi)
            {
                MelonLogger.Warning("[SeamlessInteriors] API version " + (version ?? "?") + " is not supported (expected " + SupportedApi + "); building items are synced with the region.");
                return;
            }
            try
            {
                MethodInfo interiorOf = api.GetMethod("GetInteriorIdOf", new[] { typeof(GameObject) });
                MethodInfo playerInterior = api.GetMethod("GetPlayerInteriorId", Type.EmptyTypes);
                MethodInfo interiorRoot = api.GetMethod("GetInteriorRoot", new[] { typeof(string) });
                MethodInfo interiorOpen = api.GetMethod("IsInteriorOpen", new[] { typeof(string) });
                PropertyInfo interiorsReady = api.GetProperty("AreInteriorsReady");
                if (interiorOf == null || playerInterior == null || interiorRoot == null || interiorOpen == null || interiorsReady == null)
                {
                    MelonLogger.Warning("[SeamlessInteriors] API members missing; building items are synced with the region.");
                    return;
                }
                s_InteriorOf = interiorOf;
                s_PlayerInterior = playerInterior;
                s_InteriorRoot = interiorRoot;
                s_InteriorOpen = interiorOpen;
                s_InteriorsReady = interiorsReady;
                MelonLogger.Msg("[SeamlessInteriors] Found; items in buildings are shared per building.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SeamlessInteriors] Could not use its API: " + e.Message);
            }
        }

        static bool s_CallFailed = false;

        // These are polled every second next to the fire and spawner sync; a throwing API answers null instead of
        // stopping those, and is reported once.
        static object Call(MethodInfo m, params object[] args)
        {
            try
            {
                return m.Invoke(null, args);
            }
            catch (Exception e)
            {
                if (!s_CallFailed)
                {
                    s_CallFailed = true;
                    MelonLogger.Warning("[SeamlessInteriors] " + m.Name + " failed: " + (e.InnerException ?? e).Message);
                }
                return null;
            }
        }

        // Id of the SeamlessInteriors building the object belongs to, null outside buildings or without the mod.
        public static string InteriorOf(GameObject go)
        {
            return s_InteriorOf != null && go != null ? Call(s_InteriorOf, go) as string : null;
        }

        // Root object of a building's content; SeamlessInteriors saves the gear under it with the building.
        public static GameObject InteriorRoot(string interior)
        {
            return s_InteriorRoot != null && !string.IsNullOrEmpty(interior) ? Call(s_InteriorRoot, interior) as GameObject : null;
        }

        // The building's content is built and shown, and no building is being set up: its items can be read.
        public static bool IsReady(string interior)
        {
            return s_InteriorOpen != null && s_InteriorsReady.GetValue(null) is bool all && all && Call(s_InteriorOpen, interior) is bool open && open;
        }

        // Id of the building the player is in, null outdoors or without the mod.
        public static string PlayerInterior()
        {
            return s_PlayerInterior != null ? Call(s_PlayerInterior) as string : null;
        }
    }
}
