using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using MonoMod.Cil;
using MonoMod.Utils;

namespace EpicLootProgressionFix
{
    // Optional integrations resolve and compile their entire patch set before any detour is installed.
    // Core callers keep their existing immediate Harmony behavior through the implicit adapter.
    internal sealed class IntegrationPatches
    {
        private sealed class Edit
        {
            internal MethodBase Original;
            internal HarmonyMethod Prefix, Postfix, Transpiler, Finalizer, ILManipulator;
            internal MethodInfo Remove;
        }
        private readonly Harmony harmony;
        private readonly bool immediate;
        private bool installed;
        private readonly List<Edit> edits = new List<Edit>();
        private readonly Dictionary<MethodBase, PatchInfo> previous = new Dictionary<MethodBase, PatchInfo>();
        internal readonly List<Action> Changes = new List<Action>(), Undo = new List<Action>(), Cleanup = new List<Action>();
        internal IntegrationPatches(string id) => harmony = new Harmony(id);
        private IntegrationPatches(Harmony instance) { harmony = instance; immediate = true; }
        public static implicit operator IntegrationPatches(Harmony instance) => new IntegrationPatches(instance);
        internal string Id => harmony.Id;

        internal void Patch(MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null, HarmonyMethod finalizer = null, HarmonyMethod ilmanipulator = null)
        {
            if (original == null) throw new MissingMethodException("Integration patch target was not found.");
            foreach (var patch in new[] { prefix, postfix, transpiler, finalizer, ilmanipulator })
                if (patch != null && patch.method == null) throw new MissingMethodException("Integration patch handler was not found.");
            if (immediate) harmony.Patch(original, prefix, postfix, transpiler, finalizer, ilmanipulator);
            else edits.Add(new Edit { Original = original, Prefix = prefix, Postfix = postfix, Transpiler = transpiler, Finalizer = finalizer, ILManipulator = ilmanipulator });
        }

        internal void Unpatch(MethodBase original, MethodInfo patch)
        {
            if (original == null || patch == null) throw new MissingMethodException("Integration patch removal target was not found.");
            if (immediate) harmony.Unpatch(original, patch);
            else edits.Add(new Edit { Original = original, Remove = patch });
        }

        internal void AddClass(Type type)
        {
            var prepare = AccessTools.DeclaredMethod(type, "Prepare", Type.EmptyTypes);
            if (prepare != null && !(bool)prepare.Invoke(null, null)) return;
            var processor = harmony.CreateClassProcessor(type);
            var originals = (List<MethodBase>)AccessTools.Method(typeof(PatchClassProcessor), "GetBulkMethods").Invoke(processor, null);
            var patches = (IEnumerable)AccessTools.Field(typeof(PatchClassProcessor), "patchMethods").GetValue(processor);
            if (originals.Count == 0 && (AccessTools.DeclaredMethod(type, "TargetMethods") != null || AccessTools.DeclaredMethod(type, "TargetMethod") != null))
                throw new MissingMethodException(type.FullName + " found no method to patch.");
            var tools = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchTools", true);
            var resolve = AccessTools.Method(tools, "GetOriginalMethod");
            foreach (var patch in patches)
            {
                var info = (HarmonyMethod)AccessTools.Field(patch.GetType(), "info").GetValue(patch);
                var kind = (HarmonyPatchType)AccessTools.Field(patch.GetType(), "type").GetValue(patch);
                var targets = originals.Count > 0 ? originals : new List<MethodBase> { (MethodBase)resolve.Invoke(null, new object[] { info }) };
                foreach (var target in targets)
                    switch (kind)
                    {
                        case HarmonyPatchType.Prefix: Patch(target, prefix: info); break;
                        case HarmonyPatchType.Postfix: Patch(target, postfix: info); break;
                        case HarmonyPatchType.Transpiler: Patch(target, transpiler: info); break;
                        case HarmonyPatchType.Finalizer: Patch(target, finalizer: info); break;
                        case HarmonyPatchType.ILManipulator: Patch(target, ilmanipulator: info); break;
                        default: throw new NotSupportedException("Unsupported optional integration patch kind: " + kind);
                    }
            }
        }

