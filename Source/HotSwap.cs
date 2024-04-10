using dnlib.DotNet;
using HarmonyLib;
using LudeonTK;
using MonoMod.Core;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Verse;

namespace HotSwap
{
    [HotSwappable]
    [StaticConstructorOnStartup]
    static class HotSwapMain
    {
        [TweakValue("HotSwap")]
        public static bool LogReloadedMethods = false;
        public static bool EnableAutoReload = true;
        public static int runInFrames;
        public static Dictionary<Assembly, FileInfo> AssemblyFiles;
        public static KeyBindingDef HotSwapKey = KeyBindingDef.Named("HotSwapKey");
        public static HashSet<string> HotSwapNames = new()
        {
            "hotswap", "hotswapattribute",
            "hotswappable", "hotswappableattribute",
        };
        public static HashSet<string> HotSwapAllNames = new()
        {
            "hotswapall", "hotswapallattribute",
            "hotswappableall", "hotswappableallattribute",
        };
        public static HashSet<string> IgnoreNames = new()
        {
            "ignorehotswap", "ignorehotswapattribute",
        };
        public static HashSet<string> AssembliesToReloadAllMethods = new();

        // A cache used so the methods don't get GC'd
        private static readonly Dictionary<MethodBase, ICoreDetour> dynMethods = new();
        private static readonly Harmony harmony = new("HotSwap");
        private static readonly Dictionary<string, FileSystemWatcher> dirToWatcher = new();
        private static int count;
        private static readonly DateTime startTime = DateTime.Now;

        static HotSwapMain()
        {
            harmony.PatchAll();
            AssemblyFiles = MapModAssemblies();
        }

        static Dictionary<Assembly, FileInfo> MapModAssemblies()
        {
            var dict = new Dictionary<Assembly, FileInfo>();

            foreach (var mod in LoadedModManager.RunningMods)
            {
                // Ignore mods that are not in the /Mods folder.
                if (new DirectoryInfo(mod.RootDir).Parent.Name != "Mods")
                    continue;

                foreach (FileInfo fileInfo in ModContentPack.GetAllFilesForModPreserveOrder(mod, "Assemblies/", (string e) => e.ToLower() == ".dll", null).Select(t => t.Item2))
                {
                    var fileAsmName = AssemblyName.GetAssemblyName(fileInfo.FullName).FullName;
                    var found = mod.assemblies.loadedAssemblies.Find(a => a.GetName().FullName == fileAsmName);
                    if (found == null || !dict.TryAdd(found, fileInfo))
                        continue;
                    
                    // Set up file system watcher, if we don't already have one.
                    string watchDir = fileInfo.Directory.FullName;
                    if (!dirToWatcher.ContainsKey(watchDir))
                    {
                        var watcher = new FileSystemWatcher(watchDir, "*.dll");
                        watcher.NotifyFilter = NotifyFilters.Attributes
                                                | NotifyFilters.CreationTime
                                                | NotifyFilters.LastWrite
                                                | NotifyFilters.Size;

                        watcher.Changed += OnDllChange;

                        dirToWatcher.Add(watchDir, watcher);
                        watcher.IncludeSubdirectories = true;
                        watcher.EnableRaisingEvents = true;
                    }

                    // Check for 'HotSwapAll' attribute.
                    foreach (var type in found.GetTypes())
                    {
                        if (type.CustomAttributes.Any(a => HotSwapAllNames.Contains(a.AttributeType.Name.ToLowerInvariant())))
                        {
                            AssembliesToReloadAllMethods.Add(fileInfo.FullName);
                            break;
                        }
                    }                    
                }
            }

            Info($"HotSwap mapped {dict.Count} assemblies to their .dll files.");
            return dict;
        }

        private static void OnDllChange(object sender, FileSystemEventArgs e)
        {
            if (!EnableAutoReload)
                return;

            var file = new FileInfo(e.FullPath);

            if (!file.Exists)
                return;

            bool hotSwapAllMethods = AssembliesToReloadAllMethods.Contains(file.FullName);
            HotSwap(file, hotSwapAllMethods);
        }

        public static void ScheduleHotSwap()
        {
            runInFrames = 2;
            Messages.Message("Hotswapping...", MessageTypeDefOf.SilentInput);
        }

