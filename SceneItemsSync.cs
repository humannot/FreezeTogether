using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameServer;
using MelonLoader;
using UnityEngine;

namespace SkyCoop
{
    // Items lying in a scene are the same for every player. One player per scene owns them: the host when it is in
    // the scene, otherwise the player with the lowest id in it. The host keeps the owner's version per scene and sends
    // it to everybody entering the scene.
    //
    // Radial spawners (sticks, stones, limbs and the like): the owner's game spawns and respawns them with its own
    // rules and timers and sends a spawner's saved state (RadialObjectSpawner.Serialize) whenever it changes; the
    // others load that state into their spawner. An item taken from a spawner is removed from it on every client.
    //
    // Floor items (loose gear placed by the scene and its random spawns) all follow one record per area: the region
    // (key FloorKey) and, with SeamlessInteriors, every building (key FloorKey + ":" + building id), whose content is
    // created inside the region scene only when a player comes near. The first player to have an area ready records
    // which items lie where; the host keeps the first record it gets, sends it to everybody in the scene (the sender
    // included, as its confirmation) and to everybody entering later. Every other player applies it once: hides the
    // items that are not in it and creates the ones it lacks. Taking them is synced by the per-position pickup list.
    // Notes and story items (NarrativeCollectibleItem) are in no record.
    public static class SceneItemsSync
    {
        const int KindState = 0;
        const int KindPick = 1;
        const int KindSceneSent = 2;  // the host has sent everything it stores for the scene
        const int KindFloorPart = 3;  // part of a floor record
        const int KindFloorEnd = 4;   // floor record complete
        const int KindFire = 5;       // a fire's saved state (Fire.Serialize); pos.x = sender's hours played
        const int KindFireRemove = 6; // a campfire was dismantled

        const string FloorKey = "#floor"; // floor record stored next to the spawner states of a scene
        const string FirePrefix = "fire:"; // fire states are stored as FirePrefix + fire guid
        const int TickSeconds = 2;
        const int MaxSendsPerTick = 30;   // a first visit sends every spawner once; spread it over a few ticks
        const int FloorItemsPerPacket = 40;
        const float MatchDistance = 0.5f;

        // Host: scene -> spawner guid (or FloorKey) -> state, written by MPSaveManager.SaveRecentStuff.
        public static Dictionary<string, Dictionary<string, string>> Recent = new Dictionary<string, Dictionary<string, string>>();

        // Current scene: spawner guid -> state last sent or loaded, so only changes are sent.
        static readonly Dictionary<string, string> s_Known = new Dictionary<string, string>();
        // Items removed from a spawner here, so the pickup patches leave them out of the per-position pickup list.
        static readonly HashSet<int> s_Removed = new HashSet<int>();
        // floor records being received, by building id ("" = the region)
        static readonly Dictionary<string, StringBuilder> s_FloorIn = new Dictionary<string, StringBuilder>();
        static readonly Dictionary<string, StringBuilder> s_FloorFromClient = new Dictionary<string, StringBuilder>();
        // Client: building records received before this player entered the building, applied on entering it.
        static readonly Dictionary<string, string> s_PendingFloor = new Dictionary<string, string>();
        // Client: records this player sent and waits to get back from the host, by building id ("" = the region).
        static readonly Dictionary<string, string> s_FloorSent = new Dictionary<string, string>();
        static bool s_Ready = false;     // stored states of this scene are loaded; until then the owner must not send
        static bool s_ApplyingRemote = false;
        static int s_Tick = 0;

        // ---------- host storage ----------

        public static Dictionary<string, string> Load(string scene)
        {
            Dictionary<string, string> dict;
            if (Recent.TryGetValue(scene, out dict))
            {
                return dict;
            }
            string content = MPSaveManager.LoadData(MPSaveManager.GetKeyTemplate(MPSaveManager.SaveKeyTemplateType.SceneItems, scene), MPSaveManager.GetSeed());
            dict = content != "" ? TinyJSON.JSON.Load(content).Make<Dictionary<string, string>>() : null;
            if (dict == null)
            {
                dict = new Dictionary<string, string>();
            }
            Recent[scene] = dict;
            return dict;
        }

        static void Store(string scene, string key, string state)
        {
            Load(scene)[key] = state;
        }

        // interior: SeamlessInteriors building id, "" for the region's own record
        static string FloorKeyOf(string interior)
        {
            return interior.Length == 0 ? FloorKey : FloorKey + ":" + interior;
        }

        static string StoredFloor(string scene, string interior)
        {
            string floor;
            return Load(scene).TryGetValue(FloorKeyOf(interior), out floor) ? floor : null;
        }

        static StringBuilder Buffer(Dictionary<string, StringBuilder> dict, string key)
        {
            StringBuilder sb;
            if (!dict.TryGetValue(key, out sb))
            {
                sb = new StringBuilder();
                dict[key] = sb;
            }
            return sb;
        }

        // Scenes whose floor record this save already follows. The record is applied once per player and scene:
        // items that appear on the floor later (meat quarters, beachcombing finds) are not in it and must stay.
        const string FloorAppliedKey = "SceneItemsFloorApplied";
        static HashSet<string> s_FloorApplied = null;
        static int s_FloorAppliedSeed = 0; // the list belongs to one save

