using dnlib.DotNet;
using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Verse;

namespace HotSwap
{
    [HotSwappable]
    [StaticConstructorOnStartup]
    static class HotSwapMain
    {
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
        public static HashSet<string> AssembliesToReloadAllMethods = new();

        // A cache used so the methods don't get GC'd
        private static readonly Dictionary<MethodBase, DynamicMethod> dynMethods = new();
        private static readonly Harmony harmony = new("HotSwap");
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
                    if (found != null && !dict.ContainsKey(found))
                    {
                        dict[found] = fileInfo;
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
            }

            Info($"HotSwap mapped {dict.Count} assemblies to their .dll files.");
            return dict;
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

            Info($"Hotswapping done.");
        }

        public static void HotSwap(FileInfo file, bool allMethods)
        {
            using var dnModule = ModuleDefMD.Load(file.FullName);

            foreach (var dnType in dnModule.GetTypes())
            {
                if (!allMethods)
                {
                    if (!dnType.HasCustomAttributes)
                        continue;

                    if (!dnType.CustomAttributes.Any(a => HotSwapNames.Contains(a.AttributeType.Name.ToLowerInvariant())))
                        continue;
                }               

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

                        byte[] code = method.GetMethodBody().GetILAsByteArray();
                        var dnMethod = typePair.Value.Methods.FirstOrDefault(m => Translator.MethodSigMatch(method, m));

                        if (dnMethod == null) continue;

                        var methodBody = dnMethod.Body;
                        byte[] newCode = MethodSerializer.SerializeInstructions(methodBody);

                        if (ByteArrayCompare(code, newCode))
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
                            Memory.DetourMethod(method, replacement);

                            dynMethods[method] = replacement;
                        }
                        catch (Exception e)
                        {
                            Error($"Patching {method.FullDescription()} failed with {e}");
                        }
                    }
                }
            }
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

        static bool ByteArrayCompare(ReadOnlySpan<byte> a1, ReadOnlySpan<byte> a2)
        {
            return a1.SequenceEqual(a2);
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class HotSwappableAttribute : Attribute
    {
    }

}