        public static void HotSwapAll()
        {
            Info("Hotswapping...");

            foreach (var kv in AssemblyFiles)
            {
                kv.Value.Refresh();
                if (kv.Value.LastWriteTime < startTime)
                    continue;

                bool hotSwapAllMethods = AssembliesToReloadAllMethods.Contains(kv.Value.FullName);
                HotSwap(kv.Value, hotSwapAllMethods);
            }

            Info("Hotswapping done.");
        }

        public static void HotSwap(FileInfo file, bool allMethods)
        {
            using var dnModule = ModuleDefMD.Load(file.FullName);
            int methodCount = 0;

            foreach (var dnType in dnModule.GetTypes())
            {
                if (!allMethods)
                {
                    if (!dnType.HasCustomAttributes)
                        continue;

                    if (!dnType.CustomAttributes.Any(a => HotSwapNames.Contains(a.AttributeType.Name.ToLowerInvariant())))
                        continue;
                }

                if (dnType.CustomAttributes.Any(a => IgnoreNames.Contains(a.AttributeType.Name.ToLowerInvariant())))
                    continue;

                const BindingFlags ALL_DECLARED = BindingFlags.DeclaredOnly | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

                var typeWithAttr = Type.GetType(dnType.AssemblyQualifiedName);
                var types = typeWithAttr.GetNestedTypes(ALL_DECLARED).Where(IsCompilerGenerated).Concat(typeWithAttr);
                var typesKv = types.Select(t => new KeyValuePair<Type, TypeDef>(t, dnModule.FindReflection(t.FullName))).Where(t => t.Key != null && t.Value != null);

                foreach (var typePair in typesKv)
                {
                    if (typePair.Key.IsGenericTypeDefinition)
                        continue;

                    foreach (var method in typePair.Key.GetMethods(ALL_DECLARED).Concat(typePair.key.GetConstructors(ALL_DECLARED).Cast<MethodBase>()))
                    {
                        if (method.GetMethodBody() == null)
                            continue;
                        if (method.IsGenericMethodDefinition)
                            continue;

                        if (LogReloadedMethods)
                            Info($"Reloading {dnType.Namespace}.{dnType.Name}.{method.Name}");

                        byte[] code = method.GetMethodBody().GetILAsByteArray();
                        var dnMethod = typePair.Value.Methods.FirstOrDefault(m => Translator.MethodSigMatch(method, m));

                        if (dnMethod == null) continue;

                        var methodBody = dnMethod.Body;
                        byte[] newCode = MethodSerializer.SerializeInstructions(methodBody);

                        if (code.AsReadOnlySpan().SequenceEqual(newCode))
                            continue;

                        try
                        {
                            var replacement = OldHarmony.CreateDynamicMethod(method, $"_HotSwap{count++}");
                            var ilGen = replacement.GetILGenerator();

                            MethodTranslator.TranslateLocals(methodBody, ilGen);
                            MethodTranslator.TranslateRefs(methodBody, newCode, replacement);

                            ilGen.code = newCode;
                            ilGen.code_len = newCode.Length;
                            ilGen.max_stack = methodBody.MaxStack;

                            MethodTranslator.TranslateExceptions(methodBody, ilGen);

                            OldHarmony.PrepareDynamicMethod(replacement);

                            DetourMethod(method, replacement);

                            methodCount++;
                        }
                        catch (Exception e)
                        {
                            Error($"Patching {method.FullDescription()} failed with {e}");
                        }
                    }
                }
            }

            Info($"Reloaded {methodCount} methods in {dnModule.Name} (from {file.Name}).");
            if (methodCount > 0)
                Messages.Message($"Reloaded {methodCount} methods in {dnModule.Name} (from {file.Name}).", MessageTypeDefOf.NeutralEvent, false);
        }

        internal static void DetourMethod(MethodBase method, MethodBase replacement)
        {
            if (dynMethods.TryGetValue(method, out var detour))
                detour.Dispose();

            dynMethods[method] = DetourFactory.Current.CreateDetour(method, replacement);            
        }

        static void Info(string str) => Log.Message($"<color=magenta>[HotSwap]</color> {str}");
        static void Error(string str) => Log.Error($"<color=magenta>[HotSwap]</color> {str}");

        public static bool IsCompilerGenerated(Type type)
        {
            while (type != null)
            {
                if (type.HasAttribute<CompilerGeneratedAttribute>()) return true;
                type = type.DeclaringType;
            }

            return false;
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class HotSwappableAttribute : Attribute
    {
    }
}