        private static PatchInfo Copy(PatchInfo source) => source == null ? new PatchInfo() : new PatchInfo
        {
            prefixes = source.prefixes.ToArray(), postfixes = source.postfixes.ToArray(),
            transpilers = source.transpilers.ToArray(), finalizers = source.finalizers.ToArray(), ilmanipulators = source.ilmanipulators.ToArray()
        };

        private Patch[] Append(Patch[] current, HarmonyMethod method) => method == null ? current :
            current.Concat(new[] { new Patch(method, current.Length, Id) }).ToArray();

        internal void Apply()
        {
            var targets = edits.Select(edit => edit.Original).Distinct().ToArray();
            foreach (var target in targets)
            {
                var snapshot = Copy(PatchManager.GetPatchInfo(target));
                previous.Add(target, snapshot);
                var planned = Copy(snapshot);
                foreach (var edit in edits.Where(edit => edit.Original == target))
                {
                    if (edit.Remove != null)
                    {
                        planned.prefixes = planned.prefixes.Where(patch => patch.PatchMethod != edit.Remove).ToArray();
                        planned.postfixes = planned.postfixes.Where(patch => patch.PatchMethod != edit.Remove).ToArray();
                        planned.transpilers = planned.transpilers.Where(patch => patch.PatchMethod != edit.Remove).ToArray();
                        planned.finalizers = planned.finalizers.Where(patch => patch.PatchMethod != edit.Remove).ToArray();
                        planned.ilmanipulators = planned.ilmanipulators.Where(patch => patch.PatchMethod != edit.Remove).ToArray();
                    }
                    else
                    {
                        planned.prefixes = Append(planned.prefixes, edit.Prefix);
                        planned.postfixes = Append(planned.postfixes, edit.Postfix);
                        planned.transpilers = Append(planned.transpilers, edit.Transpiler);
                        planned.finalizers = Append(planned.finalizers, edit.Finalizer);
                        planned.ilmanipulators = Append(planned.ilmanipulators, edit.ILManipulator);
                    }
                }
                using (var definition = new DynamicMethodDefinition(target))
                using (var context = new ILContext(definition.Definition))
                {
                    HarmonyManipulator.Manipulate(target, planned, context);
                    definition.Generate();
                }
            }
            // Reflection, hook signatures and transpiler patterns have all succeeded without changing the originals.
            installed = edits.Count > 0;
            foreach (var edit in edits)
                if (edit.Remove != null) harmony.Unpatch(edit.Original, edit.Remove);
                else harmony.Patch(edit.Original, edit.Prefix, edit.Postfix, edit.Transpiler, edit.Finalizer, edit.ILManipulator);
            foreach (var change in Changes) change();
        }

        internal void Rollback()
        {
            var failures = new List<Exception>();
            foreach (var pair in installed ? previous.Reverse() : Enumerable.Empty<KeyValuePair<MethodBase, PatchInfo>>())
                try
                {
                    // Restore exact patch indices and ordering, including patches belonging to other mods.
                    var current = PatchManager.ToPatchInfo(pair.Key);
                    current.prefixes = pair.Value.prefixes; current.postfixes = pair.Value.postfixes;
                    current.transpilers = pair.Value.transpilers; current.finalizers = pair.Value.finalizers;
                    current.ilmanipulators = pair.Value.ilmanipulators;
                    harmony.Patch(pair.Key);
                }
                catch (Exception error) { failures.Add(error); }
            foreach (var undo in Undo.AsEnumerable().Reverse())
                try { undo(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new AggregateException("An integration could not restore its original state; startup cannot safely continue.", failures);
        }
    }
}
