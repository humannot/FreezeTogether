using System;
using System.Collections.Generic;
using System.Diagnostics;
using MelonLoader;

namespace GameServer
{
    // Reliable ordered delivery for IP mode, on top of the plain UDP sockets. Packets that a newer copy soon
    // replaces (positions, animal sync, voice, keep-alive, time and weather ticks), the handshake, RCON and the
    // sliced transfers that confirm every slice themselves stay plain UDP. Everything else is wrapped in a
    // RELIABLE packet with a sequence number, acknowledged with RELIABLEACK, repeated until acknowledged and
    // handed to its handler in send order. Steam P2P already sends reliably and is not touched.
    public static class Reliable
    {
        static readonly Stopwatch s_Clock = Stopwatch.StartNew();
        public static double Now { get { return s_Clock.Elapsed.TotalSeconds; } }

        // Client side: the connection to the server.
        public static readonly ReliableChannel ToServer = new ReliableChannel();
        // Server side: one channel per client slot.
        static readonly Dictionary<int, ReliableChannel> s_Clients = new Dictionary<int, ReliableChannel>();

        public static ReliableChannel ForClient(int id)
        {
            lock (s_Clients)
            {
                if (!s_Clients.TryGetValue(id, out ReliableChannel c))
                {
                    c = new ReliableChannel();
                    s_Clients[id] = c;
                }
                return c;
            }
        }

        static readonly HashSet<int> s_PlainServerPackets = new HashSet<int>
        {
            (int)ServerPackets.welcome, (int)ServerPackets.XYZ, (int)ServerPackets.XYZW, (int)ServerPackets.XYZDW,
            (int)ServerPackets.ANIMALSYNC, (int)ServerPackets.ALIGNANIMAL, (int)ServerPackets.KEEPITALIVE,
            (int)ServerPackets.VOICECHAT, (int)ServerPackets.GAMETIME, (int)ServerPackets.SYNCWEATHER,
            (int)ServerPackets.DEDICATEDWEATHER, (int)ServerPackets.PLAYERSSTATUS, (int)ServerPackets.KICKMESSAGE,
            (int)ServerPackets.PINGSERVER, (int)ServerPackets.RCONCONNECTED, (int)ServerPackets.RCONCALLBACK,
            (int)ServerPackets.GOTITEMSLICE, (int)ServerPackets.SLICEDBYTES, (int)ServerPackets.GOTDROPSLICE,
            (int)ServerPackets.GETREQUESTEDITEMSLICE, (int)ServerPackets.GETREQUESTEDFORPLACESLICE,
            (int)ServerPackets.GOTCONTAINERSLICE, (int)ServerPackets.GOTPHOTOSLICE, (int)ServerPackets.BASE64SLICE,
            (int)ServerPackets.READYSENDNEXTSLICE, (int)ServerPackets.READYSENDNEXTSLICEGEAR,
            (int)ServerPackets.READYSENDNEXTSLICEPHOTO, (int)ServerPackets.RELIABLE, (int)ServerPackets.RELIABLEACK,
        };

        static readonly HashSet<int> s_PlainClientPackets = new HashSet<int>
        {
            (int)ClientPackets.welcomeReceived, (int)ClientPackets.XYZ, (int)ClientPackets.XYZW, (int)ClientPackets.XYZDW,
            (int)ClientPackets.ANIMALSYNC, (int)ClientPackets.ALIGNANIMAL, (int)ClientPackets.KEEPITALIVE,
            (int)ClientPackets.VOICECHAT, (int)ClientPackets.GAMETIME, (int)ClientPackets.SYNCWEATHER,
            (int)ClientPackets.DEDICATEDWEATHER, (int)ClientPackets.WEATHERVOLUNTEER, (int)ClientPackets.PINGSERVER,
            (int)ClientPackets.RCONCOMMAND, (int)ClientPackets.GOTITEMSLICE, (int)ClientPackets.SLICEDBYTES,
            (int)ClientPackets.GOTDROPSLICE, (int)ClientPackets.GETREQUESTEDITEMSLICE,
            (int)ClientPackets.GETREQUESTEDFORPLACESLICE, (int)ClientPackets.GOTCONTAINERSLICE,
            (int)ClientPackets.GOTPHOTOSLICE, (int)ClientPackets.BASE64SLICE, (int)ClientPackets.READYSENDNEXTSLICE,
            (int)ClientPackets.READYSENDNEXTSLICEGEAR, (int)ClientPackets.READYSENDNEXTSLICEPHOTO,
            (int)ClientPackets.RELIABLE, (int)ClientPackets.RELIABLEACK,
        };

        static Packet Wrap(int wrapperId, int seq, byte[] inner)
        {
            Packet p = new Packet(wrapperId);
            p.Write(seq);
            p.Write(inner.Length);
            p.Write(inner);
            return p;
        }

