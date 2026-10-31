// Carryalls | Intercontinental Transport (fork) - RimWorld 1.7 compatibility shim
//
// 1) Crash fix (see 修复说明.md)
//    - SRTS.StartUp's [StaticConstructorOnStartup] runs Harmony.PatchAll(). One patch class
//      carries a hand written 1.6 transpiler for ExpandableWorldObjectsOnGUI that cannot be
//      applied to the 1.7 IL, so PatchAll threw inside the static constructor and left the
//      type permanently faulted. Every transport gizmo request then threw
//      TypeInitializationException and RimWorld dropped the whole gizmo panel.
//    - Layers: neutralise that transpiler, guard the static constructor with a finalizer,
//      and guard CompTransporter.CompGetGizmosExtra with a finalizer.
//
// 2) Small runtime optimisations (behaviour preserving)
//    - NoLaunchGroupForSRTS: skip the call entirely for transports that have no
//      CompProperties_LaunchableSRTS (cached per ThingDef) instead of running the whole
//      method and the generic comp lookup for every vanilla transporter gizmo request.
//    - SRTSHelper.ContainedInDefProjects: O(1) hash set instead of a linear scan over
//      srtsDefProjects (called per research project per frame by the research tab).
//
// 3) 1.7 patch audit
//    - After loading, compare the Harmony patch methods of the mod assembly with the patches
//      that actually reached a target method, and log which ones are dead on 1.7. This is the
//      diagnostic that tells you which features of the 1.6 build silently did not apply.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace CarryallsForkFix
{
    public static class ForkFix
    {
        private const string LogPrefix = "[Carryalls fork] ";
        private const string ModAssemblyName = "NephsCarryalls";

        private static bool installed;
        private static bool gizmoGuardReported;

        private static FieldInfo srtsDefProjectsField;
        private static Dictionary<ResearchProjectDef, bool> srtsResearchSet;
        private static bool researchCacheDirty = true;

        private static readonly Dictionary<ThingDef, bool> srtsDefCache = new Dictionary<ThingDef, bool>();

        public static void Install()
        {
            if (installed)
            {
                return;
            }

            installed = true;

            Harmony harmony;
            try
            {
                harmony = new Harmony("mlie.carryalls.fork.rw17fix");
            }
            catch (Exception exception)
            {
                Log.Error(LogPrefix + "could not create the Harmony instance: " + exception);
                return;
            }

            bool gizmoGuard = TryPatch(harmony, "transporter gizmo guard", delegate
            {
                MethodInfo gizmoMethod = AccessTools.Method(typeof(CompTransporter), "CompGetGizmosExtra", new Type[0]) ??
                                         AccessTools.Method(typeof(CompTransporter), "CompGetGizmosExtra");
                if (gizmoMethod == null)
                {
                    return false;
                }

                harmony.Patch(gizmoMethod, null, null, null,
                    new HarmonyMethod(typeof(ForkFix).GetMethod("TransportGizmoFinalizer", BindingFlags.Static | BindingFlags.Public)));
                return true;
            });

            Type startupType = FindType("SRTS.StartUp");
            if (startupType == null)
            {
                Log.Warning(LogPrefix + "SRTS.StartUp was not found, only the gizmo guard is active.");
                ScheduleAudit();
                return;
            }

            bool skippedTranspiler = TryPatch(harmony, "map icon transpiler bypass", delegate
            {
                Type guiPatchType = FindType("SRTS.Patches.ExpandableWorldObjectsUtility_ExpandableWorldObjectsOnGUI_Patch");
                if (guiPatchType == null)
                {
                    return false;
                }

                MethodInfo transpiler = AccessTools.Method(guiPatchType, "Transpiler");
                if (transpiler == null)
                {
                    return false;
                }

                harmony.Patch(transpiler, new HarmonyMethod(typeof(ForkFix).GetMethod("SkipTranspiler",
                    BindingFlags.Static | BindingFlags.Public)));
                return true;
            });

            bool guardedCctor = TryPatch(harmony, "start-up guard", delegate
            {
                ConstructorInfo startupCctor = startupType.TypeInitializer;
                if (startupCctor == null)
                {
                    return false;
                }

                harmony.Patch(startupCctor, null, null, null,
                    new HarmonyMethod(typeof(ForkFix).GetMethod("StartUpCctorFinalizer",
                        BindingFlags.Static | BindingFlags.Public)));
                return true;
            });

            bool fastGizmoFilter = TryPatch(harmony, "gizmo filter fast path", delegate
            {
                MethodInfo noLaunchGroup = AccessTools.Method(startupType, "NoLaunchGroupForSRTS");
                if (noLaunchGroup == null)
                {
                    return false;
                }

                harmony.Patch(noLaunchGroup, new HarmonyMethod(typeof(ForkFix).GetMethod("NoLaunchGroupPrefix",
                    BindingFlags.Static | BindingFlags.Public)));
                return true;
            });

            bool fastResearchLookup = TryPatch(harmony, "research lookup cache", delegate
            {
                Type helperType = FindType("SRTS.SRTSHelper");
                if (helperType == null)
                {
                    return false;
                }

                MethodInfo contained = AccessTools.Method(helperType, "ContainedInDefProjects",
                    new[] { typeof(ResearchProjectDef) });
                MethodInfo populate = AccessTools.Method(helperType, "PopulateDictionary", new Type[0]);
                if (contained == null)
                {
                    return false;
                }

                harmony.Patch(contained, new HarmonyMethod(typeof(ForkFix).GetMethod("ResearchContainedPrefix",
                    BindingFlags.Static | BindingFlags.Public)));

                if (populate != null)
                {
                    harmony.Patch(populate, null,
                        new HarmonyMethod(typeof(ForkFix).GetMethod("PopulateDictionaryPostfix",
                            BindingFlags.Static | BindingFlags.Public)));
                }

                return true;
            });

            Log.Message(LogPrefix + "1.7 compatibility shim active (icon transpiler neutralised: " + skippedTranspiler +
                        ", start-up guarded: " + guardedCctor + ", gizmo guard: " + gizmoGuard +
                        ", fast gizmo filter: " + fastGizmoFilter + ", research cache: " + fastResearchLookup + ").");

            ScheduleAudit();
        }

        private static bool TryPatch(Harmony harmony, string what, Func<bool> action)
        {
            try
            {
                return action();
            }
            catch (Exception exception)
            {
                Log.Error(LogPrefix + "could not install the " + what + ": " + exception);
                return false;
            }
        }

        private static Type FindType(string fullName)
        {
            Type type = AccessTools.TypeByName(fullName);
            if (type != null)
            {
                return type;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    type = assemblies[i].GetType(fullName, false);
                }
                catch (Exception)
                {
                    type = null;
                }

                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        // ---------------------------------------------------------------- patched methods

        /// <summary>
        ///     Prefix on the 1.6 world icon transpiler: hand the original instruction list back,
        ///     so Harmony applies a no-op transpiler instead of invalid IL.
        /// </summary>
        public static bool SkipTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilg,
            ref IEnumerable<CodeInstruction> __result)
        {
            __result = instructions;
            return false;
        }

        /// <summary>
        ///     Finalizer on the SRTS.StartUp static constructor: swallow a patch failure so the type
        ///     still counts as initialised and the gizmo panel keeps working.
        /// </summary>
        public static Exception StartUpCctorFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                Log.Warning(LogPrefix + "SRTS.StartUp could not apply every patch (" +
                            __exception.GetType().Name + ": " + __exception.Message +
                            "). The exception was suppressed so the mod stays usable on 1.7.");
            }

            return null;
        }

        /// <summary>
        ///     Finalizer on CompTransporter.CompGetGizmosExtra: one bad gizmo must not remove every
        ///     button of the selected transport.
        /// </summary>
        public static Exception TransportGizmoFinalizer(Exception __exception, ref IEnumerable<Gizmo> __result)
        {
            if (__exception == null)
            {
                return null;
            }

            if (!gizmoGuardReported)
            {
                gizmoGuardReported = true;
                Log.Warning(LogPrefix +
                            "a transporter gizmo provider threw an exception, the remaining buttons stay usable (" +
                            __exception.GetType().Name + ": " + __exception.Message + ").");
            }

            if (__result == null)
            {
                __result = new List<Gizmo>();
            }

            return null;
        }

        /// <summary>
        ///     Fast path for StartUp.NoLaunchGroupForSRTS. The method only ever removes vanilla
        ///     "select previous/next/all transporter" buttons from SRTS craft, so for every other
        ///     transporter we can skip it without touching the result.
        /// </summary>
        public static bool NoLaunchGroupPrefix(CompTransporter __instance, ref IEnumerable<Gizmo> __result)
        {
            if (__instance == null || __instance.parent == null)
            {
                return false;
            }

            return IsSrtsCraft(__instance.parent.def);
        }

        private static bool IsSrtsCraft(ThingDef def)
        {
            if (def == null)
            {
                return false;
            }

            bool result;
            if (srtsDefCache.TryGetValue(def, out result))
            {
                return result;
            }

            result = false;
            List<CompProperties> comps = def.comps;
            if (comps != null)
            {
                for (int i = 0; i < comps.Count; i++)
                {
                    CompProperties props = comps[i];
                    if (props != null && props.GetType().Name == "CompProperties_LaunchableSRTS")
                    {
                        result = true;
                        break;
                    }
                }
            }

            srtsDefCache[def] = result;
            return result;
        }

        /// <summary>
        ///     O(1) replacement for SRTSHelper.ContainedInDefProjects, which is a linear scan of
        ///     SRTSHelper.srtsDefProjects and is called for every research node, every frame.
        /// </summary>
        public static bool ResearchContainedPrefix(ResearchProjectDef project, ref bool __result)
        {
            EnsureResearchCache();
            if (srtsResearchSet == null)
            {
                return true;
            }

            __result = project != null && srtsResearchSet.ContainsKey(project);
            return false;
        }

        /// <summary>Marks the research cache stale whenever the mod rebuilds its dictionary.</summary>
        public static void PopulateDictionaryPostfix()
        {
            researchCacheDirty = true;
        }

        private static void EnsureResearchCache()
        {
            if (!researchCacheDirty && srtsResearchSet != null)
            {
                return;
            }

            researchCacheDirty = false;
            srtsResearchSet = new Dictionary<ResearchProjectDef, bool>();

            try
            {
                Type helperType = FindType("SRTS.SRTSHelper");
                if (helperType == null)
                {
                    return;
                }

                if (srtsDefProjectsField == null)
                {
                    srtsDefProjectsField = AccessTools.Field(helperType, "srtsDefProjects");
                }

                if (srtsDefProjectsField == null)
                {
                    return;
                }

                IDictionary dictionary = srtsDefProjectsField.GetValue(null) as IDictionary;
                if (dictionary == null)
                {
                    return;
                }

                foreach (DictionaryEntry entry in dictionary)
                {
                    ResearchProjectDef project = entry.Value as ResearchProjectDef;
                    if (project != null)
                    {
                        srtsResearchSet[project] = true;
                    }
                }
            }
            catch (Exception exception)
            {
                Log.Error(LogPrefix + "could not build the research lookup cache: " + exception);
            }
        }

        // ---------------------------------------------------------------- 1.7 patch audit

        private static void ScheduleAudit()
        {
            try
            {
                LongEventHandler.ExecuteWhenFinished(AuditPatches);
            }
            catch (Exception exception)
            {
                Log.Error(LogPrefix + "could not schedule the patch audit: " + exception);
            }
        }

        /// <summary>
        ///     Every HarmonyPatch class of the mod assembly names a target; on a 1.6 mod running on
        ///     1.7 some of those targets may no longer exist (renamed or moved). Those patch classes
        ///     fail at patch time and their feature silently disappears, so log the list.
        /// </summary>
        public static void AuditPatches()
        {
            try
            {
                Assembly modAssembly = null;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    if (assemblies[i].GetName().Name == ModAssemblyName)
                    {
                        modAssembly = assemblies[i];
                        break;
                    }
                }

                if (modAssembly == null)
                {
                    Log.Warning(LogPrefix + "patch audit skipped: " + ModAssemblyName + " is not loaded.");
                    return;
                }

                int total = 0;
                List<string> unresolved = new List<string>();
                Type[] types = modAssembly.GetTypes();
                for (int i = 0; i < types.Length; i++)
                {
                    Type type = types[i];
                    object[] attributes = type.GetCustomAttributes(typeof(HarmonyPatch), true);
                    if (attributes.Length == 0)
                    {
                        continue;
                    }

                    HarmonyPatch attribute = attributes[0] as HarmonyPatch;
                    HarmonyMethod info = attribute == null ? null : attribute.info;
                    if (info == null || info.declaringType == null || string.IsNullOrEmpty(info.methodName))
                    {
                        continue;
                    }

                    total++;
                    if (ResolveTarget(info) == null)
                    {
                        unresolved.Add(type.Name + " -> " + info.declaringType.Name + "." + info.methodName);
                    }
                }

                if (unresolved.Count == 0)
                {
                    Log.Message(LogPrefix + "patch audit: all " + total +
                                " SRTS patch targets still exist on this game version.");
                }
                else
                {
                    Log.Warning(LogPrefix + "patch audit: " + unresolved.Count + " of " + total +
                                " SRTS patch target(s) do not exist on this game version, the related features are dead: " +
                                string.Join(", ", unresolved.ToArray()));
                }
            }
            catch (Exception exception)
            {
                Log.Error(LogPrefix + "patch audit failed: " + exception);
            }
        }

        private static MethodBase ResolveTarget(HarmonyMethod info)
        {
            try
            {
                switch (info.methodType)
                {
                    case MethodType.Getter:
                        return AccessTools.PropertyGetter(info.declaringType, info.methodName);
                    case MethodType.Setter:
                        return AccessTools.PropertySetter(info.declaringType, info.methodName);
                    case MethodType.Constructor:
                        return AccessTools.Constructor(info.declaringType, info.argumentTypes);
                    case MethodType.StaticConstructor:
                        return info.declaringType.TypeInitializer;
                    default:
                        return AccessTools.Method(info.declaringType, info.methodName);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    ///     Runs while the active mods are instantiated, which is before any
    ///     [StaticConstructorOnStartup] type - and therefore before SRTS.StartUp - is touched.
    /// </summary>
    public class CarryallsForkMod : Mod
    {
        public CarryallsForkMod(ModContentPack content) : base(content)
        {
            ForkFix.Install();
        }
    }

    /// <summary>
    ///     Fallback in case the Mod class is not instantiated for some reason.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class CarryallsForkStartup
    {
        static CarryallsForkStartup()
        {
            ForkFix.Install();
        }
    }
}
