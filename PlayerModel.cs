using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace SkyCoop
{
    // Humanoid character for other players. The body is a rigged model whose clothes are child meshes named after
    // their gear (GEAR_*), dressed with the game's own material for that gear. Held items and head/neck accessories
    // are mesh-only copies of the game's gear prefabs placed on the skeleton, so every gear the game has can be shown
    // without a model of its own. The model sits on the classic player object, which keeps its colliders, voice,
    // lights and logic; only that object's meshes are hidden.
    public class PlayerModel : MonoBehaviour
    {
        public PlayerModel(IntPtr ptr) : base(ptr) { }

        public const string BundleFile = "skycoopplayer.unity3d";

        static GameObject s_Prefab = null;
        static bool s_Tried = false;

        static GameObject Prefab
        {
            get
            {
                if (s_Tried)
                {
                    return s_Prefab;
                }
                s_Tried = true;
                string path = Path.Combine(MelonLoader.Utils.MelonEnvironment.ModsDirectory, BundleFile);
                if (!File.Exists(path))
                {
                    MelonLogger.Msg("[PlayerModel] " + BundleFile + " not found, other players keep the classic model.");
                    return null;
                }
                try
                {
                    AssetBundle bundle = AssetBundle.LoadFromFile(path);
                    if (bundle != null)
                    {
                        s_Prefab = bundle.LoadAsset<GameObject>("SkyCoopPlayer");
                        if (s_Prefab == null)
                        {
                            foreach (string name in bundle.GetAllAssetNames())
                            {
                                if (name.EndsWith("skycoopplayer.prefab", StringComparison.OrdinalIgnoreCase))
                                {
                                    s_Prefab = bundle.LoadAsset<GameObject>(name);
                                }
                            }
                        }
                    }
                    if (s_Prefab != null)
                    {
                        // Scene loads unload unused assets; the prefab has to survive them for players created later.
                        s_Prefab.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                        MelonLogger.Msg("[PlayerModel] Humanoid player model loaded.");
                    }
                    else
                    {
                        MelonLogger.Msg(ConsoleColor.Red, "[PlayerModel] " + BundleFile + " has no SkyCoopPlayer prefab.");
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Msg(ConsoleColor.Red, "[PlayerModel] Could not load " + BundleFile + ": " + e.Message);
                }
                return s_Prefab;
            }
        }

        public static void Attach(GameObject player, Comps.MultiplayerPlayer mp)
        {
            if (Prefab == null || player.GetComponent<PlayerModel>() != null)
            {
                return;
            }
            player.AddComponent<PlayerModel>().m_MP = mp;
        }

        // Shots, throws and melee hits of another player play their one-shot animation on the model.
        public static void OnShoot(int from, string projectile)
        {
            if (from < 0 || from >= MyMod.players.Count || MyMod.players[from] == null)
            {
                return;
            }
            PlayerModel pm = MyMod.players[from].GetComponent<PlayerModel>();
            if (pm == null || pm.m_Anim == null)
            {
                return;
            }
            if (projectile == "Melee")
            {
                pm.m_Anim.SetTrigger("Hit");
            }
            else if (projectile == "GEAR_Stone" || projectile == "GEAR_NoiseMaker")
            {
                pm.m_Anim.SetTrigger("Throw");
            }
            else
            {
                pm.m_Anim.SetTrigger("Shoot");
            }
        }

        public Comps.MultiplayerPlayer m_MP = null;
        GameObject m_Model = null;
        Animator m_Anim = null;
        int m_Layer = 0;

        Transform m_Spine = null;
        float m_Tilt = 0f;

        // Lower lip, moved by the loudness of the player's voice.
        Transform m_Lip = null;
        readonly List<AudioSource> m_Voices = new List<AudioSource>();
        Il2CppStructArray<float> m_VoiceSamples = null;
        const float MouthOpenY = 0.03f, MouthClosedY = 0.053f, MouthGain = 50f;

        Vector3 m_LastPos = Vector3.zero;
        bool m_HasLastPos = false;
        float m_NextHideScan = 0f;

        string m_ShownHeld = null;
        GameObject m_HeldGo = null;
        bool m_Hang = false;

        string m_ClothingKey = null;
        readonly List<Renderer> m_ClothingParts = new List<Renderer>();
        readonly List<GameObject> m_Accessories = new List<GameObject>();
        GameObject m_Hair = null;
        GameObject m_Beard = null;
        GameObject m_Brows = null;

        void Start()
        {
            m_Model = UnityEngine.Object.Instantiate(Prefab, transform, false);
            m_Model.name = "PlayerModel";
            // The classic rig is not at unit scale; keep the humanoid at its real size.
            Vector3 s = transform.lossyScale;
            m_Model.transform.localScale = new Vector3(1f / s.x, 1f / s.y, 1f / s.z);
            // Hits are taken by the classic object's damage zones; the model must not block or catch raycasts.
            foreach (Collider c in m_Model.GetComponentsInChildren<Collider>(true))
            {
                UnityEngine.Object.Destroy(c);
            }
            m_Anim = m_Model.GetComponentInChildren<Animator>();
            if (m_Anim != null)
            {
                m_Anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            m_Layer = m_MP != null && m_MP.hand_r != null ? m_MP.hand_r.layer : gameObject.layer;
            foreach (Transform t in m_Model.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = m_Layer;
                if (t.name.StartsWith("GEAR_", StringComparison.Ordinal) && t.GetComponent<Renderer>() != null)
                {
                    m_ClothingParts.Add(t.GetComponent<Renderer>());
                }
                else if (t.name.StartsWith("Hair_mesh", StringComparison.Ordinal))
                {
                    m_Hair = t.gameObject;
                }
                else if (t.name.StartsWith("Beard_mesh", StringComparison.Ordinal))
                {
                    m_Beard = t.gameObject;
                }
                else if (t.name.StartsWith("Eyebrows_mesh", StringComparison.Ordinal))
                {
                    m_Brows = t.gameObject;
                }
            }
            HideClassicMeshes();

            m_Spine = Bone(HumanBodyBones.Spine);
            Transform head = Bone(HumanBodyBones.Head);
            m_Lip = head != null ? head.Find("Lip_Bottom") : null;
            FindVoices();
        }

        void FindVoices()
        {
            m_Voices.Clear();
            foreach (Comps.MultiplayerPlayerVoiceChatPlayer v in GetComponentsInChildren<Comps.MultiplayerPlayerVoiceChatPlayer>(true))
            {
                if (v.aSource != null)
                {
                    m_Voices.Add(v.aSource);
                }
            }
        }

        // The classic code keeps toggling its own meshes (held items, back items, clothing variants), so they are
        // switched off again periodically instead of once.
        void HideClassicMeshes()
        {
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (r.transform.IsChildOf(m_Model.transform))
                {
                    continue;
                }
                if (r.TryCast<SkinnedMeshRenderer>() != null || r.TryCast<MeshRenderer>() != null)
                {
                    r.enabled = false;
                }
            }
        }

        void Update()
        {
            if (m_Model == null || m_MP == null || m_MP.m_ID >= MyMod.playersData.Count || MyMod.playersData[m_MP.m_ID] == null)
            {
                return;
            }
            if (Time.time >= m_NextHideScan)
            {
                m_NextHideScan = Time.time + 0.5f;
                HideClassicMeshes();
                if (m_Voices.Count == 0)
                {
                    FindVoices();
                }
            }
            DataStr.MultiPlayerClientData data = MyMod.playersData[m_MP.m_ID];
            string held = m_MP.m_HoldingItemRaw ?? "";
            if (held != m_ShownHeld)
            {
                ShowHeld(held);
            }
            ApplyClothing(data.m_PlayerClothingData);
        }

        void LateUpdate()
        {
            if (m_Model == null || m_MP == null || m_MP.m_ID >= MyMod.playersData.Count || MyMod.playersData[m_MP.m_ID] == null)
            {
                return;
            }
            DataStr.MultiPlayerClientData data = MyMod.playersData[m_MP.m_ID];
            // The classic object is squashed and lowered for the female character; the model keeps its own size
            // and stands on the ground.
            Vector3 s = transform.lossyScale;
            m_Model.transform.localScale = new Vector3(1f / s.x, 1f / s.y, 1f / s.z);
            m_Model.transform.localPosition = new Vector3(0, data.m_Female ? 0.194f / s.y : 0f, 0);
            int action = DriveAnimator(data);
            ApplyTilt(data, action);
            AnimateMouth();
            // Lanterns hang upright from the hand by their handle.
            if (m_Hang && m_HeldGo != null && m_HeldGo.transform.parent != null)
            {
                Transform t = m_HeldGo.transform;
                t.rotation = Quaternion.Euler(0, transform.eulerAngles.y, 0);
                t.position = t.parent.position + Vector3.down * 0.02f;
            }
        }

        // --- animation ---

        // Values of the model controller's "Action" and "Gear" parameters.
        const int ActionNone = 0, ActionHarvesting = 1, ActionPistolAim = 2, ActionRifleAim = 3, ActionIgniting = 4, ActionKnocked = 6, ActionSleep = 7;
        const int PoseNone = 0, PosePistol = 1, PoseRifle = 2, PoseLantern = 3, PoseGenericHold = 4, PoseBow = 6;
        const float MinimalSpeed = 0.1f;

        static int HandPose(string gear)
        {
            if (gear.Length == 0) return PoseNone;
            if (gear.Contains("FlareGun") || gear.Contains("Revolver")) return PosePistol;
            if (gear.Contains("Rifle")) return PoseRifle;
            if (gear.Contains("Lamp") || gear.Contains("Lantern")) return PoseLantern;
            if (gear.Contains("Bow") && !gear.Contains("Arrow")) return PoseBow;
            return PoseGenericHold;
        }

        static int ActionOf(string state, bool aiming, int pose)
        {
            switch (state)
            {
                case "Sleep": return ActionSleep;
                case "Knock":
                case "Fight": return ActionKnocked;
                case "Igniting": return ActionIgniting;
                case "Harvesting":
                case "HarvestingStanding": return ActionHarvesting;
            }
            if (aiming || state == "HoldGun")
            {
                if (pose == PoseRifle) return ActionRifleAim;
                if (pose == PosePistol) return ActionPistolAim;
            }
            return ActionNone;
        }

        // The controller starts moving; feed it the speed and direction implied by how the player object moved.
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        void DriveAnimator(DataStr.MultiPlayerClientData data)
        {
            if (m_Anim == null)
            {
                return;
            }
            Vector3 pos = transform.position;
            float dt = Time.deltaTime;
            Vector3 velocity = m_HasLastPos && dt > 0 ? (pos - m_LastPos) / dt : Vector3.zero;
            m_LastPos = pos;
            m_HasLastPos = true;
            velocity.y = 0;
            Vector3 local = transform.InverseTransformDirection(velocity);
            string state = data.m_AnimState ?? "";
            bool crouch = state == "Ctrl";
            int pose = HandPose(m_ShownHeld ?? "");
            m_Anim.SetFloat("Speed", crouch ? velocity.magnitude : velocity.magnitude / 4f);
            m_Anim.SetBool("IsMoving", local.magnitude > MinimalSpeed);
            m_Anim.SetBool("Crouch", crouch);
            m_Anim.SetInteger("Gear", pose);
            m_Anim.SetInteger("Action", ActionOf(state, data.m_Aiming, pose));
            m_Anim.SetFloat("DirectionX", Mathf.Lerp(m_Anim.GetFloat("DirectionX"), Mathf.Clamp(local.x, -1f, 1f), 0.1f));
            m_Anim.SetFloat("DirectionY", Mathf.Lerp(m_Anim.GetFloat("DirectionY"), Mathf.Clamp(local.z, -1f, 1f), 0.1f));
        }

        // --- held item ---

        // Placement in the right hand bone's space; anything not listed gets the knife grip.
        static readonly Dictionary<string, Vector3[]> Grips = new Dictionary<string, Vector3[]>
        {
            { "GEAR_Rifle", new[] { new Vector3(-0.23f, 0.32f, -0.047f), new Vector3(75, 90, 0) } },
            { "GEAR_Revolver", new[] { new Vector3(0, 0.15f, -0.06f), new Vector3(90, 0, 0) } },
            { "GEAR_FlareGun", new[] { new Vector3(0.05f, 0.14f, -0.07f), new Vector3(90, 0, 0) } },
            { "GEAR_Stone", new[] { new Vector3(0, 0.095f, -0.053f), Vector3.zero } },
            { "GEAR_NoiseMaker", new[] { new Vector3(0.03f, 0.08f, -0.05f), new Vector3(-30, 0, 0) } },
            { "GEAR_EmergencyStim", new[] { new Vector3(0.01f, 0.07f, -0.047f), new Vector3(0, 6, 0) } },
            { "GEAR_Hatchet", new[] { new Vector3(0.1f, 0.135f, -0.05f), new Vector3(90, 180, 180) } },
            { "GEAR_HatchetImprovised", new[] { new Vector3(0.05f, 0.09f, -0.05f), new Vector3(90, 180, 180) } },
            { "GEAR_KnifeScrapMetal", new[] { new Vector3(0.08f, 0.11f, -0.051f), new Vector3(0, 270, 300) } },
            { "GEAR_Hammer", new[] { new Vector3(0.09f, 0.11f, -0.1f), new Vector3(80, 0, 0) } },
            { "GEAR_Prybar", new[] { new Vector3(0.09f, 0.1f, -0.02f), new Vector3(350, 0, 0) } },
            { "GEAR_Camera", new[] { new Vector3(0, 0.09f, -0.03f), new Vector3(0, 0, 270) } },
        };
        static readonly Vector3[] DefaultGrip = { new Vector3(0.09f, 0.11f, -0.061f), new Vector3(75, 0, 0) };

        static string GripKey(string gear)
        {
            if (Grips.ContainsKey(gear)) return gear;
            // Rifle and revolver variants are held like the base weapon.
            if (gear.StartsWith("GEAR_Rifle", StringComparison.Ordinal)) return "GEAR_Rifle";
            if (gear.StartsWith("GEAR_Revolver", StringComparison.Ordinal)) return "GEAR_Revolver";
            return gear;
        }

        void ShowHeld(string gear)
        {
            m_ShownHeld = gear;
            if (m_HeldGo != null)
            {
                UnityEngine.Object.Destroy(m_HeldGo);
            }
            m_HeldGo = null;
            m_Hang = false;
            Transform hand = Bone(HumanBodyBones.RightHand);
            if (gear.Length == 0 || hand == null || !gear.StartsWith("GEAR_", StringComparison.Ordinal))
            {
                return;
            }
            GameObject prefab = LoadGearPrefab(gear);
            if (prefab == null)
            {
                return;
            }
            m_HeldGo = MeshCopy(prefab, "Held_" + gear, null);
            m_HeldGo.transform.SetParent(hand, false);
            Vector3[] grip = Grips.TryGetValue(GripKey(gear), out Vector3[] g) ? g : DefaultGrip;
            m_HeldGo.transform.localPosition = grip[0];
            m_HeldGo.transform.localRotation = EulerXYZ(grip[1]);
            m_Hang = HandPose(gear) == PoseLantern;
            if (m_Hang)
            {
                MoveTopToOrigin(m_HeldGo.transform);
            }
        }

        // --- clothing ---

        static readonly int BlendAmount = Shader.PropertyToID("_blend_amt");
        static readonly Dictionary<string, Material> s_GearMaterials = new Dictionary<string, Material>();

        // Gear worn as a separate object on the head bone (in its space), with the child of the prefab to show.
        sealed class Accessory
        {
            public Vector3 Pos, Rot, Scale;
            public int Child = -1;
        }
        static readonly Dictionary<string, Accessory> Accessories = new Dictionary<string, Accessory>
        {
            { "GEAR_WoolWrap", new Accessory { Pos = new Vector3(-0.02f, -0.11f, 0.06f), Rot = new Vector3(35, 0, 0), Scale = new Vector3(1.21f, 1, 1.3f) } },
            { "GEAR_CottonScarf", new Accessory { Pos = new Vector3(-0.01f, -0.1f, 0.02f), Rot = new Vector3(30, 0, 0), Scale = new Vector3(0.93f, 1, 1.03f) } },
            { "GEAR_BasicWoolScarf", new Accessory { Pos = new Vector3(0.03f, -0.1f, 0), Rot = new Vector3(30, 0, 0), Scale = new Vector3(0.7f, 1, 1) } },
            { "GEAR_EarMuffs", new Accessory { Pos = new Vector3(0, 0.08f, -0.02f), Rot = new Vector3(330, 0, 0), Scale = new Vector3(1.3f, 1, 1.1f) } },
            { "GEAR_HatLeatherAviatorA", new Accessory { Pos = new Vector3(0, 0.08f, 0), Rot = Vector3.zero, Scale = new Vector3(1.14f, 1, 1), Child = 0 } },
            { "GEAR_MinersHelmet", new Accessory { Pos = new Vector3(0, 0.118f, 0), Rot = Vector3.zero, Scale = new Vector3(1.2f, 1, 1) } },
            { "GEAR_CougarWrap", new Accessory { Pos = new Vector3(0, -0.07f, -0.03f), Rot = Vector3.zero, Scale = Vector3.one, Child = 1 } },
            { "GEAR_WolfSkinHat", new Accessory { Pos = new Vector3(0, 0.06f, 0), Rot = Vector3.zero, Scale = Vector3.one, Child = 1 } },
            { "GEAR_DeerSkinHat", new Accessory { Pos = new Vector3(0, 0.1f, 0), Rot = Vector3.zero, Scale = new Vector3(0.4f, 0.4f, 0.4f), Child = 1 } },
        };

        static readonly string[] FaceCovers = { "GEAR_Balaclava", "GEAR_HatGatorBalaclavaA", "GEAR_WoolWrapCap", "GEAR_WoolWrap", "GEAR_CottonScarf", "GEAR_BasicWoolScarf" };
        static readonly string[] OpenHeadwear = { "GEAR_WoolWrap", "GEAR_CottonScarf", "GEAR_BasicWoolScarf" };

        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        void ApplyClothing(DataStr.PlayerClothingData c)
        {
            if (c == null)
            {
                return;
            }
            string[] worn = { c.m_Hat ?? "", c.m_Top ?? "", c.m_Bottom ?? "", c.m_Boots ?? "", c.m_Scarf ?? "", c.m_Balaclava ?? "", c.m_Gloves ?? "" };
            string key = string.Join("|", worn);
            if (key == m_ClothingKey)
            {
                return;
            }
            m_ClothingKey = key;
            HashSet<string> set = new HashSet<string>(worn);
            set.Remove("");

            foreach (Renderer rend in m_ClothingParts)
            {
                string name = rend.gameObject.name;
                bool on = set.Contains(name);
                rend.gameObject.SetActive(on);
                if (!on)
                {
                    continue;
                }
                Material mat = GearMaterial(name);
                if (mat != null)
                {
                    Il2CppReferenceArray<Material> mats = new Il2CppReferenceArray<Material>(Math.Max(1, rend.sharedMaterials.Length));
                    for (int i = 0; i < mats.Length; i++)
                    {
                        mats[i] = mat;
                    }
                    rend.sharedMaterials = mats;
                }
                MaterialPropertyBlock block = new MaterialPropertyBlock();
                rend.GetPropertyBlock(block);
                block.SetFloat(BlendAmount, 0f);
                rend.SetPropertyBlock(block);
            }

            foreach (GameObject a in m_Accessories)
            {
                UnityEngine.Object.Destroy(a);
            }
            m_Accessories.Clear();
            Transform head = Bone(HumanBodyBones.LeftEye) ?? Bone(HumanBodyBones.Head);
            if (head != null)
            {
                foreach (string gear in set)
                {
                    if (!Accessories.TryGetValue(gear, out Accessory acc) || HasPart(gear))
                    {
                        continue;
                    }
                    GameObject prefab = LoadGearPrefab(gear);
                    if (prefab == null)
                    {
                        continue;
                    }
                    Transform source = acc.Child >= 0 && prefab.transform.childCount > acc.Child ? prefab.transform.GetChild(acc.Child) : null;
                    GameObject copy = MeshCopy(prefab, "Worn_" + gear, source);
                    copy.transform.SetParent(head, false);
                    copy.transform.localPosition = acc.Pos;
                    copy.transform.localRotation = EulerXYZ(acc.Rot);
                    copy.transform.localScale = acc.Scale;
                    m_Accessories.Add(copy);
                }
            }

            string hat = c.m_Hat ?? "";
            string balaclava = c.m_Balaclava ?? "";
            if (m_Hair != null)
            {
                m_Hair.SetActive((hat.Length == 0 || Array.IndexOf(OpenHeadwear, hat) >= 0) && balaclava.Length == 0);
            }
            if (m_Beard != null)
            {
                bool covered = false;
                foreach (string f in FaceCovers)
                {
                    covered |= set.Contains(f);
                }
                m_Beard.SetActive(!covered);
            }
            if (m_Brows != null)
            {
                m_Brows.SetActive(!set.Contains("GEAR_Balaclava") && !set.Contains("GEAR_HatGatorBalaclavaA"));
            }
        }

        bool HasPart(string gear)
        {
            foreach (Renderer r in m_ClothingParts)
            {
                if (r.gameObject.name == gear) return true;
            }
            return false;
        }

        static Material GearMaterial(string gear)
        {
            if (s_GearMaterials.TryGetValue(gear, out Material m))
            {
                return m;
            }
            m = null;
            GameObject prefab = LoadGearPrefab(gear);
            if (prefab != null)
            {
                Renderer rend;
                // Two skin garments keep a leftover renderer on the root; the real one is the first child.
                if ((gear == "GEAR_DeerSkinPants" || gear == "GEAR_WolfSkinPant") && prefab.transform.childCount > 0)
                {
                    rend = prefab.transform.GetChild(0).GetComponent<Renderer>();
                }
                else
                {
                    rend = prefab.GetComponent<Renderer>();
                    if (rend == null)
                    {
                        rend = prefab.GetComponentInChildren<Renderer>();
                    }
                }
                m = rend != null ? rend.sharedMaterial : null;
            }
            s_GearMaterials[gear] = m;
            return m;
        }

        // --- helpers ---

        Transform Bone(HumanBodyBones bone)
        {
            return m_Anim != null && m_Anim.isHuman ? m_Anim.GetBoneTransform(bone) : null;
        }

        static GameObject LoadGearPrefab(string gear)
        {
            try
            {
                return Addressables.LoadAssetAsync<GameObject>((Il2CppSystem.Object)gear).WaitForCompletion();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Copy of the prefab's visible meshes only, so none of the game's gear scripts run on it.
        GameObject MeshCopy(GameObject prefab, string name, Transform onlyUnder)
        {
            GameObject root = new GameObject(name);
            root.layer = m_Layer;
            Transform pt = prefab.transform;
            foreach (MeshFilter mf in (onlyUnder != null ? onlyUnder : pt).GetComponentsInChildren<MeshFilter>(true))
            {
                MeshRenderer src = mf.GetComponent<MeshRenderer>();
                if (src == null || !src.enabled || mf.sharedMesh == null || (mf.name.Contains("LOD") && !mf.name.Contains("LOD0")))
                {
                    continue;
                }
                GameObject part = new GameObject(mf.name);
                part.layer = m_Layer;
                Transform ct = part.transform;
                ct.SetParent(root.transform, false);
                ct.localPosition = pt.InverseTransformPoint(mf.transform.position);
                ct.localRotation = Quaternion.Inverse(pt.rotation) * mf.transform.rotation;
                ct.localScale = mf.transform.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                part.AddComponent<MeshRenderer>().sharedMaterials = src.sharedMaterials;
            }
            return root;
        }

        // Grip rotations are given in X-Y-Z order; Quaternion.Euler applies Z-X-Y.
        static Quaternion EulerXYZ(Vector3 e)
        {
            return Quaternion.AngleAxis(e.z, Vector3.forward) * Quaternion.AngleAxis(e.y, Vector3.up) * Quaternion.AngleAxis(e.x, Vector3.right);
        }

        // Gear prefabs have their origin at the base; move the meshes so the top centre (the carry handle) is the origin.
        static void MoveTopToOrigin(Transform root)
        {
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = -min;
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Matrix4x4 m = Matrix4x4.TRS(mf.transform.localPosition, mf.transform.localRotation, mf.transform.localScale);
                Bounds b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1));
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            if (min.x > max.x)
            {
                return;
            }
            Vector3 top = new Vector3((min.x + max.x) / 2, max.y, (min.z + max.z) / 2);
            for (int i = 0; i < root.childCount; i++)
            {
                root.GetChild(i).localPosition -= top;
            }
        }
    }
}
