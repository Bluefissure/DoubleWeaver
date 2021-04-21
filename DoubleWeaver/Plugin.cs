using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Hooking;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;

namespace DoubleWeaver
{
    public class Plugin : IDalamudPlugin
    {
        public string Name => "Double Weaver";

        private const string commandName = "/doublewaver";

        private Dictionary<uint, Stopwatch> actionRequestTime = new Dictionary<uint, Stopwatch> { };
        private long RTT = 300;

        private DalamudPluginInterface pi;
        private Configuration configuration;
        private PluginUI ui;

        public IntPtr ActionEffectFunc;
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate char ActionEffectFuncDelegate(Int64 a1, int a2, Int16 a3, IntPtr a4, int size);
        private Hook<ActionEffectFuncDelegate> ActionEffectFuncHook;

        public IntPtr ActionRequestFunc;
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate Int64 ActionRequestFuncDelegate(Int64 a1, uint a2, uint a3);
        private Hook<ActionRequestFuncDelegate> ActionRequestFuncHook;

        private delegate void UpdateRTTDelegate(ExpandoObject expando);

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

        public void Initialize(DalamudPluginInterface pluginInterface)
        {
            this.pi = pluginInterface;
            
            this.configuration = this.pi.GetPluginConfig() as Configuration ?? new Configuration();
            this.configuration.Initialize(this.pi);

            this.pi.Subscribe("PingPlugin", UpdateRTTDetour);


            ActionEffectFunc = this.pi.TargetModuleScanner.ScanText("4D 8B F9 0F B6 91 ?? ?? ?? ??") - 0xF;
            PluginLog.Log($"ActionEffectFunc:{ActionEffectFunc:X}");
            ActionRequestFunc = this.pi.TargetModuleScanner.ScanText("E8 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? FF 50 18");
            PluginLog.Log($"ActionRequestFunc:{ActionRequestFunc:X}");

            ActionEffectFuncHook = new Hook<ActionEffectFuncDelegate>(
                ActionEffectFunc,
                new ActionEffectFuncDelegate(ActionEffectFuncDetour)
            );
            ActionRequestFuncHook = new Hook<ActionRequestFuncDelegate>(
                ActionRequestFunc,
                new ActionRequestFuncDelegate(ActionRequestFuncDetour)
            );

            ActionEffectFuncHook.Enable();
            ActionRequestFuncHook.Enable();

            this.pi.CommandManager.AddHandler(commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "A useful message to display in /xlhelp"
            });
        }

        private void UpdateRTTDetour(dynamic expando)
        {
            PluginLog.LogDebug($"LastRTT:{expando.LastRTT} AverageRTT:{expando.AverageRTT}");
            RTT = (long)expando.AverageRTT;
        }


        private char ActionEffectFuncDetour(Int64 a1, int a2, Int16 a3, IntPtr a4, int size)
        {
            try
            {
                if ((size == 0x78) || (size == 0x278) ||
                    (size == 0x4B8) || (size == 0x6F8) || (size == 0x938))
                {
                    var actionId = Marshal.ReadInt32(a4 + 8);
                    actionRequestTime.TryGetValue((uint)actionId, out Stopwatch stopwatch);
                    stopwatch?.Stop();
                    var actionEffect = (ActionEffect)Marshal.PtrToStructure(a4, typeof(ActionEffect));
                    if (actionEffect.SourceSequence > 0 && actionRequestTime.ContainsKey(actionEffect.ActionId))
                    {
                        actionRequestTime.Remove(actionEffect.ActionId);
                        var serverAnimationLock = actionEffect.AnimationLockDuration * 1000;
                        var elapsedTime = Math.Max(stopwatch.ElapsedMilliseconds - 75, 0);
                        string logLine = $"Status ActionId:{actionEffect.ActionId} Sequence:{actionEffect.SourceSequence} " +
                            $"Elapsed:{elapsedTime}ms RTT:{RTT}ms AnimationLockDuration:{serverAnimationLock}ms ";
                        if (serverAnimationLock > 500)
                        {
                            float defaultAnimationLock = Math.Max(serverAnimationLock - Math.Min(elapsedTime, RTT), 500);
                            byte[] bytes = BitConverter.GetBytes(defaultAnimationLock / 1000);
                            Marshal.Copy(bytes, 0, a4 + 16, bytes.Length);
                            logLine += $"-> {defaultAnimationLock}ms";
                        }
                        PluginLog.Log(logLine);
                    }
                }
            }
            catch
            {
                PluginLog.Log("Don't crash the game");
            }
            var result = ActionEffectFuncHook.Original(a1, a2, a3, a4, size);
            return result;
        }

        private Int64 ActionRequestFuncDetour(Int64 a1, uint a2, uint a3)
        {
            Stopwatch stopwatch = new Stopwatch();
            try
            {
                string logLine = $"Request ActionId:{a3}";
                if (actionRequestTime.ContainsKey(a3))
                    actionRequestTime.Remove(a3);
                actionRequestTime.Add(a3, stopwatch);
                PluginLog.Log(logLine);
            }
            catch
            {
                PluginLog.Log("Don't crash the game");
            }
            var result = ActionRequestFuncHook.Original(a1, a2, a3);
            stopwatch.Start();
            return result;
        }

        public void Dispose()
        {
            //this.ui.Dispose();
            this.pi.Unsubscribe("PingPlugin");
            ActionEffectFuncHook.Dispose();
            ActionRequestFuncHook.Dispose();
            this.pi.CommandManager.RemoveHandler(commandName);
            this.pi.Dispose();
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