        static HashSet<string> FloorApplied()
        {
            int seed = MPSaveManager.GetSeed();
            if (s_FloorApplied == null || s_FloorAppliedSeed != seed)
            {
                string content = MPSaveManager.LoadData(FloorAppliedKey, seed);
                List<string> list = content != "" ? TinyJSON.JSON.Load(content).Make<List<string>>() : null;
                s_FloorApplied = new HashSet<string>(list ?? new List<string>());
                s_FloorAppliedSeed = seed;
            }
            return s_FloorApplied;
        }

        // the region's record is listed under the scene name, a building's under the scene name and its record key
        static string DoneKey(string scene, string interior)
        {
            return interior.Length == 0 ? scene : scene + FloorKeyOf(interior);
        }

        static bool FloorDone(string scene, string interior)
        {
            return FloorApplied().Contains(DoneKey(scene, interior));
        }

        static void MarkFloorDone(string scene, string interior)
        {
            if (FloorApplied().Add(DoneKey(scene, interior)))
            {
                MPSaveManager.ValidateRootExits();
                MPSaveManager.SaveData(FloorAppliedKey, TinyJSON.JSON.Dump(FloorApplied().ToList()), MPSaveManager.GetSeed());
            }
        }

        // ---------- scene ----------

        // Called once the scene's own data is loaded. The host applies what it stores right away (and records the
        // floor on a first visit); a client waits for the host to send it with the rest of the scene.
        public static void OnSceneLoaded()
        {
            s_Known.Clear();
            s_SpawnerByGuid.Clear();
            s_KnownFire.Clear();
            s_Removed.Clear();
            s_FloorIn.Clear();
            s_PendingFloor.Clear();
            s_FloorSent.Clear();
            s_OpenRooms.Clear();
            s_FireAppliedIn = null;
            s_Ready = false;
            if (!MyMod.InOnline())
            {
                return;
            }
            string scene = MyMod.level_guid;
            if (!MyMod.iAmHost)
            {
                return;
            }
            foreach (var item in Load(scene).ToList())
            {
                if (item.Key.StartsWith(FirePrefix))
                {
                    OnFireState(item.Key.Substring(FirePrefix.Length), item.Value);
                }
                else if (!item.Key.StartsWith(FloorKey))
                {
                    Apply(item.Key, item.Value);
                }
            }
            s_Ready = true;
            CheckArea("");
        }

        // One area's record: the region ("") once the scene's stored data is in, a building once this player is in it
        // and its content is ready. Polled rather than taken from the entered event: the player can be marked inside
        // while the building is still being set up (a save loaded inside one), and an empty building must not become
        // the record. The record is applied once; when there is none, this player's items become it.
        static void CheckArea(string interior)
        {
            string scene = MyMod.level_guid;
            if (FloorDone(scene, interior)
                || (interior.Length > 0 && (SeamlessCompat.PlayerInterior() != interior || !SeamlessCompat.IsReady(interior))))
            {
                return;
            }
            string floor;
            if (MyMod.iAmHost)
            {
                floor = StoredFloor(scene, interior);
            }
            else if (!s_PendingFloor.TryGetValue(interior, out floor))
            {
                floor = null;
            }
            if (floor != null)
            {
                s_PendingFloor.Remove(interior);
                string sent;
                // this player's own record came back: its items already match, and applying it would bring back the
                // items picked up while waiting
                if (!s_FloorSent.TryGetValue(interior, out sent) || sent != floor)
                {
                    ApplyFloor(floor, interior);
                }
                s_FloorSent.Remove(interior);
                MarkFloorDone(scene, interior);
                return;
            }
            if (s_FloorSent.ContainsKey(interior))
            {
                return; // waiting for the host to confirm it or send the record that came first
            }
            floor = BuildFloor(interior);
            if (MyMod.iAmHost)
            {
                Store(scene, FloorKeyOf(interior), floor);
                MarkFloorDone(scene, interior);
            }
            else
            {
                s_FloorSent[interior] = floor;
            }
            foreach (string part in FloorParts(floor))
            {
                Send(KindFloorPart, interior, part, Vector3.zero);
            }
            Send(KindFloorEnd, interior, "", Vector3.zero);
        }

        // Client: a complete record arrived; CheckArea applies it once this player is in that area.
        static void OnFloorReceived(string scene, string interior, string floor)
        {
            if (scene != MyMod.level_guid || MyMod.iAmHost || FloorDone(scene, interior))
            {
                return;
            }
            s_PendingFloor[interior] = floor;
            if (s_Ready)
            {
                CheckArea(interior);
            }
        }

        // Host: a client entered a scene.
        public static void SendSceneTo(int client, string scene)
        {
            // parts of a record the client did not finish before changing scene
            foreach (string key in s_FloorFromClient.Keys.Where(k => k.StartsWith(client + "|")).ToList())
            {
                s_FloorFromClient.Remove(key);
            }
            foreach (var item in Load(scene).ToList())
            {
                if (item.Key.StartsWith(FirePrefix))
                {
                    ServerSendTo(client, KindFire, scene, item.Key.Substring(FirePrefix.Length), item.Value, Clock());
                }
                else if (item.Key.StartsWith(FloorKey))
                {
                    // the region's record (key FloorKey) and the buildings' (FloorKey + ":" + id)
                    string interior = item.Key.Length > FloorKey.Length ? item.Key.Substring(FloorKey.Length + 1) : "";
                    foreach (string part in FloorParts(item.Value))
                    {
                        ServerSendTo(client, KindFloorPart, scene, interior, part, Vector3.zero);
                    }
                    ServerSendTo(client, KindFloorEnd, scene, interior, "", Vector3.zero);
                }
                else
                {
                    ServerSendTo(client, KindState, scene, item.Key, item.Value, Vector3.zero);
                }
            }
            ServerSendTo(client, KindSceneSent, scene, "", "", Vector3.zero);
        }

