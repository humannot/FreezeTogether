using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using GameServer;
using MelonLoader;
using UnityEngine;

namespace FreezeTogether
{
    // Pre-game lobby for IP mode, the counterpart of the Steam lobby: players join from the main menu, are
    // listed at the bottom of the screen, vote for the region and experience mode, and the host starts the
    // game. It runs on the server port before the game server exists. When the host has loaded the game it
    // tells everyone to connect and hands the port over to the game server.
    // Datagrams start with Magic; game clients only send slot ids, -1 (RCON) or -2 (ping) there.
    public static class IpLobby
    {
        public const int Magic = -4;
        const int MsgHello = 1, MsgKeepAlive = 2, MsgBye = 3, MsgWelcome = 11, MsgRefuse = 12, MsgState = 13, MsgGo = 14, MsgGameRunning = 15;
        const double MemberTimeout = 6;
        const double HostTimeout = 10;
        // The host's frames stall while it loads the game; nobody is dropped meanwhile.
        const double LoadingTimeout = 120;
        const double HelloTimeout = 3;
        const double GoDelay = 2;
        const ulong HostId = 1;

        sealed class Member
        {
            public IPEndPoint EndPoint;
            public string Name;
            public ulong Id;
            public int RegionVote = -1;
            public int ExpVote = -1;
            public double LastSeen;
        }

        public static bool IsHost { get; private set; }
        public static bool IsClient { get; private set; }
        public static bool Active { get { return IsHost || IsClient; } }

        static UdpClient s_Socket;
        static readonly ConcurrentQueue<KeyValuePair<IPEndPoint, byte[]>> s_Inbox = new ConcurrentQueue<KeyValuePair<IPEndPoint, byte[]>>();
        static readonly Dictionary<string, Member> s_Members = new Dictionary<string, Member>();
        static double s_NextSecond;
        static double s_LastHeard;
        static double s_JoiningSince = -1;
        static double s_NextHello;
        static double s_ConnectGameAt = -1;
        static IPEndPoint s_HostEndPoint;
        static string s_HostAddress = "";
        static int s_MyRegionVote = -1;
        static int s_MyExpVote = -1;
        static int s_Limit = 0;
        static List<KeyValuePair<ulong, string>> s_Roster = new List<KeyValuePair<ulong, string>>();
        static List<int> s_RegionVotes = new List<int>();
        static List<int> s_ExpVotes = new List<int>();
        static Texture2D s_Avatar;

        static double Now { get { return Reliable.Now; } }

        // --- socket ---