        // Client side. `packet` holds [id][payload]; returns it unchanged or a new RELIABLE packet carrying it.
        public static Packet WrapForServer(Packet packet)
        {
            byte[] inner = packet.ToArray();
            if (inner.Length < 4)
            {
                return packet;
            }
            int id = BitConverter.ToInt32(inner, 0);
            if (!Enum.IsDefined(typeof(ClientPackets), id) || s_PlainClientPackets.Contains(id))
            {
                return packet;
            }
            int seq = ToServer.Track(inner, Now);
            return Wrap((int)ClientPackets.RELIABLE, seq, inner);
        }

        // Server side. `packet` holds [length][id][payload] (WriteLength was called); returns it unchanged or a
        // new RELIABLE packet carrying it, with its length written.
        public static Packet WrapForClient(int client, Packet packet)
        {
            byte[] all = packet.ToArray();
            if (all.Length < 8)
            {
                return packet;
            }
            int length = BitConverter.ToInt32(all, 0);
            int id = BitConverter.ToInt32(all, 4);
            if (length != all.Length - 4 || !Enum.IsDefined(typeof(ServerPackets), id) || s_PlainServerPackets.Contains(id))
            {
                return packet;
            }
            byte[] inner = new byte[length];
            Buffer.BlockCopy(all, 4, inner, 0, length);
            int seq = ForClient(client).Track(inner, Now);
            Packet wrapped = Wrap((int)ServerPackets.RELIABLE, seq, inner);
            wrapped.WriteLength();
            return wrapped;
        }

        // Repeats unacknowledged packets; runs every frame on the main thread.
        public static void Tick()
        {
            double now = Now;
#if (!DEDICATED)
            if (FreezeTogether.MyMod.sendMyPosition && !(FreezeTogether.SteamConnect.CanUseSteam && FreezeTogether.MyMod.ConnectedSteamWorks) && ClientUser.udp != null)
            {
                List<KeyValuePair<int, byte[]>> dueToServer = ToServer.DueForResend(now, out int givenUp);
                foreach (KeyValuePair<int, byte[]> due in dueToServer)
                {
                    using (Packet p = Wrap((int)ClientPackets.RELIABLE, due.Key, due.Value))
                    {
                        ClientUser.udp.SendRaw(p);
                    }
                }
                if (givenUp > 0)
                {
                    MelonLogger.Msg(ConsoleColor.Red, "[Reliable] Server did not confirm " + givenUp + " packet(s), dropped them.");
                }
            }
#endif
            if (FreezeTogether.MyMod.iAmHost && !Server.UsingSteamWorks && Server.clients != null)
            {
                for (int i = 1; i <= Server.MaxPlayers; i++)
                {
                    if (!Server.clients.TryGetValue(i, out Client c) || c.RCON || c.udp == null || c.udp.endPoint == null)
                    {
                        continue;
                    }
                    List<KeyValuePair<int, byte[]>> dueToClient = ForClient(i).DueForResend(now, out int givenUp);
                    foreach (KeyValuePair<int, byte[]> due in dueToClient)
                    {
                        using (Packet p = Wrap((int)ServerPackets.RELIABLE, due.Key, due.Value))
                        {
                            p.WriteLength();
                            Server.SendUDPData(c.udp.endPoint, p);
                        }
                    }
                    if (givenUp > 0)
                    {
                        Log("[Reliable] Client " + i + " did not confirm " + givenUp + " packet(s), dropped them.");
                    }
                }
            }
        }

        static void Log(string msg)
        {
            MelonLogger.Msg(ConsoleColor.Red, msg);
        }

        // Client side: RELIABLE packet from the server.
        public static void ClientReceive(Packet packet)
        {
            int seq = packet.ReadInt();
            int length = packet.ReadInt();
            byte[] inner = packet.ReadBytes(length);
            using (Packet ack = new Packet((int)ClientPackets.RELIABLEACK))
            {
                ack.Write(seq);
                ClientUser.udp.SendData(ack);
            }
            foreach (byte[] data in ToServer.Receive(seq, inner))
            {
                using (Packet p = new Packet(data))
                {
                    int id = p.ReadInt();
                    if (ClientUser.packetHandlers.TryGetValue(id, out ClientUser.PacketHandler handler))
                    {
                        handler(p);
                    }
                }
            }
        }

        // Server side: RELIABLE packet from a client.
        public static void ServerReceive(int client, Packet packet)
        {
            int seq = packet.ReadInt();
            int length = packet.ReadInt();
            byte[] inner = packet.ReadBytes(length);
            using (Packet ack = new Packet((int)ServerPackets.RELIABLEACK))
            {
                ack.Write(seq);
                ServerSend.SendUDPData(client, ack, true);
            }
            foreach (byte[] data in ForClient(client).Receive(seq, inner))
            {
                using (Packet p = new Packet(data))
                {
                    int id = p.ReadInt();
                    if (Server.packetHandlers.TryGetValue(id, out Server.PacketHandler handler))
                    {
                        handler(client, p);
                    }
                }
            }
        }
    }
}