        static bool IsOwner()
        {
            if (MyMod.iAmHost)
            {
                return true;
            }
            for (int i = 0; i < ClientUser.myId && i < MyMod.playersData.Count; i++)
            {
                DataStr.MultiPlayerClientData p = MyMod.playersData[i];
                // m_Used: still listed in the host's last player status, so a disconnected player does not keep the scene
                if (p != null && p.m_Used && p.m_Levelid == MyMod.levelid && p.m_LevelGuid == MyMod.level_guid)
                {
                    return false;
                }
            }
            return true;
        }

        // ---------- pickups in SeamlessInteriors rooms ----------
        //
        // A room's content is built from SeamlessInteriors' own save of this player each time the room opens, and a
        // pickup that arrived while it was closed found nothing to hide. Each time a room opens, the scene's pickups
        // lying in it are applied again: the host's stored list, or on a client every pickup it was sent for the scene.

        static readonly HashSet<string> s_OpenRooms = new HashSet<string>();
        static readonly Dictionary<string, Dictionary<long, DataStr.PickedGearSync>> s_PickedSent = new Dictionary<string, Dictionary<long, DataStr.PickedGearSync>>();

        public static void OnPickupReceived(DataStr.PickedGearSync picked)
        {
            if (MyMod.iAmHost || picked == null || string.IsNullOrEmpty(picked.m_LevelGUID))
            {
                return;
            }
            if (!s_PickedSent.TryGetValue(picked.m_LevelGUID, out Dictionary<long, DataStr.PickedGearSync> scene))
            {
                scene = new Dictionary<long, DataStr.PickedGearSync>();
                s_PickedSent[picked.m_LevelGUID] = scene;
            }
            scene[MPSaveManager.GetPickedGearKey(picked)] = picked;
        }

        static void ApplyPickupsInOpenedRooms()
        {
            if (!SeamlessCompat.Loaded)
            {
                return;
            }
            foreach (string room in SeamlessCompat.InteriorIds())
            {
                if (!SeamlessCompat.IsReady(room))
                {
                    s_OpenRooms.Remove(room);
                    continue;
                }
                if (!s_OpenRooms.Add(room))
                {
                    continue;
                }
                Dictionary<long, DataStr.PickedGearSync> picked = null;
                if (MyMod.iAmHost)
                {
                    picked = MPSaveManager.LoadPickedGearsData(MyMod.level_guid);
                }
                else
                {
                    s_PickedSent.TryGetValue(MyMod.level_guid, out picked);
                }
                ApplyFiresAgain();
                if (picked == null)
                {
                    continue;
                }
                foreach (DataStr.PickedGearSync p in picked.Values.ToList())
                {
                    if (SeamlessCompat.InteriorAt(p.m_Spawn) == room)
                    {
                        MyMod.RemovePickedGear(p);
                    }
                }
            }
        }

        public static void EverySecond()
        {
            if (s_Ready && MyMod.InOnline())
            {
                ApplyPickupsInOpenedRooms();
                CheckArea("");
                string interior = SeamlessCompat.PlayerInterior();
                if (!string.IsNullOrEmpty(interior))
                {
                    CheckArea(interior);
                    // a building already open (seen from outside) is not opened again on entry
                    if (interior != s_FireAppliedIn && SeamlessCompat.IsReady(interior))
                    {
                        s_FireAppliedIn = interior;
                        ApplyFiresAgain();
                    }
                }
                else
                {
                    s_FireAppliedIn = null;
                }
            }
            if (++s_Tick < TickSeconds)
            {
                return;
            }
            s_Tick = 0;
            if (!s_Ready || !MyMod.InOnline())
            {
                return;
            }
            PollFires();
            if (!IsOwner() || RadialSpawnManager.m_RadialSpawnObjects == null)
            {
                return;
            }
            int sent = 0;
            foreach (RadialObjectSpawner s in RadialSpawnManager.m_RadialSpawnObjects)
            {
                if (s == null)
                {
                    continue;
                }
                string guid = GuidOf(s);
                if (string.IsNullOrEmpty(guid))
                {
                    continue;
                }
                string state = s.Serialize();
                string known;
                if (s_Known.TryGetValue(guid, out known) && known == state)
                {
                    continue;
                }
                s_Known[guid] = state;
                Send(KindState, guid, state, Vector3.zero);
                if (++sent >= MaxSendsPerTick)
                {
                    break;
                }
            }
        }

        // ---------- radial spawners ----------

        static string GuidOf(RadialObjectSpawner s)
        {
            return ObjectGuid.MaybeGetGuidFromGameObject(s.gameObject);
        }

        // guid -> spawner of the current scene, built on first use: a scene entry applies one state per spawner.
        static readonly Dictionary<string, RadialObjectSpawner> s_SpawnerByGuid = new Dictionary<string, RadialObjectSpawner>();

