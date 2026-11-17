using System;
using System.Linq;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace SkyCoop
{
    // Optional support for SeamlessInteriors, through its public SeamlessInteriorsApi plus one internal list for window
    // views (all looked up by reflection, so SkyCoop has no dependency on it). With it, buildings are part of the region scene: the scene key of a player
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
                s_InteriorAt = api.GetMethod("GetInteriorIdAt", new[] { typeof(Vector3) });
                s_InteriorIds = api.GetMethod("GetInteriorIds", Type.EmptyTypes);
                s_InteriorScene = api.GetMethod("GetInteriorSceneName", new[] { typeof(string) });
                MelonLogger.Msg("[SeamlessInteriors] Found; items in buildings are shared per building.");
                InitWindowViews(asm);
                InitDoors(asm);
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

        static MethodInfo s_InteriorAt = null;
        static MethodInfo s_InteriorIds = null;

        // Ids of every building SeamlessInteriors supports.
        public static string[] InteriorIds()
        {
            return (s_InteriorIds != null ? Call(s_InteriorIds) as string[] : null) ?? new string[0];
        }

        // Id of the building whose room holds the position, null outside rooms or without the mod.
        public static string InteriorAt(Vector3 pos)
        {
            return s_InteriorAt != null ? Call(s_InteriorAt, pos) as string : null;
        }

        // A room is not at its building: it stands parked away from it, and from outside the building's windows show it
        // through cameras that draw only the layer the room is moved to while it is looked at. The public API does not
        // tell that layer, so it is read from the mod's own list of looked-at buildings; without it players in a room
        // are not drawn in its windows.
        static FieldInfo s_ViewBuildings = null;
        static FieldInfo s_ViewInstanceId = null;
        static FieldInfo s_ViewBorrowed = null;
        static FieldInfo s_ViewLayer = null;

        static void InitWindowViews(Assembly asm)
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;
            Type mod = asm.GetType("SeamlessInteriors.SeamlessInteriorsMod");
            Type building = mod?.GetNestedType("OutsideInBuilding", Any);
            FieldInfo list = mod?.GetField("s_OutsideInBuildings", Any | BindingFlags.Static);
            FieldInfo id = building?.GetField("InstanceId", Any | BindingFlags.Instance);
            FieldInfo borrowed = building?.GetField("Borrowed", Any | BindingFlags.Instance);
            FieldInfo layer = building?.GetField("Layer", Any | BindingFlags.Instance);
            if (list == null || id == null || borrowed == null || layer == null)
            {
                MelonLogger.Warning("[SeamlessInteriors] Window views not found; players in a room are not seen through its windows.");
                return;
            }
            s_ViewBuildings = list;
            s_ViewInstanceId = id;
            s_ViewBorrowed = borrowed;
            s_ViewLayer = layer;
        }

        // The player gets into and out of a room only through the building's doors: SeamlessInteriors switches its
        // inside/outside state (room shown, shell hidden, sound) in its door hook, a plain teleport leaves that state behind.
        // The public API has no door lookup, so the building shell is read from the mod's list of buildings.
        static MethodInfo s_InteriorScene = null;
        static FieldInfo s_Buildings = null;
        static FieldInfo s_BuildingShell = null;

        static void InitDoors(Assembly asm)
        {
            Type mod = asm.GetType("SeamlessInteriors.SeamlessInteriorsMod");
            Type instance = asm.GetType("SeamlessInteriors.SeamlessInteriorInstance");
            s_Buildings = mod?.GetField("ActiveInteriors", BindingFlags.Public | BindingFlags.Static);
            s_BuildingShell = instance?.GetField("ExteriorShell", BindingFlags.Public | BindingFlags.Instance);
            if (s_Buildings == null || s_BuildingShell == null || s_InteriorScene == null)
            {
                s_Buildings = null;
                MelonLogger.Warning("[SeamlessInteriors] Building doors not found; respawn in a building's room is not possible.");
            }
        }

        // No building is being set up, so rooms and doors can be used.
        public static bool AllReady()
        {
            return s_InteriorsReady != null && s_InteriorsReady.GetValue(null) is bool all && all;
        }

        // Door on the building's outside that leads into its room.
        public static LoadScene EntryDoor(string interior)
        {
            if (s_Buildings == null || string.IsNullOrEmpty(interior))
            {
                return null;
            }
            try
            {
                string scene = Call(s_InteriorScene, interior) as string;
                if (s_Buildings.GetValue(null) is System.Collections.IDictionary buildings && buildings.Contains(interior)
                    && s_BuildingShell.GetValue(buildings[interior]) is GameObject shell && shell != null)
                {
                    foreach (LoadScene door in shell.GetComponentsInChildren<LoadScene>(true))
                    {
                        if (door.m_SceneToLoad == scene)
                        {
                            return door;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                s_Buildings = null;
                MelonLogger.Warning("[SeamlessInteriors] Building doors failed: " + e.Message);
            }
            return null;
        }

        // Door in the room that leads out to the region.
        public static LoadScene ExitDoor(string interior)
        {
            GameObject room = InteriorRoot(interior);
            if (room == null)
            {
                return null;
            }
            foreach (LoadScene door in room.GetComponentsInChildren<LoadScene>(true))
            {
                if (!string.IsNullOrEmpty(door.m_SceneToLoad) && GameManager.IsOutDoorsScene(door.m_SceneToLoad))
                {
                    return door;
                }
            }
            return null;
        }

        // Layer the building's windows draw its room from while the room is looked at from outside, -1 otherwise.
        public static int WindowViewLayer(string interior)
        {
            if (s_ViewBuildings == null || string.IsNullOrEmpty(interior))
            {
                return -1;
            }
            try
            {
                if (s_ViewBuildings.GetValue(null) is System.Collections.IList buildings)
                {
                    foreach (object b in buildings)
                    {
                        if (b != null && s_ViewInstanceId.GetValue(b) as string == interior && s_ViewBorrowed.GetValue(b) is bool on && on)
                        {
                            return (int)s_ViewLayer.GetValue(b);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                s_ViewBuildings = null;
                MelonLogger.Warning("[SeamlessInteriors] Window views failed: " + e.Message);
            }
            return -1;
        }
    }
}