        static void Receive(IAsyncResult result)
        {
            UdpClient socket = (UdpClient)result.AsyncState;
            try
            {
                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = socket.EndReceive(result, ref from);
                if (data.Length >= 8 && BitConverter.ToInt32(data, 0) == Magic)
                {
                    s_Inbox.Enqueue(new KeyValuePair<IPEndPoint, byte[]>(from, data));
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // ICMP "port unreachable" from a peer that left; keep listening.
            }
            try
            {
                socket.BeginReceive(Receive, socket);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        static byte[] Build(int msg, Action<BinaryWriter> write)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(Magic);
            w.Write(msg);
            write?.Invoke(w);
            return ms.ToArray();
        }

        static void Send(IPEndPoint to, byte[] data)
        {
            try
            {
                if (s_Socket != null && to != null)
                {
                    s_Socket.Send(data, data.Length, to);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Msg("[IpLobby] Send failed: " + e.Message);
            }
        }

        static void CloseSocket()
        {
            if (s_Socket != null)
            {
                try { s_Socket.Close(); } catch (Exception) { }
                s_Socket = null;
            }
            while (s_Inbox.TryDequeue(out _)) { }
        }

        // Reply of a running game server to a lobby datagram: the host is already in the game.
        public static byte[] GameRunningReply()
        {
            return Build(MsgGameRunning, null);
        }

        // --- host ---

        public static bool StartHost(int port)
        {
            CloseSocket();
            try
            {
                s_Socket = new UdpClient(port);
            }
            catch (Exception e)
            {
                MelonLogger.Msg(ConsoleColor.Red, "[IpLobby] Can't open lobby on port " + port + ": " + e.Message);
                MyMod.DoOKMessage("Connection failed", Loc.T("Can't open the lobby, the port is busy: ") + port);
                return false;
            }
            IsHost = true;
            IsClient = false;
            s_Members.Clear();
            s_MyRegionVote = -1;
            s_MyExpVote = -1;
            s_Limit = MyMod.MaxPlayers;
            MyMod.LobbyState = "";
            SetState("WaitForPlayers");
            s_Socket.BeginReceive(Receive, s_Socket);
            MelonLogger.Msg("[IpLobby] Lobby open on port " + port);
            return true;
        }

        // The host loaded the game: everyone connects to the game server, which takes over the port.
        public static void HandOverToGame()
        {
            if (!IsHost)
            {
                return;
            }
            byte[] go = Build(MsgGo, null);
            for (int i = 0; i < 3; i++)
            {
                foreach (Member m in s_Members.Values)
                {
                    Send(m.EndPoint, go);
                }
            }
            MelonLogger.Msg("[IpLobby] Game started, " + s_Members.Count + " player(s) told to connect");
            Close();
        }

        static string RefusalFor(string version, string name)
        {
            if (version != MyMod.BuildInfo.Version)
            {
                return "Wrong mod version! Server using version " + MyMod.BuildInfo.Version;
            }
            string clean = (name ?? "").Trim();
            if (string.Equals(clean, (MyMod.MyChatName ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                || s_Members.Values.Any(m => string.Equals(clean, m.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return "A player with this nickname is already in the lobby. Change your nickname and connect again.";
            }
            // Slot 0 is the host, so a server for N players has N - 1 client slots.
            if (s_Members.Count + 1 >= s_Limit)
            {
                return "Server is full!";
            }
            if (MyMod.LobbyState == "StartingGame")
            {
                return "The game is already starting, connect again in a minute.";
            }
            return null;
        }

        static void HostHandle(IPEndPoint from, BinaryReader r, int msg)
        {
            string key = from.ToString();
            s_Members.TryGetValue(key, out Member member);
            if (msg == MsgHello)
            {
                string version = r.ReadString();
                string name = r.ReadString();
                if (member != null)
                {
                    // Hello repeated before our welcome arrived.
                    Send(from, Build(MsgWelcome, w => w.Write(s_Limit)));
                    return;
                }
                string refusal = RefusalFor(version, name);
                if (refusal != null)
                {
                    MelonLogger.Msg("[IpLobby] Refused " + name + " (" + from + "): " + refusal);
                    Send(from, Build(MsgRefuse, w => w.Write(refusal)));
                    return;
                }
                member = new Member { EndPoint = from, Name = name, Id = 100UL + (ulong)(s_Members.Count + 1) + (ulong)(key.GetHashCode() & 0xFFFF) * 1000UL, LastSeen = Now };
                s_Members[key] = member;
                MelonLogger.Msg("[IpLobby] " + name + " joined the lobby from " + from);
                Send(from, Build(MsgWelcome, w => w.Write(s_Limit)));
                s_NextSecond = 0;
            }
            else if (member == null)
            {
                return;
            }
            else if (msg == MsgKeepAlive)
            {
                member.RegionVote = r.ReadInt32();
                member.ExpVote = r.ReadInt32();
                member.LastSeen = Now;
            }
            else if (msg == MsgBye)
            {
                MelonLogger.Msg("[IpLobby] " + member.Name + " left the lobby");
                s_Members.Remove(key);
                s_NextSecond = 0;
            }
        }

        static void HostSecond()
        {
            double timeout = MyMod.LobbyState == "StartingGame" ? LoadingTimeout : MemberTimeout;
            foreach (string key in s_Members.Where(p => Now - p.Value.LastSeen > timeout).Select(p => p.Key).ToList())
            {
                MelonLogger.Msg("[IpLobby] " + s_Members[key].Name + " timed out");
                s_Members.Remove(key);
            }

            s_RegionVotes = EmptyRegionVotes();
            s_ExpVotes = EmptyExpVotes();
            if (MyMod.LobbyState == "Vote")
            {
                CountVote(s_MyRegionVote, s_MyExpVote);
                foreach (Member m in s_Members.Values)
                {
                    CountVote(m.RegionVote, m.ExpVote);
                }
                MyMod.LobbyVoteLeft--;
                if (MyMod.LobbyVoteLeft <= 0)
                {
                    PickVoted();
                }
            }

            s_Roster = new List<KeyValuePair<ulong, string>> { new KeyValuePair<ulong, string>(HostId, MyMod.MyChatName) };
            s_Roster.AddRange(s_Members.Values.Select(m => new KeyValuePair<ulong, string>(m.Id, m.Name)));

            byte[] state = Build(MsgState, w =>
            {
                w.Write(MyMod.LobbyState ?? "");
                w.Write(MyMod.LobbyVoteLeft);
                w.Write(MyMod.LobbyStartingRegion);
                w.Write(MyMod.LobbyStartingExperience);
                w.Write(MyMod.SelectedSaveSeed);
                w.Write(MyMod.ServerConfig.m_PlayersSpawnType);
                w.Write(s_Limit);
                w.Write(s_Roster.Count);
                foreach (KeyValuePair<ulong, string> p in s_Roster)
                {
                    w.Write(p.Key);
                    w.Write(p.Value);
                }
                WriteInts(w, s_RegionVotes);
                WriteInts(w, s_ExpVotes);
            });
            foreach (Member m in s_Members.Values)
            {
                Send(m.EndPoint, state);
            }
        }

        static void CountVote(int region, int exp)
        {
            if (region >= 0 && region < s_RegionVotes.Count)
            {
                s_RegionVotes[region]++;
            }
            if (exp >= 0 && exp < s_ExpVotes.Count)
            {
                s_ExpVotes[exp]++;
            }
        }

        // Same rule as the Steam lobby: the most voted region and mode; random region for random spawns.
        static void PickVoted()
        {
            int bestRegion = 0;
            int bestExp = 0;
            for (int i = 0; i < s_RegionVotes.Count; i++)
            {
                if (s_RegionVotes[i] > s_RegionVotes[bestRegion]) bestRegion = i;
            }
            for (int i = 0; i < s_ExpVotes.Count; i++)
            {
                if (s_ExpVotes[i] > s_ExpVotes[bestExp]) bestExp = i;
            }
            MyMod.LobbyStartingRegion = bestRegion;
            MyMod.LobbyStartingExperience = bestExp;
            if (MyMod.ServerConfig.m_PlayersSpawnType == 1 || MyMod.ServerConfig.m_PlayersSpawnType == 2)
            {
                MyMod.LobbyStartingRegion = (int)Shared.GameRegion.RandomRegion;
            }
            if (MyMod.Golosovanie)
            {
                UnityEngine.Object.Destroy(MyMod.Golosovanie);
            }
            SetState("SelectedNewSave");
            SteamConnect.Main.OnSaveSlotChanged();
        }

        // --- client ---

        // Connect by IP: try the lobby first; a host already in the game (or an older one) gets the normal connection.
        public static void Join(string address)
        {
            Close();
            s_HostAddress = address;
            try
            {
                string host = address;
                int port = 26950;
                if (address.Contains(':'))
                {
                    string[] parts = address.Split(':');
                    host = parts[0];
                    port = Convert.ToInt32(parts[1]);
                }
                IPAddress ip = Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
                s_HostEndPoint = new IPEndPoint(ip, port);
                s_Socket = new UdpClient(0);
                s_Socket.BeginReceive(Receive, s_Socket);
            }
            catch (Exception e)
            {
                MelonLogger.Msg("[IpLobby] Can't reach " + address + ": " + e.Message + ", connecting directly");
                CloseSocket();
                ClientUser.DoConnectToIp(address);
                return;
            }
            s_JoiningSince = Now;
            s_NextHello = 0;
            MyMod.DoPleaseWait("Connecting", "Trying to join...");
        }

        public static void Leave()
        {
            if (IsHost)
            {
                byte[] closed = Build(MsgRefuse, w => w.Write("The host closed the lobby."));
                foreach (Member m in s_Members.Values)
                {
                    Send(m.EndPoint, closed);
                }
            }
            else if (IsClient)
            {
                Send(s_HostEndPoint, Build(MsgBye, null));
            }
            Close();
        }

        static void Close()
        {
            CloseSocket();
            IsHost = false;
            IsClient = false;
            s_JoiningSince = -1;
            s_Members.Clear();
            foreach (ulong id in new List<ulong>(MyMod.LobbyElements.Keys))
            {
                MyMod.RemovePersonFromLobby(id);
            }
            MyMod.LobbyState = "";
        }

        static void ClientHandle(IPEndPoint from, BinaryReader r, int msg)
        {
            if (s_HostEndPoint == null || !from.Address.Equals(s_HostEndPoint.Address) || from.Port != s_HostEndPoint.Port)
            {
                return;
            }
            s_LastHeard = Now;
            if (msg == MsgWelcome && s_JoiningSince >= 0)
            {
                s_JoiningSince = -1;
                s_Limit = r.ReadInt32();
                IsClient = true;
                s_MyRegionVote = -1;
                s_MyExpVote = -1;
                MyMod.LobbyState = "";
                MyMod.RemovePleaseWait();
                MelonLogger.Msg("[IpLobby] Joined the lobby at " + s_HostAddress);
                MPSaveManager.SaveLastConnectedServer(s_HostAddress);
                MyMod.m_Panel_Sandbox.Enable(true);
                MyMod.m_Panel_Sandbox.m_TitleLocalizationId = Loc.T("MULTIPLAYER");
                MyMod.m_Panel_Sandbox.m_BasicMenu.UpdateTitle(Loc.T("MULTIPLAYER"));
                MenuChange.ChangeMenuItems("Lobby");
            }
            else if (msg == MsgRefuse)
            {
                string reason = r.ReadString();
                MelonLogger.Msg("[IpLobby] Refused: " + reason);
                MyMod.RemovePleaseWait();
                bool wasInLobby = IsClient;
                Close();
                if (wasInLobby)
                {
                    MenuChange.CloseLobbyUI();
                    MenuChange.ChangeMenuItems("Multiplayer");
                }
                MyMod.DoOKMessage("DISCONNECTED", reason);
            }
            else if (msg == MsgGameRunning && s_JoiningSince >= 0)
            {
                MelonLogger.Msg("[IpLobby] Host is already in the game, connecting directly");
                Close();
                MyMod.RemovePleaseWait();
                ClientUser.DoConnectToIp(s_HostAddress);
            }
            else if (msg == MsgState && IsClient)
            {
                ApplyState(r);
            }
            else if (msg == MsgGo && IsClient)
            {
                MelonLogger.Msg("[IpLobby] Host started the game, connecting");
                Close();
                MenuChange.CloseLobbyUI();
                MenuChange.ChangeMenuItems("Original");
                MyMod.DoPleaseWait("Connecting", "Host started the game...");
                s_ConnectGameAt = Now + GoDelay;
            }
        }

        static void ApplyState(BinaryReader r)
        {
            string state = r.ReadString();
            MyMod.LobbyVoteLeft = r.ReadInt32();
            int region = r.ReadInt32();
            int exp = r.ReadInt32();
            int seed = r.ReadInt32();
            MyMod.ServerConfig.m_PlayersSpawnType = r.ReadInt32();
            s_Limit = r.ReadInt32();
            int count = r.ReadInt32();
            s_Roster = new List<KeyValuePair<ulong, string>>();
            for (int i = 0; i < count; i++)
            {
                ulong id = r.ReadUInt64();
                s_Roster.Add(new KeyValuePair<ulong, string>(id, r.ReadString()));
            }
            s_RegionVotes = ReadInts(r);
            s_ExpVotes = ReadInts(r);

            bool slotChanged = region != MyMod.LobbyStartingRegion || exp != MyMod.LobbyStartingExperience || seed != MyMod.SelectedSaveSeed;
            MyMod.LobbyStartingRegion = region;
            MyMod.LobbyStartingExperience = exp;
            MyMod.SelectedSaveSeed = seed;
            if (state != "" && state != MyMod.LobbyState)
            {
                SetState(state);
            }
            else if (slotChanged)
            {
                SteamConnect.Main.OnSaveSlotChanged();
            }
        }

        // --- shared ---

        public static void SetState(string state)
        {
            if (MyMod.LobbyState == state)
            {
                return;
            }
            MyMod.LobbyState = state;
            if (state == "Vote")
            {
                MyMod.LobbyVoteLeft = 62;
                s_MyRegionVote = -1;
                s_MyExpVote = -1;
            }
            s_NextSecond = 0;
            SteamConnect.Main.OnLobbyStateChanged();
        }

        public static void VoteForRegion(int region)
        {
            s_MyRegionVote = region;
            MelonLogger.Msg("[IpLobby] Voted for region " + region);
            SendKeepAlive();
        }

        public static void VoteForExperience(int exp)
        {
            s_MyExpVote = exp;
            MelonLogger.Msg("[IpLobby] Voted for experience mode " + exp);
            SendKeepAlive();
        }

        public static void TickNow()
        {
            s_NextSecond = 0;
        }

        static void SendKeepAlive()
        {
            if (IsClient)
            {
                Send(s_HostEndPoint, Build(MsgKeepAlive, w => { w.Write(s_MyRegionVote); w.Write(s_MyExpVote); }));
            }
            else if (IsHost)
            {
                s_NextSecond = 0;
            }
        }

        public static void Tick()
        {
            while (s_Inbox.TryDequeue(out KeyValuePair<IPEndPoint, byte[]> item))
            {
                try
                {
                    BinaryReader r = new BinaryReader(new MemoryStream(item.Value));
                    r.ReadInt32();
                    int msg = r.ReadInt32();
                    if (IsHost)
                    {
                        HostHandle(item.Key, r, msg);
                    }
                    else
                    {
                        ClientHandle(item.Key, r, msg);
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Msg("[IpLobby] Bad datagram from " + item.Key + ": " + e.Message);
                }
            }

            if (s_ConnectGameAt >= 0 && Now >= s_ConnectGameAt)
            {
                s_ConnectGameAt = -1;
                MyMod.RemovePleaseWait();
                ClientUser.DoConnectToIp(s_HostAddress);
            }

            if (s_JoiningSince >= 0)
            {
                if (Now - s_JoiningSince > HelloTimeout)
                {
                    MelonLogger.Msg("[IpLobby] No lobby at " + s_HostAddress + ", connecting directly");
                    Close();
                    MyMod.RemovePleaseWait();
                    ClientUser.DoConnectToIp(s_HostAddress);
                }
                else if (Now >= s_NextHello)
                {
                    s_NextHello = Now + 0.5;
                    Send(s_HostEndPoint, Build(MsgHello, w => { w.Write(MyMod.BuildInfo.Version); w.Write(MyMod.MyChatName); }));
                }
                return;
            }

            if (!Active || Now < s_NextSecond)
            {
                return;
            }
            s_NextSecond = Now + 1;

            if (IsHost)
            {
                HostSecond();
            }
            else
            {
                double timeout = MyMod.LobbyState == "StartingGame" ? LoadingTimeout : HostTimeout;
                if (Now - s_LastHeard > timeout)
                {
                    MelonLogger.Msg("[IpLobby] Lost the lobby host");
                    Close();
                    MenuChange.CloseLobbyUI();
                    MenuChange.ChangeMenuItems("Multiplayer");
                    MyMod.DoOKMessage("DISCONNECTED", "Lost connection to the lobby.");
                    return;
                }
                SendKeepAlive();
            }
            UpdateUi();
        }

        // --- ui (the Steam lobby's screen) ---

        static void UpdateUi()
        {
            if (MyMod.LobbyUI == null || MyMod.DedicatedServerAppMode || MyMod.level_name != "MainMenu")
            {
                return;
            }
            UnityEngine.UI.Text header = MyMod.LobbyUI.transform.GetChild(2).GetComponent<UnityEngine.UI.Text>();
            HashSet<ulong> ids = new HashSet<ulong>();
            foreach (KeyValuePair<ulong, string> p in s_Roster)
            {
                ids.Add(p.Key);
                if (!MyMod.LobbyContains(p.Key))
                {
                    MyMod.AddPersonToLobby(p.Value, p.Key, Avatar());
                }
                MyMod.SetPersonNameFromLobby(p.Key, p.Value);
                NameLabel(p.Key, p.Value, header);
            }
            foreach (ulong id in new List<ulong>(MyMod.LobbyElements.Keys))
            {
                if (!ids.Contains(id))
                {
                    MyMod.RemovePersonFromLobby(id);
                }
            }

            if (MyMod.LobbyState == "Vote")
            {
                SteamConnect.Main.ClearVotesObjects();
                SteamConnect.Main.UpdateVoteObjects(s_RegionVotes.Count > 0 ? s_RegionVotes : EmptyRegionVotes(), s_ExpVotes.Count > 0 ? s_ExpVotes : EmptyExpVotes());
                if (Loc.IsRussian)
                {
                    MyMod.StartGOLOSOVANIE();
                }
                header.text = Loc.T("VOTING: " + Math.Max(0, MyMod.LobbyVoteLeft));
            }
            else
            {
                header.text = Loc.T("LOBBY " + s_Roster.Count + "/" + s_Limit);
            }
        }

        // Without a Steam avatar the square says nothing, so the nickname is written under it.
        static void NameLabel(ulong id, string name, UnityEngine.UI.Text style)
        {
            if (!MyMod.LobbyElements.TryGetValue(id, out GameObject element) || element == null)
            {
                return;
            }
            Transform existing = element.transform.Find("IpLobbyName");
            UnityEngine.UI.Text text;
            if (existing == null)
            {
                GameObject label = new GameObject("IpLobbyName");
                label.transform.SetParent(element.transform, false);
                text = label.AddComponent<UnityEngine.UI.Text>();
                text.font = style.font;
                // The roster strip clips anything outside the squares, so the name goes inside its square.
                text.fontSize = 14;
                text.resizeTextForBestFit = true;
                text.resizeTextMinSize = 8;
                text.resizeTextMaxSize = 14;
                text.alignment = TextAnchor.MiddleCenter;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Truncate;
                text.color = Color.white;
                text.raycastTarget = false;
                RectTransform rt = label.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = new Vector2(3f, 3f);
                rt.offsetMax = new Vector2(-3f, -3f);
            }
            else
            {
                text = existing.GetComponent<UnityEngine.UI.Text>();
            }
            if (text.text != name)
            {
                text.text = name;
            }
        }

        // Players joined by IP have no Steam avatar.
        static Texture2D Avatar()
        {
            if (s_Avatar == null)
            {
                s_Avatar = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                Color32[] pixels = new Color32[64 * 64];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = new Color32(70, 78, 84, 255);
                }
                s_Avatar.SetPixels32(pixels);
                s_Avatar.Apply();
                s_Avatar.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            }
            return s_Avatar;
        }

        static List<int> EmptyRegionVotes()
        {
            return Enumerable.Repeat(0, Enum.GetNames(typeof(Shared.GameRegion)).Length).ToList();
        }

        static List<int> EmptyExpVotes()
        {
            return Enumerable.Repeat(0, (int)ExperienceModeType.NUM_MODES).ToList();
        }

        static void WriteInts(BinaryWriter w, List<int> values)
        {
            w.Write(values.Count);
            foreach (int v in values)
            {
                w.Write(v);
            }
        }

        static List<int> ReadInts(BinaryReader r)
        {
            int n = r.ReadInt32();
            List<int> values = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                values.Add(r.ReadInt32());
            }
            return values;
        }
    }

    // Lobby screen actions, sent to the IP lobby while it is open and to the Steam lobby otherwise.
    public static class Lobby
    {
        public static bool InLobby { get { return MyMod.MyLobby != "" || IpLobby.Active; } }
        public static bool IsHost { get { return IpLobby.Active ? IpLobby.IsHost : SteamConnect.IsMyLobby; } }

        public static void SetState(string state)
        {
            if (IpLobby.Active)
            {
                if (IpLobby.IsHost) IpLobby.SetState(state);
                return;
            }
            SteamConnect.Main.SetLobbyState(state);
        }

        public static void SetSaveSeed(int seed)
        {
            if (IpLobby.Active)
            {
                MyMod.SelectedSaveSeed = seed;
                return;
            }
            SteamConnect.Main.SetSaveSlotSeed(seed);
        }

        public static void SetNewGameSettings(int region, int exp)
        {
            if (IpLobby.Active)
            {
                MyMod.LobbyStartingRegion = region;
                MyMod.LobbyStartingExperience = exp;
                return;
            }
            SteamConnect.Main.SetNewGameSettings(region, exp);
        }

        // The host ends the vote early: the countdown runs out on the next lobby second and the result is picked.
        public static void EndVote()
        {
            MyMod.LobbyVoteLeft = 0;
            if (IpLobby.IsHost)
            {
                IpLobby.TickNow();
            }
        }

        public static void VoteForRegion(int region)
        {
            if (IpLobby.Active)
            {
                IpLobby.VoteForRegion(region);
            }
            else if (SteamConnect.CanUseSteam)
            {
                SteamConnect.Main.VoteForRegion(region);
            }
        }

        public static void VoteForExperience(int exp)
        {
            if (IpLobby.Active)
            {
                IpLobby.VoteForExperience(exp);
            }
            else if (SteamConnect.CanUseSteam)
            {
                SteamConnect.Main.VoteForExperienceMode(exp);
            }
        }

        // START SERVER: the host loads the chosen save; once in the game the server starts and the lobby follows.
        public static void StartGame()
        {
            if (IpLobby.IsHost)
            {
                string state = MyMod.LobbyState;
                IpLobby.SetState("StartingGame");
                MenuChange.ChangeMenuItems("Original");
                if (state == "SelectedNewSave")
                {
                    MyMod.ForceCreateSlotForPlaying(MyMod.LobbyStartingRegion, MyMod.LobbyStartingExperience);
                }
                else if (state == "SelectedSave")
                {
                    MyMod.ForceLoadSlotByName(MyMod.SelectedSaveName);
                }
                // The game server of IP mode starts after loading, not the Steam one.
                MyMod.HostFromLobbyAfterLoader = false;
                MyMod.StartServerAfterSelectSave = true;
                return;
            }
            if (SteamConnect.CanUseSteam)
            {
                if (MyMod.LobbyState == "SelectedNewSave")
                {
                    SteamConnect.Main.CreateSaveForHosting();
                }
                else if (MyMod.LobbyState == "SelectedSave")
                {
                    SteamConnect.Main.LoadSaveForHosting();
                }
                SteamConnect.Main.SetLobbyState("StartingGame");
            }
        }
    }
}