        static RadialObjectSpawner Find(string guid)
        {
            RadialObjectSpawner s;
            if (s_SpawnerByGuid.TryGetValue(guid, out s) && s != null)
            {
                return s;
            }
            if (RadialSpawnManager.m_RadialSpawnObjects == null)
            {
                return null;
            }
            s_SpawnerByGuid.Clear();
            foreach (RadialObjectSpawner sp in RadialSpawnManager.m_RadialSpawnObjects)
            {
                string g = sp != null ? GuidOf(sp) : null;
                if (!string.IsNullOrEmpty(g))
                {
                    s_SpawnerByGuid[g] = sp;
                }
            }
            return s_SpawnerByGuid.TryGetValue(guid, out s) ? s : null;
        }

        static RadialObjectSpawner SpawnerOf(GameObject go)
        {
            if (go == null || RadialSpawnManager.m_RadialSpawnObjects == null)
            {
                return null;
            }
            foreach (RadialObjectSpawner s in RadialSpawnManager.m_RadialSpawnObjects)
            {
                if (s != null && s.m_Spawns != null && s.m_Spawns.Contains(go))
                {
                    return s;
                }
            }
            return null;
        }

        // Called by the pickup patches before the item leaves the ground (picking up does not call RemoveFromSpawns;
        // the spawner only stops saving the item). Returns true for a spawner's item: its pickup is synced here and
        // not by the per-position pickup list.
        public static bool OnPickup(GameObject go)
        {
            if (!MyMod.InOnline() || go == null)
            {
                return false;
            }
            if (s_Removed.Contains(go.GetInstanceID()))
            {
                return true;
            }
            RadialObjectSpawner s = SpawnerOf(go);
            if (s == null)
            {
                return false;
            }
            ReportRemoved(s, go);
            return true;
        }

        static void ReportRemoved(RadialObjectSpawner s, GameObject go)
        {
            string guid = GuidOf(s);
            if (string.IsNullOrEmpty(guid) || !s_Removed.Add(go.GetInstanceID()))
            {
                return;
            }
            Send(KindPick, guid, "", go.transform.position);
        }

        // A picked-up item stays in its spawner's list; take it out before the spawner releases its objects to the
        // pool, or the item would be pulled out of the player's inventory.
        static void DetachTaken(RadialObjectSpawner s)
        {
            for (int i = s.m_Spawns.Count - 1; i >= 0; i--)
            {
                GameObject go = s.m_Spawns[i];
                GearItem gi = go != null ? go.GetComponent<GearItem>() : null;
                if (go == null || (gi != null && (gi.m_BeenInPlayerInventory || gi.m_InPlayerInventory)))
                {
                    s.m_Spawns.RemoveAt(i);
                }
            }
        }

        public static void Apply(string guid, string state)
        {
            s_Known[guid] = state;
            RadialObjectSpawner s = Find(guid);
            if (s == null || s.Serialize() == state)
            {
                return;
            }
            s_ApplyingRemote = true;
            try
            {
                DetachTaken(s);
                s.ReleaseSpawnedObjectsToPool();
                s.Deserialize(state);
            }
            finally
            {
                s_ApplyingRemote = false;
            }
        }

        public static void RemoveNear(string guid, Vector3 pos)
        {
            RadialObjectSpawner s = Find(guid);
            if (s == null || s.m_Spawns == null)
            {
                return;
            }
            DetachTaken(s);
            GameObject best = null;
            float bestDist = MatchDistance;
            foreach (GameObject go in s.m_Spawns)
            {
                if (go == null || !go.activeSelf)
                {
                    continue;
                }
                float d = Vector3.Distance(go.transform.position, pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = go;
                }
            }
            if (best == null)
            {
                return;
            }
            s_ApplyingRemote = true;
            try
            {
                s.RemoveFromSpawns(best);
                BreakDown bd = best.GetComponent<BreakDown>();
                if (bd != null)
                {
                    BreakDown.m_BreakDownObjects.Remove(bd);
                }
                RadialSpawnManager.ReturnToObjectPool(best);
            }
            finally
            {
                s_ApplyingRemote = false;
            }
        }

        // Runs remote removals (e.g. a limb broken by another player) without reporting them back.
        public static void RunRemote(System.Action action)
        {
            bool was = s_ApplyingRemote;
            s_ApplyingRemote = true;
            try { action(); }
            finally { s_ApplyingRemote = was; }
        }

        [HarmonyLib.HarmonyPatch(typeof(RadialObjectSpawner), "RemoveFromSpawns")]
        internal static class RadialObjectSpawner_RemoveFromSpawns
        {
            private static void Prefix(RadialObjectSpawner __instance, GameObject go)
            {
                if (s_ApplyingRemote || go == null || !MyMod.InOnline())
                {
                    return;
                }
                ReportRemoved(__instance, go);
            }
        }

        // ---------- floor items ----------

        static bool IsFloorItem(GearItem gi, string interior)
        {
            if (gi == null)
            {
                return false;
            }
            GameObject go = gi.gameObject;
            return go.activeInHierarchy && !gi.m_BeenInPlayerInventory && !gi.m_InPlayerInventory && !gi.m_InsideContainer
                && gi.GetComponent<NarrativeCollectibleItem>() == null && !Pathes.DuppableGearItem(gi.m_GearName)
                && SpawnerOf(go) == null && (SeamlessCompat.InteriorOf(go) ?? "") == interior;
        }

