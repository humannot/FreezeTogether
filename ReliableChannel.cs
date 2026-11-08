using System;
using System.Collections.Generic;

namespace GameServer
{
    // Reliable, ordered delivery for one direction of one connection over the plain UDP socket of IP mode
    // (Steam P2P delivers reliably by itself). Every packet gets a sequence number; the receiver acknowledges
    // each one, hands them on in order and drops duplicates, and the sender repeats a packet until it is
    // acknowledged. Has no game dependencies so it can be tested on its own.
    public class ReliableChannel
    {
        public const double ResendInterval = 0.25;
        // About 15 seconds of repeats; a peer silent for that long is disconnected by the keep-alive timeout anyway.
        public const int MaxTries = 60;

        sealed class Pending
        {
            public byte[] Data;
            public double LastSent;
            public int Tries;
        }

        readonly object m_Lock = new object();
        int m_NextSendSeq = 1;
        int m_NextExpectedSeq = 1;
        readonly SortedDictionary<int, Pending> m_Pending = new SortedDictionary<int, Pending>();
        readonly Dictionary<int, byte[]> m_OutOfOrder = new Dictionary<int, byte[]>();

        public int PendingCount { get { lock (m_Lock) { return m_Pending.Count; } } }

        public void Reset()
        {
            lock (m_Lock)
            {
                m_NextSendSeq = 1;
                m_NextExpectedSeq = 1;
                m_Pending.Clear();
                m_OutOfOrder.Clear();
            }
        }

        // Registers an outgoing packet; returns its sequence number. The caller sends it right away.
        public int Track(byte[] data, double now)
        {
            lock (m_Lock)
            {
                int seq = m_NextSendSeq++;
                m_Pending[seq] = new Pending { Data = data, LastSent = now, Tries = 1 };
                return seq;
            }
        }

        public void Ack(int seq)
        {
            lock (m_Lock)
            {
                m_Pending.Remove(seq);
            }
        }

        // Packets to send again now, oldest first. Packets repeated MaxTries times are given up.
        public List<KeyValuePair<int, byte[]>> DueForResend(double now, out int givenUp)
        {
            List<KeyValuePair<int, byte[]>> due = new List<KeyValuePair<int, byte[]>>();
            List<int> drop = new List<int>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<int, Pending> p in m_Pending)
                {
                    if (now - p.Value.LastSent < ResendInterval)
                    {
                        continue;
                    }
                    if (p.Value.Tries >= MaxTries)
                    {
                        drop.Add(p.Key);
                        continue;
                    }
                    p.Value.LastSent = now;
                    p.Value.Tries++;
                    due.Add(new KeyValuePair<int, byte[]>(p.Key, p.Value.Data));
                }
                foreach (int seq in drop)
                {
                    m_Pending.Remove(seq);
                }
            }
            givenUp = drop.Count;
            return due;
        }

        // Accepts an incoming packet (always acknowledge it) and returns the packets that are now ready
        // to be handled, in send order. A duplicate or a packet ahead of a gap returns nothing yet.
        public List<byte[]> Receive(int seq, byte[] data)
        {
            List<byte[]> ready = new List<byte[]>();
            lock (m_Lock)
            {
                if (seq < m_NextExpectedSeq || m_OutOfOrder.ContainsKey(seq))
                {
                    return ready;
                }
                if (seq > m_NextExpectedSeq)
                {
                    m_OutOfOrder[seq] = data;
                    return ready;
                }
                ready.Add(data);
                m_NextExpectedSeq++;
                while (m_OutOfOrder.TryGetValue(m_NextExpectedSeq, out byte[] next))
                {
                    m_OutOfOrder.Remove(m_NextExpectedSeq);
                    ready.Add(next);
                    m_NextExpectedSeq++;
                }
            }
            return ready;
        }
    }
}
