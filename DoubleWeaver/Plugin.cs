﻿using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Hooking;
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using Dalamud.IoC;
using Dalamud.Game;
using Dalamud.Game.Gui;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.ClientState;
using Dalamud.Data;
using Dalamud.Logging;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace DoubleWeaver
{
    public class Plugin : IDalamudPlugin
    {
        public string Name => "Double Weaver";

        private const string commandName = "/doubleweaver";

        private Dictionary<uint, Stopwatch> actionRequestTime = new Dictionary<uint, Stopwatch> { };
        private long RTT = 0; // set a default value if PingPlugin is not installed
        private long LastRTT = 0;

        private Configuration configuration;
        private PluginUI ui;

        public IntPtr ActionEffectFunc;
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate char ActionEffectFuncDelegate(Int64 a1, int a2, Int16 a3, IntPtr a4, int size);
        private Hook<ActionEffectFuncDelegate> ActionEffectFuncHook;

        public IntPtr ActionRequestFunc;
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate Int64 ActionRequestFuncDelegate(Int64 a1, uint a2, uint a3, Int64 a4, IntPtr a5, uint a6);
        private Hook<ActionRequestFuncDelegate> ActionRequestFuncHook;
        /*
        public IntPtr AdjustActionIdFunc;
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate Int64 AdjustActionIdFuncDelegate(Int64 a1, int a2);
        private Hook<AdjustActionIdFuncDelegate> AdjustActionIdFuncHook;
        */

        private delegate void UpdateRTTDelegate(ExpandoObject expando);

        [PluginService]
        public static ICommandManager CmdManager { get; private set; }
        [PluginService]
        public static IFramework Framework { get; private set; }
        [PluginService]
        public static ISigScanner SigScanner { get; private set; }
        [PluginService]
        public static IDalamudPluginInterface Interface { get; private set; }
        [PluginService]
        public static IGameGui GameGui { get; private set; }
        [PluginService]
        public static IChatGui ChatGui { get; private set; }
        [PluginService]
        public static IToastGui ToastGui { get; private set; }
        [PluginService]
        public static IClientState ClientState { get; private set; }
        [PluginService]
        public static IDataManager Data { get; private set; }
        [PluginService]
        public static IGameInteropProvider Hook { get; private set; }
        [PluginService]
        public static IPluginLog Log { get; private set; }

        public static ICallGateSubscriber<object, object> IpcSubscriber;

        [StructLayout(LayoutKind.Sequential)]
        internal struct ActionEffect
        {
            internal readonly UInt32 AnimationTargetActor;
            internal readonly UInt32 Unknown1;
            internal readonly UInt32 ActionId;
            internal readonly UInt32 GlobalEffectCounter;
            internal readonly float AnimationLockDuration;
            internal readonly UInt32 UnknownTargetId;
            internal readonly Int16 SourceSequence;
        }

        public Plugin()
        {
            
            this.configuration = Interface.GetPluginConfig() as Configuration ?? new Configuration();

            InitPingPluginIPC();

            ActionEffectFunc = SigScanner.ScanText("44 0F B6 91 ?? ?? ?? ?? 4D 8B F9") - 0xC;
            Log.Info($"ActionEffectFunc:{ActionEffectFunc:X}");
            ActionRequestFunc = SigScanner.ScanText("E8 ?? ?? ?? ?? 41 3A C5 0F 85 ?? ?? ?? ?? ");
            Log.Info($"ActionRequestFunc:{ActionRequestFunc:X}");
            // AdjustActionIdFunc = this.pi.TargetModuleScanner.ScanText("8B DA BE ?? ?? ?? ??") - 0xF;
            // PluginLog.Log($"AdjustActionIdFunc:{AdjustActionIdFunc:X}");

            ActionEffectFuncHook = Hook.HookFromAddress<ActionEffectFuncDelegate>(
                ActionEffectFunc,
                new ActionEffectFuncDelegate(ActionEffectFuncDetour)
            );
            ActionRequestFuncHook = Hook.HookFromAddress<ActionRequestFuncDelegate>(
                ActionRequestFunc,
                new ActionRequestFuncDelegate(ActionRequestFuncDetour)
            );
            /*
            AdjustActionIdFuncHook = new Hook<AdjustActionIdFuncDelegate>(
                AdjustActionIdFunc,
                new AdjustActionIdFuncDelegate(AdjustActionIdFuncDetour)
            );
            */

            ActionEffectFuncHook.Enable();
            ActionRequestFuncHook.Enable();
            //AdjustActionIdFuncHook.Enable();
            /*
            CmdManager.AddHandler(commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Double weaving in high latency network."
            });
            */
        }

        private void InitPingPluginIPC()
        {
            Log.Info("Initializing IPC");
            IpcSubscriber = Interface.GetIpcSubscriber<object, object>("PingPlugin.Ipc");
            IpcSubscriber.Subscribe(UpdateRTTDetour);
        }

        private void UpdateRTTDetour(dynamic expando)
        {
            Log.Debug($"LastRTT:{expando.LastRTT} AverageRTT:{expando.AverageRTT}");
            RTT = (long)expando.AverageRTT;
            LastRTT = (long)expando.LastRTT;
        }

        /*
        private Int64 AdjustActionIdFuncDetour(Int64 a1, int a2)
        {
            Int64 result = this.AdjustActionIdFuncHook.Original(a1, a2);
            if(a2 != (int)result && a2 != 21)
                PluginLog.LogDebug($"AdjustActionId a1:{a1} a2:{a2} result:{result}");
            return result;
        }
        */


        private char ActionEffectFuncDetour(Int64 a1, int sourceActorId, Int16 a3, IntPtr a4, int size)
        {
            try
            {
                if (((size == 0x78) || (size == 0x278) ||
                    (size == 0x4B8) || (size == 0x6F8) || (size == 0x938)) && 
                    (ulong)sourceActorId == ClientState.LocalPlayer?.GameObjectId)
                {
                    var actionId = Marshal.ReadInt32(a4 + 8);
                    actionRequestTime.TryGetValue((uint)actionId, out Stopwatch stopwatch);
                    stopwatch?.Stop();
                    var actionEffect = (ActionEffect)Marshal.PtrToStructure(a4, typeof(ActionEffect));
                    if (actionEffect.SourceSequence > 0 && actionEffect.AnimationLockDuration > 0.5)
                    {
                        long elapsedTime = 0;
                        long laggingTime = 0;
                        if (actionRequestTime.ContainsKey(actionEffect.ActionId))
                        {
                            actionRequestTime.Remove(actionEffect.ActionId);
                            elapsedTime = Math.Max(stopwatch.ElapsedMilliseconds / 2, elapsedTime);
                            elapsedTime = Math.Max(elapsedTime - 50, 0);
                            laggingTime = Math.Min(Math.Min(elapsedTime, LastRTT), 300);
                        }
                        else
                        {
                            laggingTime = Math.Min(LastRTT, 300);
                        }
                        var serverAnimationLock = actionEffect.AnimationLockDuration * 1000;
                        float animationLock = Math.Max(serverAnimationLock - laggingTime, 300);
                        byte[] bytes = BitConverter.GetBytes(animationLock / 1000);
                        Marshal.Copy(bytes, 0, a4 + 16, bytes.Length);
                        string logLine = $"Status ActionId:{actionEffect.ActionId} Sequence:{actionEffect.SourceSequence} " +
                            $"Elapsed:{elapsedTime}ms RTT:{RTT}ms Lagging:{laggingTime}ms " +
                            $"AnimationLockDuration:{serverAnimationLock}ms -> {animationLock}ms";
                        Log.Debug(logLine);
                    }
                }
            }
            catch(Exception e)
            {
                Log.Debug($"Exception: {e}");
                Log.Debug("Don't crash the game");
            }
            var result = ActionEffectFuncHook.Original(a1, sourceActorId, a3, a4, size);
            return result;
        }

        private Int64 ActionRequestFuncDetour(Int64 a1, uint a2, uint a3, Int64 a4, IntPtr a5, uint a6)
        {
            Stopwatch stopwatch = new Stopwatch();
            try
            {
                string logLine = $"Request ActionId:{a3}";
                if (actionRequestTime.ContainsKey(a3))
                    actionRequestTime.Remove(a3);
                actionRequestTime.Add(a3, stopwatch);
                Log.Debug(logLine);
            }
            catch
            {
                Log.Info("Don't crash the game");
            }
            var result = ActionRequestFuncHook.Original(a1, a2, a3, a4, a5, a6);
            stopwatch.Start();
            return result;
        }

        public void Dispose()
        {
            // this.ui.Dispose();
            // this.pi.Unsubscribe("PingPlugin");
            ActionEffectFuncHook.Dispose();
            ActionRequestFuncHook.Dispose();
            // AdjustActionIdFuncHook.Dispose();
            // CmdManager.RemoveHandler(commandName);
        }

        private void OnCommand(string command, string args)
        {
            // in response to the slash command, just display our main ui
            //this.ui.Visible = true;
        }

        private void DrawUI()
        {
            this.ui.Draw();
        }

        private void DrawConfigUI()
        {
            this.ui.SettingsVisible = true;
        }
    }
}