        static List<GearItem> FloorItems(string interior)
        {
            List<GearItem> list = new List<GearItem>();
            for (int i = 0; i < GearManager.m_Gear.Count; i++)
            {
                GearItem gi = GearManager.m_Gear[i];
                if (IsFloorItem(gi, interior))
                {
                    list.Add(gi);
                }
            }
            return list;
        }

        static string F(float v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static float P(string s)
        {
            return float.Parse(s, CultureInfo.InvariantCulture);
        }

        // Record: "name|x|y|z|qx|qy|qz|qw|condition;" per item.
        static string BuildFloor(string interior)
        {
            StringBuilder sb = new StringBuilder();
            foreach (GearItem gi in FloorItems(interior))
            {
                Vector3 p = gi.transform.position;
                Quaternion r = gi.transform.rotation;
                sb.Append(Compat.CleanGearName(gi.gameObject.name)).Append('|')
                  .Append(F(p.x)).Append('|').Append(F(p.y)).Append('|').Append(F(p.z)).Append('|')
                  .Append(F(r.x)).Append('|').Append(F(r.y)).Append('|').Append(F(r.z)).Append('|').Append(F(r.w)).Append('|')
                  .Append(F(gi.GetNormalizedCondition())).Append(';');
            }
            MelonLogger.Msg("[SceneItems] Floor of " + MyMod.level_guid + FloorKeyOf(interior) + " recorded");
            return sb.ToString();
        }

        static IEnumerable<string> FloorParts(string floor)
        {
            string[] items = floor.Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < items.Length; i += FloorItemsPerPacket)
            {
                yield return string.Join(";", items.Skip(i).Take(FloorItemsPerPacket)) + ";";
            }
        }

        public static void ApplyFloor(string floor, string interior)
        {
            List<GearItem> local = FloorItems(interior);
            bool[] kept = new bool[local.Count];
            int created = 0, hidden = 0;
            foreach (string entry in floor.Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = entry.Split('|');
                if (f.Length < 9)
                {
                    continue;
                }
                string name = f[0];
                Vector3 pos = new Vector3(P(f[1]), P(f[2]), P(f[3]));
                int match = -1;
                float best = MatchDistance;
                for (int i = 0; i < local.Count; i++)
                {
                    if (kept[i] || Compat.CleanGearName(local[i].gameObject.name) != name)
                    {
                        continue;
                    }
                    float d = Vector3.Distance(local[i].transform.position, pos);
                    if (d < best)
                    {
                        best = d;
                        match = i;
                    }
                }
                if (match >= 0)
                {
                    kept[match] = true;
                    continue;
                }
                GameObject prefab = MyMod.GetGearItemObject(name);
                if (prefab == null)
                {
                    continue;
                }
                GameObject obj = Object.Instantiate(prefab, pos, new Quaternion(P(f[4]), P(f[5]), P(f[6]), P(f[7])));
                obj.name = name;
                GameObject root = SeamlessCompat.InteriorRoot(interior);
                if (root != null)
                {
                    obj.transform.SetParent(root.transform, true);
                }
                GearItem gi = obj.GetComponent<GearItem>();
                if (gi != null)
                {
                    gi.SkipSpawnChanceRollInitialDecayAndAutoEvolve();
                    gi.SetNormalizedHP(P(f[8]), false);
                }
                created++;
            }
            for (int i = 0; i < local.Count; i++)
            {
                if (!kept[i])
                {
                    // destroyed, not hidden: the record is applied once, and the game keeps only existing items in the save;
                    // deactivated first, as Destroy takes effect at the end of the frame and the pickup keys are rebuilt below
                    local[i].gameObject.SetActive(false);
                    Object.Destroy(local[i].gameObject);
                    hidden++;
                }
            }
            // New items need their pickup keys, and on the host the stored pickups must hide them again.
            MyMod.BakePreSpawnedGearsList();
            if (MyMod.iAmHost)
            {
                Dictionary<long, DataStr.PickedGearSync> picked = MPSaveManager.LoadPickedGearsData(MyMod.level_guid);
                if (picked != null)
                {
                    foreach (var item in picked)
                    {
                        MyMod.RemovePickedGear(item.Value);
                    }
                }
            }
            MelonLogger.Msg("[SceneItems] Floor of " + MyMod.level_guid + FloorKeyOf(interior) + " applied: kept " + (local.Count - hidden) + ", created " + created + ", hidden " + hidden);
        }

        // ---------- fires ----------
        //
        // A fire's state is the game's own save snapshot (Fire.Serialize): state, burn time, heat, charcoal and, for
        // a campfire, the ring. The snapshot carries the game time it was taken at, so a receiver's game burns it on
        // from there. Fires are identified by their guid; a campfire copy gets the guid of the original, which the
        // game keeps in that player's save. Every player checks its fires; a fire whose signature (state, game time
        // it burns out at, charcoal) changed by something other than burning on (fuel, torch, lighting, wind,
        // charcoal taken, placed) is sent. Fires burning on change the same way everywhere and are not sent.

        struct FireSig
        {
            public FireState State;
            public float BurnOut; // hours played at which the fire burns out
            public int Coal;
            public bool Collected;
        }

