using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;
using Steamworks;
using UnityEngine;

namespace SkyCoop
{
    // The game and the mod each carry a Steamworks.NET, and both read Steam's callback queue (manual dispatch).
    // A callback goes to whichever reads it first, so the mod lost about half of its lobby events (lobby created,
    // invite accepted, join by rich presence, lobby list, member left) and the game lost some of its own. The
    // game's read is replaced by this one, which reads the queue once per frame and hands every callback and call
    // result to the subscribers of both.
    public static class SteamCallbacks
    {
        const string SteamApi = "steam_api64";

        [DllImport(SteamApi, CallingConvention = CallingConvention.Cdecl)]
        static extern void SteamAPI_ManualDispatch_RunFrame(int hSteamPipe);

        [DllImport(SteamApi, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        static extern bool SteamAPI_ManualDispatch_GetNextCallback(int hSteamPipe, IntPtr pCallbackMsg);

        [DllImport(SteamApi, CallingConvention = CallingConvention.Cdecl)]
        static extern void SteamAPI_ManualDispatch_FreeLastCallback(int hSteamPipe);

        [DllImport(SteamApi, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        static extern bool SteamAPI_ManualDispatch_GetAPICallResult(int hSteamPipe, ulong hSteamAPICall, IntPtr pCallback, int cubCallback, int iCallbackExpected, [MarshalAs(UnmanagedType.I1)] out bool pbFailed);

        // The mod's Steamworks.NET keeps its subscribers in internal fields.
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
        static readonly Type ModDispatcher = typeof(SteamAPI).Assembly.GetType("Steamworks.CallbackDispatcher");
        static readonly FieldInfo ModCallbacksField = ModDispatcher.GetField("m_registeredCallbacks", Hidden);
        static readonly FieldInfo ModCallResultsField = ModDispatcher.GetField("m_registeredCallResults", Hidden);
        static readonly FieldInfo ModSyncField = ModDispatcher.GetField("m_sync", Hidden);
        static readonly MethodInfo ModOnRunCallback = typeof(Steamworks.Callback).GetMethod("OnRunCallback", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly MethodInfo ModOnRunCallResult = typeof(Steamworks.CallResult).GetMethod("OnRunCallResult", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly MethodInfo ModSetUnregistered = typeof(Steamworks.CallResult).GetMethod("SetUnregistered", BindingFlags.NonPublic | BindingFlags.Instance);

        static IntPtr s_Msg = IntPtr.Zero;
        static int s_Frame = -1;

        // Called from the game's frame (patched RunFrame) and from the mod's update; runs once per frame.
        public static void Pump()
        {
            if (Time.frameCount == s_Frame)
            {
                return;
            }
            s_Frame = Time.frameCount;
            if (s_Msg == IntPtr.Zero)
            {
                s_Msg = Marshal.AllocHGlobal(Marshal.SizeOf<CallbackMsg_t>());
            }
            int pipe = (int)SteamAPI.GetHSteamPipe();
            SteamAPI_ManualDispatch_RunFrame(pipe);
            while (SteamAPI_ManualDispatch_GetNextCallback(pipe, s_Msg))
            {
                try
                {
                    CallbackMsg_t msg = Marshal.PtrToStructure<CallbackMsg_t>(s_Msg);
                    if (msg.m_iCallback == SteamAPICallCompleted_t.k_iCallback)
                    {
                        CallCompleted(pipe, msg.m_pubParam);
                    }
                    else
                    {
                        RunModCallbacks(msg.m_iCallback, msg.m_pubParam);
                        RunGameCallbacks(msg.m_iCallback, msg.m_pubParam);
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[SteamCallbacks] " + e);
                }
                finally
                {
                    SteamAPI_ManualDispatch_FreeLastCallback(pipe);
                }
            }
        }

        static void RunModCallbacks(int id, IntPtr param)
        {
            List<Steamworks.Callback> list;
            lock (ModSyncField.GetValue(null))
            {
                var all = (Dictionary<int, List<Steamworks.Callback>>)ModCallbacksField.GetValue(null);
                if (!all.TryGetValue(id, out List<Steamworks.Callback> registered))
                {
                    return;
                }
                list = new List<Steamworks.Callback>(registered);
            }
            foreach (Steamworks.Callback cb in list)
            {
                try
                {
                    ModOnRunCallback.Invoke(cb, new object[] { param });
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[SteamCallbacks] mod callback " + id + ": " + e);
                }
            }
        }

        static void RunGameCallbacks(int id, IntPtr param)
        {
            var all = Il2CppSteamworks.CallbackDispatcher.m_registeredCallbacks;
            if (all == null || !all.ContainsKey(id))
            {
                return;
            }
            var registered = all[id];
            var list = new List<Il2CppSteamworks.Callback>();
            for (int i = 0; i < registered.Count; i++)
            {
                list.Add(registered[i]);
            }
            foreach (Il2CppSteamworks.Callback cb in list)
            {
                try
                {
                    cb.OnRunCallback(param);
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[SteamCallbacks] game callback " + id + ": " + e);
                }
            }
        }

        static void CallCompleted(int pipe, IntPtr param)
        {
            SteamAPICallCompleted_t done = Marshal.PtrToStructure<SteamAPICallCompleted_t>(param);
            ulong call = (ulong)done.m_hAsyncCall;

            List<Steamworks.CallResult> mod = null;
            lock (ModSyncField.GetValue(null))
            {
                var all = (Dictionary<ulong, List<Steamworks.CallResult>>)ModCallResultsField.GetValue(null);
                if (all.TryGetValue(call, out mod))
                {
                    all.Remove(call);
                }
            }
            Il2CppSystem.Collections.Generic.List<Il2CppSteamworks.CallResult> game = null;
            var gameAll = Il2CppSteamworks.CallbackDispatcher.m_registeredCallResults;
            if (gameAll != null && gameAll.ContainsKey(call))
            {
                game = gameAll[call];
                gameAll.Remove(call);
            }
            if (mod == null && game == null)
            {
                return;
            }

            IntPtr result = Marshal.AllocHGlobal((int)done.m_cubParam);
            try
            {
                if (!SteamAPI_ManualDispatch_GetAPICallResult(pipe, call, result, (int)done.m_cubParam, done.m_iCallback, out bool failed))
                {
                    return;
                }
                if (mod != null)
                {
                    foreach (Steamworks.CallResult cr in mod)
                    {
                        ModOnRunCallResult.Invoke(cr, new object[] { result, failed, call });
                        ModSetUnregistered.Invoke(cr, null);
                    }
                }
                if (game != null)
                {
                    for (int i = 0; i < game.Count; i++)
                    {
                        game[i].OnRunCallResult(result, failed, call);
                        game[i].SetUnregistered();
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(result);
            }
        }

        // The game's own read of the queue; replaced while the mod's Steam is running.
        [HarmonyLib.HarmonyPatch(typeof(Il2CppSteamworks.CallbackDispatcher), "RunFrame")]
        internal static class CallbackDispatcher_RunFrame
        {
            private static bool Prefix(bool isGameServer)
            {
                if (isGameServer || !SteamConnect.CanUseSteam)
                {
                    return true;
                }
                Pump();
                return false;
            }
        }
    }
}