        const float BurnOutTolerance = 0.05f; // game hours
        // Host: campfires dismantled this session; a late report of one must not bring it back.
        static readonly HashSet<string> s_DismantledFires = new HashSet<string>();
        static readonly Dictionary<string, FireSig> s_KnownFire = new Dictionary<string, FireSig>();

        static bool FireSyncOn()
        {
            return MyMod.ServerConfig.m_FireSync != 0;
        }

        // Fire snapshots are stamped with the hours played of the save that took them; every save counts from its
        // own start, so the stamp is moved to the receiver's clock using the sender's clock sent along.
        static float HoursNow()
        {
            return GameManager.GetTimeOfDayComponent().GetHoursPlayedNotPaused();
        }

        static Vector3 Clock()
        {
            return new Vector3(HoursNow(), 0, 0);
        }

        static readonly Regex s_HoursRx = new Regex("\"m_HoursPlayed\":(-?[0-9.Ee+-]+)");

        static string Rebase(string state, float senderHours)
        {
            Match m = s_HoursRx.Match(state);
            if (!m.Success)
            {
                return state;
            }
            float hours = P(m.Groups[1].Value) + HoursNow() - senderHours;
            return state.Substring(0, m.Index) + "\"m_HoursPlayed\":" + hours.ToString("R", CultureInfo.InvariantCulture) + state.Substring(m.Index + m.Length);
        }

        static float Num(string state, string key, float def)
        {
            Match m = Regex.Match(state, "\"" + key + "\":(-?[0-9.Ee+-]+)");
            return m.Success ? P(m.Groups[1].Value) : def;
        }

        static bool IsCampfireState(string state)
        {
            return state.Contains("\"m_CampfireSerialized\":\"{");
        }

        static Vector3 PosOfState(string state)
        {
            Match m = Regex.Match(state, "\"m_Position\":\\[([^,\\]]+),([^,\\]]+),([^\\]]+)\\]");
            return m.Success ? new Vector3(P(m.Groups[1].Value), P(m.Groups[2].Value), P(m.Groups[3].Value)) : Vector3.zero;
        }

        static FireSig SigOf(Fire f)
        {
            return new FireSig
            {
                State = f.m_FireState,
                BurnOut = HoursNow() + f.GetRemainingLifeTimeSeconds() / 3600f,
                Coal = f.m_NumGeneratedCharcoalPieces,
                Collected = f.m_HasCollectedCharcoal,
            };
        }

        // Signature of a snapshot already moved to this player's clock; an Off snapshot has no state field.
        static FireSig SigOf(string state)
        {
            Match st = Regex.Match(state, "\"m_FireStateProxy\":\"(\\w+)\"");
            FireState fs = FireState.Off;
            if (st.Success)
            {
                System.Enum.TryParse(st.Groups[1].Value, out fs);
            }
            return new FireSig
            {
                State = fs,
                BurnOut = Num(state, "m_HoursPlayed", 0) + (Num(state, "m_MaxOnTODSecondsProxy", 0) - Num(state, "m_ElapsedOnTODSecondsProxy", 0)) / 3600f,
                Coal = (int)Num(state, "m_NumGeneratedCharcoalPieces", 0),
                Collected = state.Contains("\"m_HasCollectedCharcoal\":true"),
            };
        }

        static bool Same(FireSig a, FireSig b)
        {
            return a.State == b.State && a.Coal == b.Coal && a.Collected == b.Collected
                && (a.State == FireState.Off || Mathf.Abs(a.BurnOut - b.BurnOut) <= BurnOutTolerance);
        }

        // A campfire placed by this player gets a guid here if the game gave it none.
        static string FireGuid(Fire f, bool create)
        {
            string guid = ObjectGuid.MaybeGetGuidFromGameObject(f.gameObject);
            if (string.IsNullOrEmpty(guid) && create && f.m_Campfire != null)
            {
                guid = System.Guid.NewGuid().ToString();
                ObjectGuid.MaybeAttachObjectGuidAddOrReplace(f.gameObject, guid);
            }
            return guid;
        }

        static Fire FindFire(string guid, string state)
        {
            if (FireManager.m_Fires == null)
            {
                return null;
            }
            foreach (Fire f in FireManager.m_Fires)
            {
                if (f != null && FireGuid(f, false) == guid)
                {
                    return f;
                }
            }
            if (!IsCampfireState(state))
            {
                return null;
            }
            // A copy under another guid (made before campfires shared their guid, or by both players at once):
            // the campfire standing at the same spot takes this guid.
            Vector3 pos = PosOfState(state);
            foreach (Fire f in FireManager.m_Fires)
            {
                if (f == null || f.m_Campfire == null || Vector3.Distance(f.transform.position, pos) > MatchDistance)
                {
                    continue;
                }
                string other = FireGuid(f, false);
                if (!string.IsNullOrEmpty(other))
                {
                    s_KnownFire.Remove(other);
                }
                ObjectGuid.MaybeAttachObjectGuidAddOrReplace(f.gameObject, guid);
                return f;
            }
            return null;
        }

        static void PollFires()
        {
            if (!FireSyncOn() || FireManager.m_Fires == null)
            {
                return;
            }
            foreach (Fire f in FireManager.m_Fires.ToArray())
            {
                if (f == null || f.m_IsPerpetual)
                {
                    continue;
                }
                string guid = FireGuid(f, true);
                if (string.IsNullOrEmpty(guid))
                {
                    continue;
                }
                FireSig sig = SigOf(f);
                FireSig known;
                bool isKnown = s_KnownFire.TryGetValue(guid, out known);
                s_KnownFire[guid] = sig;
                if (isKnown ? Same(known, sig) : (f.m_Campfire == null && f.m_FireState == FireState.Off))
                {
                    continue; // unchanged, or an unlit stove nobody used
                }
                Send(KindFire, guid, f.Serialize(), Clock());
            }
        }

        // A snapshot already on this player's clock.
        static void OnFireState(string guid, string state)
        {
            if (!FireSyncOn())
            {
                return;
            }
            Fire f = FindFire(guid, state);
            if (f != null && Same(SigOf(f), SigOf(state)))
            {
                s_KnownFire[guid] = SigOf(f);
                return;
            }
            bool created = false;
            if (f == null)
            {
                if (!IsCampfireState(state))
                {
                    return;
                }
                f = GameManager.GetFireManagerComponent().InstantiateCampFire();
                if (f == null)
                {
                    return;
                }
                ObjectGuid.MaybeAttachObjectGuidAddOrReplace(f.gameObject, guid);
                created = true;
            }
            f.Deserialize(state); // also places the fire
            Campfire camp = f.m_Campfire;
            if (camp != null)
            {
                if (created)
                {
                    // The ring stones fit the ground only while a campfire is being placed: run one placement update.
                    Vector3 pos = f.transform.position;
                    Quaternion rot = f.transform.rotation;
                    camp.m_LastPosition = Vector3.zero;
                    camp.m_IsBeingPlaced = true;
                    camp.Update();
                    camp.m_IsBeingPlaced = false;
                    f.transform.position = pos;
                    f.transform.rotation = rot;
                }
                // A fire put out keeps "Lit" in its campfire part.
                if (f.m_FireState == FireState.Off && camp.m_State == CampfireState.Lit)
                {
                    camp.SetState(CampfireState.BurntOut);
                }
            }
            s_KnownFire[guid] = SigOf(f);
        }

        // Client: the scene's last fire states, already on this player's clock. A stove in a closed SeamlessInteriors
        // room does not exist, so its state is applied again when the room opens.
        static string s_FireScene;
        static string s_FireAppliedIn; // building this player is in whose fires were applied on entry
        static readonly Dictionary<string, string> s_FireIn = new Dictionary<string, string>();

        static void RememberFire(string scene, string guid, string state)
        {
            if (MyMod.iAmHost)
            {
                return; // the host's scene store has them
            }
            if (s_FireScene != scene)
            {
                s_FireScene = scene;
                s_FireIn.Clear();
            }
            if (state == null)
            {
                s_FireIn.Remove(guid);
            }
            else
            {
                s_FireIn[guid] = state;
            }
        }

        static void ApplyFiresAgain()
        {
            if (!FireSyncOn())
            {
                return;
            }
            string scene = MyMod.level_guid;
            if (MyMod.iAmHost)
            {
                foreach (var item in Load(scene).ToList())
                {
                    if (item.Key.StartsWith(FirePrefix))
                    {
                        OnFireState(item.Key.Substring(FirePrefix.Length), item.Value);
                    }
                }
            }
            else if (s_FireScene == scene)
            {
                foreach (var item in s_FireIn.ToList())
                {
                    OnFireState(item.Key, item.Value);
                }
            }
        }

        static void RemoveFire(string guid)
        {
            s_KnownFire.Remove(guid);
            if (FireManager.m_Fires == null)
            {
                return;
            }
            foreach (Fire f in FireManager.m_Fires.ToArray())
            {
                if (f != null && FireGuid(f, false) == guid)
                {
                    MelonLogger.Msg("[SceneItems] Campfire " + guid + " dismantled by another player");
                    Object.Destroy(f.gameObject);
                    return;
                }
            }
        }

        // Called before this player dismantles a campfire.
        public static void OnFireDismantled(Fire f)
        {
            if (f == null || !MyMod.InOnline() || !FireSyncOn())
            {
                return;
            }
            string guid = FireGuid(f, false);
            if (string.IsNullOrEmpty(guid))
            {
                return;
            }
            s_KnownFire.Remove(guid);
            Send(KindFireRemove, guid, "", Vector3.zero);
        }

        // ---------- network ----------

        static void Write(Packet p, int kind, string scene, string key, string state, Vector3 pos)
        {
            p.Write(kind);
            p.Write(scene);
            p.Write(key);
            p.Write(state);
            p.Write(pos);
        }

        static void SendToScene(int except, int kind, string scene, string key, string state, Vector3 pos = default)
        {
            using (Packet p = new Packet((int)ServerPackets.SCENEITEMS))
            {
                Write(p, kind, scene, key, state, pos);
                ServerSend.SendUDPDataToAllButNotSender(p, except, scene);
            }
        }

        static void Send(int kind, string key, string state, Vector3 pos)
        {
            string scene = MyMod.level_guid;
            if (MyMod.iAmHost)
            {
                if (kind == KindState)
                {
                    Store(scene, key, state);
                }
                else if (kind == KindFire)
                {
                    // the host's own campfire wins over a copy stored under another guid at the same spot
                    string dup;
                    while ((dup = StoredCampfireNear(scene, key, state)) != null)
                    {
                        Load(scene).Remove(FirePrefix + dup);
                    }
                    Store(scene, FirePrefix + key, state);
                }
                else if (kind == KindFireRemove)
                {
                    Load(scene).Remove(FirePrefix + key);
                    s_DismantledFires.Add(key);
                }
                SendToScene(0, kind, scene, key, state, pos);
            }
            else if (MyMod.sendMyPosition)
            {
                using (Packet p = new Packet((int)ClientPackets.SCENEITEMS))
                {
                    Write(p, kind, scene, key, state, pos);
                    MyMod.SendUDPData(p);
                }
            }
        }

        static void ServerSendTo(int client, int kind, string scene, string key, string state, Vector3 pos)
        {
            using (Packet p = new Packet((int)ServerPackets.SCENEITEMS))
            {
                Write(p, kind, scene, key, state, pos);
                ServerSend.SendUDPData(client, p);
            }
        }

        // Applies a message on this side; relayed floor parts are collected until the record is complete.
        static void Receive(int kind, string scene, string key, string state, Vector3 pos)
        {
            if (scene != MyMod.level_guid)
            {
                return;
            }
            switch (kind)
            {
                case KindState:
                    Apply(key, state);
                    break;
                case KindPick:
                    RemoveNear(key, pos);
                    break;
                case KindFire:
                    state = Rebase(state, pos.x);
                    RememberFire(scene, key, state);
                    OnFireState(key, state);
                    break;
                case KindFireRemove:
                    RememberFire(scene, key, null);
                    RemoveFire(key);
                    break;
                case KindFloorPart:
                    // key: building id of the record, "" for the region's
                    Buffer(s_FloorIn, key).Append(state);
                    break;
                case KindFloorEnd:
                    {
                        StringBuilder buf = Buffer(s_FloorIn, key);
                        string floor = buf.ToString();
                        buf.Clear();
                        OnFloorReceived(scene, key, floor);
                    }
                    break;
                case KindSceneSent:
                    s_Ready = true;
                    CheckArea("");
                    break;
            }
        }

        // Host side.
        public static void HandleFromClient(int from, Packet p)
        {
            int kind = p.ReadInt();
            string scene = p.ReadString();
            string key = p.ReadString();
            string state = p.ReadString();
            Vector3 pos = p.ReadVector3();
            if (kind == KindFloorPart || kind == KindFloorEnd)
            {
                // key: building id of the record, "" for the region's
                StringBuilder buf = Buffer(s_FloorFromClient, from + "|" + key);
                if (kind == KindFloorPart)
                {
                    buf.Append(state);
                    return;
                }
                string floor = buf.ToString();
                buf.Clear();
                string stored = StoredFloor(scene, key);
                if (stored != null)
                {
                    // the first record of an area stays; the sender gets it in place of its own
                    foreach (string part in FloorParts(stored))
                    {
                        ServerSendTo(from, KindFloorPart, scene, key, part, Vector3.zero);
                    }
                    ServerSendTo(from, KindFloorEnd, scene, key, "", Vector3.zero);
                    return;
                }
                Store(scene, FloorKeyOf(key), floor);
                // everybody in the scene, the sender included: it waits for its record to come back
                foreach (string part in FloorParts(floor))
                {
                    SendToScene(0, KindFloorPart, scene, key, part);
                }
                SendToScene(0, KindFloorEnd, scene, key, "");
                return;
            }
            if (kind == KindState)
            {
                Store(scene, key, state);
            }
            else if (kind == KindFire)
            {
                if (s_DismantledFires.Contains(key))
                {
                    return; // sent by a player who had not yet heard the campfire was dismantled
                }
                // stored and relayed on the host's clock
                state = Rebase(state, pos.x);
                pos = Clock();
                string dup = StoredCampfireNear(scene, key, state);
                if (dup != null)
                {
                    // the sender has an old copy of a known campfire: send it the known one, which it adopts
                    ServerSendTo(from, KindFire, scene, dup, Load(scene)[FirePrefix + dup], Clock());
                    return;
                }
                Store(scene, FirePrefix + key, state);
            }
            else if (kind == KindFireRemove)
            {
                Load(scene).Remove(FirePrefix + key);
                s_DismantledFires.Add(key);
            }
            Receive(kind, scene, key, state, pos);
            SendToScene(from, kind, scene, key, state, pos);
        }

        // Host: a stored campfire under another guid standing where this one stands.
        static string StoredCampfireNear(string scene, string guid, string state)
        {
            if (!IsCampfireState(state))
            {
                return null;
            }
            Vector3 pos = PosOfState(state);
            foreach (var item in Load(scene))
            {
                if (item.Key.StartsWith(FirePrefix) && item.Key != FirePrefix + guid && IsCampfireState(item.Value)
                    && Vector3.Distance(PosOfState(item.Value), pos) < MatchDistance)
                {
                    return item.Key.Substring(FirePrefix.Length);
                }
            }
            return null;
        }

        // Client side.
        public static void HandleFromServer(Packet p)
        {
            int kind = p.ReadInt();
            string scene = p.ReadString();
            string key = p.ReadString();
            string state = p.ReadString();
            Vector3 pos = p.ReadVector3();
            Receive(kind, scene, key, state, pos);
        }
    }
}
