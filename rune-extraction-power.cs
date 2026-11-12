using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using EpicLoot;
using EpicLoot.Crafting;
using EpicLoot.CraftingV2;
using EpicLoot.ShardStones;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace EpicLootProgressionFix
{
    // This belongs to the affix, including while it is a rune or socket, rather than to the equipment carrying it.
    internal static class RuneExtractionPower
    {
        private sealed class AppliedPower { internal float Highest; }
        private static readonly ConditionalWeakTable<MagicItemEffect, AppliedPower> Applied =
            new ConditionalWeakTable<MagicItemEffect, AppliedPower>();
        private static readonly ConditionalWeakTable<MagicItemEffect, JObject> EffectExtras = new ConditionalWeakTable<MagicItemEffect, JObject>();
        private static readonly ConditionalWeakTable<MagicItem, JObject> MagicExtras = new ConditionalWeakTable<MagicItem, JObject>();
        private static readonly EffectConverter Effects = new EffectConverter();
        private static readonly JsonConverter[] Converters = { new MagicConverter(), Effects };
        internal const string SavedField = "HighestRuneExtractionPower";
        internal const float LegacyRunePower = 1.1f;
        private static readonly HashSet<string> EffectFields = Fields(typeof(MagicItemEffect));
        private static readonly HashSet<string> MagicFields = Fields(typeof(MagicItem));

        internal static float Highest(MagicItemEffect effect) => Applied.TryGetValue(effect, out var power) ? power.Highest : 1f;
        internal static float RuneHistory(MagicItemEffect effect) => Applied.TryGetValue(effect, out var power) ? power.Highest : LegacyRunePower;
        internal static bool ValidPower(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 1f && value < 999f;
        internal static void Set(MagicItemEffect effect, float power) => Applied.GetValue(effect, _ => new AppliedPower()).Highest = power;
        internal static void Copy(MagicItemEffect source, MagicItemEffect target)
        {
            if (source != null && target != null && Applied.TryGetValue(source, out var power)) Set(target, power.Highest);
            if (source != null && target != null && EffectExtras.TryGetValue(source, out var extra))
            {
                EffectExtras.Remove(target);
                EffectExtras.Add(target, (JObject)extra.DeepClone());
            }
        }
        internal static void EnsureRuneHistory(MagicItemEffect effect)
        {
            if (effect == null || Applied.TryGetValue(effect, out _) || MagicItemEffectDefinitions.IsValuelessEffect(effect.EffectType)) return;
            Set(effect, RuneHistory(effect));
        }
        internal static void LegacyRune(ItemDrop.ItemData item)
        {
            if (!RuneStacks.IsRune(item)) return;
            var magic = item.GetMagicItem();
            if (magic == null) return;
            foreach (var effect in magic.Effects) EnsureRuneHistory(effect);
        }
        internal static void LegacySockets(MagicItem magic)
        {
            if (magic == null) return;
            foreach (var socket in magic.Sockets) LegacySocket(socket);
        }
        internal static void LegacySocket(SocketedEffect socket)
        {
            if (socket?.Effect != null && socket.ShardType == ShardType.None &&
                socket.SourcePrefab?.StartsWith("EtchedRunestone", StringComparison.Ordinal) == true)
                EnsureRuneHistory(socket.Effect);
        }
        internal static void Extract(MagicItemEffect source, MagicItemEffect rune, float power)
        {
            Copy(source, rune);
            if (MagicItemEffectDefinitions.IsValuelessEffect(source.EffectType)) return;
            float highest = Highest(source);
            rune.EffectValue = source.EffectValue;
            if (!float.IsNaN(power) && power > 0f && power < 999f)
            {
                if (power > highest) { rune.EffectValue *= power / highest; highest = power; }
            }
            // Save 100% too, so a new rune can be distinguished from an older rune with no record.
            Set(rune, highest);
        }
        internal static MagicItemEffect CopyEffect(string effectType, float value, MagicItemEffect source)
        {
            var result = new MagicItemEffect(effectType, value);
            Copy(source, result);
            return result;
        }
        internal static string Serialize(object value) => JsonConvert.SerializeObject(value, Formatting.None, Converters);
        internal static string Serialize(object value, Formatting formatting) => JsonConvert.SerializeObject(value, formatting, Converters);
        internal static MagicItem Deserialize(string value)
        {
            var magic = JsonConvert.DeserializeObject<MagicItem>(value, Converters);
            if (magic == null) return null;
            if (!MagicExtras.TryGetValue(magic, out _) && !magic.Effects.Any(effect => EffectExtras.TryGetValue(effect, out _)) &&
                !magic.Sockets.Any(socket => socket?.Effect != null && EffectExtras.TryGetValue(socket.Effect, out _))) return magic;
            // Keep unknown numeric fields at their saved precision too; the game's known values remain Singles.
            var data = RuneStacks.ReadPayload(value);
            MagicExtras.Remove(magic); MagicExtras.Add(magic, Extras(data, MagicFields));
            void Remember(MagicItemEffect effect, JObject raw)
            {
                if (effect == null || raw == null) return;
                EffectExtras.Remove(effect); EffectExtras.Add(effect, Extras(raw, EffectFields));
            }
            for (int index = 0; index < magic.Effects.Count; index++) Remember(magic.Effects[index], data["Effects"]?[index] as JObject);
            for (int index = 0; index < magic.Sockets.Count; index++) Remember(magic.Sockets[index]?.Effect, data["Sockets"]?[index]?["Effect"] as JObject);
            return magic;
        }

        private static HashSet<string> Fields(Type type)
        {
            var fields = new HashSet<string>(((JsonObjectContract)JsonSerializer.CreateDefault().ContractResolver.ResolveContract(type)).Properties
                .Where(property => !property.Ignored).Select(property => property.PropertyName), StringComparer.OrdinalIgnoreCase);
            return fields;
        }

        private static JObject Extras(JObject data, HashSet<string> fields) => new JObject(data.Properties()
            .Where(property => !fields.Contains(property.Name) && (fields != EffectFields || property.Name != SavedField))
            .Select(property => new JProperty(property.Name, property.Value.DeepClone())));

        private static void PreserveExtras(JObject data, JObject extras)
        {
            foreach (var property in extras.Properties())
                if (data.Property(property.Name) == null) data.Add(property.Name, property.Value.DeepClone());
        }

        private sealed class MagicConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => type == typeof(MagicItem);
            private static JsonSerializer Serializer()
            {
                var serializer = JsonSerializer.CreateDefault(); serializer.Converters.Add(Effects); return serializer;
            }
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                if (value == null) { writer.WriteNull(); return; }
                var magic = (MagicItem)value;
                var data = JObject.FromObject(magic, Serializer());
                if (MagicExtras.TryGetValue(magic, out var extra)) PreserveExtras(data, extra);
                data.WriteTo(writer);
            }
            public override object ReadJson(JsonReader reader, Type type, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null) return null;
                var data = JObject.Load(reader);
                var magic = data.ToObject<MagicItem>(Serializer());
                var extra = Extras(data, MagicFields);
                if (extra.Count > 0) MagicExtras.Add(magic, extra);
                return magic;
            }
        }

        private sealed class EffectConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => type == typeof(MagicItemEffect);
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                if (value == null) { writer.WriteNull(); return; }
                var effect = (MagicItemEffect)value;
                // Keep EpicLoot's normal fields and ordering, including identical old binary rune data.
                var data = JObject.FromObject(effect, JsonSerializer.CreateDefault());
                if (Applied.TryGetValue(effect, out var power)) data[SavedField] = power.Highest;
                if (EffectExtras.TryGetValue(effect, out var extra)) PreserveExtras(data, extra);
                data.WriteTo(writer);
            }
            public override object ReadJson(JsonReader reader, Type type, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null) return null;
                var data = JObject.Load(reader);
                var effect = data.ToObject<MagicItemEffect>(JsonSerializer.CreateDefault());
                if (data.TryGetValue(SavedField, out var saved))
                {
                    float power = saved.Value<float>();
                    if (!ValidPower(power))
                        throw new JsonSerializationException("Invalid saved " + SavedField + ": " + saved);
                    Set(effect, power);
                }
                var extra = Extras(data, EffectFields);
                if (extra.Count > 0) EffectExtras.Add(effect, extra);
                return effect;
            }
        }
    }

    // Saving, loading and both etch results copy the magic item through JSON; each copy keeps the extraction history.
    [HarmonyPatch]
    internal static class RunePowerSerialization
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MagicItemComponent), "Serialize");
            yield return AccessTools.Method(typeof(MagicItemComponent), "Parse");
            yield return AccessTools.Method(typeof(EnchantingUIController), "BuildRuneEtchResult");
            yield return AccessTools.Method(typeof(EnchantingUIController), "BuildSetEtchResult");
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            int replaced = 0;
            foreach (var instruction in code)
            {
                if (!(instruction.operand is MethodInfo method) || method.DeclaringType != typeof(JsonConvert)) continue;
                if (method.Name == "SerializeObject")
                    instruction.operand = AccessTools.Method(typeof(RuneExtractionPower), "Serialize",
                        method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
                else if (method.Name == "DeserializeObject" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(MagicItem))
                    instruction.operand = AccessTools.Method(typeof(RuneExtractionPower), "Deserialize");
                else continue;
                instruction.opcode = OpCodes.Call;
                replaced++;
            }
            if (replaced != (__originalMethod.DeclaringType == typeof(EnchantingUIController) ? 2 : 1))
                throw new InvalidOperationException("EpicLoot's magic-item serialization changed: " + __originalMethod.Name);
            return code;
        }
    }

    [HarmonyPatch]
    internal static class RunePowerCopies
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(EnchantingUIController), "BuildRuneEtchResult");
            yield return AccessTools.Method(typeof(TemperData), "OnFail");
            yield return AccessTools.Method(typeof(ShardSocketManager), "ResolveSocketedEffect");
            yield return AccessTools.Method(typeof(ShardSocketManager), "ReconstructShardItem");
            yield return AccessTools.Method(typeof(TransferMagicalEffects), "TransferMagicToCraftedItem");
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            var constructor = AccessTools.Constructor(typeof(MagicItemEffect), new[] { typeof(string), typeof(float) });
            var effectType = AccessTools.PropertyGetter(typeof(MagicItemEffect), "EffectType");
            var effectValue = AccessTools.Field(typeof(MagicItemEffect), "EffectValue");
            int replaced = 0;
            for (int index = 1; index < code.Count; index++)
            {
                if (code[index].opcode != OpCodes.Newobj || !Equals(code[index].operand, constructor) ||
                    !code[index - 1].LoadsField(effectValue)) continue;
                int getter = code.FindLastIndex(index - 2, instruction => instruction.Calls(effectType));
                if (getter < 0) throw new InvalidOperationException("EpicLoot's affix copy changed: " + __originalMethod.Name);
                // Repeat the original source load, leaving it after the existing type and value arguments.
                var source = code.GetRange(getter + 1, index - getter - 2)
                    .Select(instruction => new CodeInstruction(instruction.opcode, instruction.operand)).ToList();
                source[0].labels.AddRange(code[index].labels);
                code[index].labels.Clear();
                code[index].opcode = OpCodes.Call;
                code[index].operand = AccessTools.Method(typeof(RuneExtractionPower), "CopyEffect");
                code.InsertRange(index, source);
                index += source.Count;
                replaced++;
            }
            if (replaced != (__originalMethod.Name == "TransferMagicToCraftedItem" ? 2 : 1))
                throw new InvalidOperationException("EpicLoot's affix copy count changed: " + __originalMethod.Name);
            return code;
        }
    }

    [HarmonyPatch(typeof(EnchantingUIController), "BuildRuneEtchResult")]
    internal static class LegacyRuneEtching
    {
        private static void Prefix(ItemDrop.ItemData rune) => RuneExtractionPower.LegacyRune(rune);
    }

    [HarmonyPatch(typeof(ShardSocketManager), "ResolveSocketedEffect")]
    internal static class LegacyRuneSocketing
    {
        private static void Prefix(ItemDrop.ItemData input) => RuneExtractionPower.LegacyRune(input);
    }

    [HarmonyPatch(typeof(ShardSocketManager), "ReconstructShardItem")]
    internal static class LegacySocketPower
    {
        private static void Prefix(SocketedEffect socketed) => RuneExtractionPower.LegacySocket(socketed);
    }

    // EpicLoot parses an item's saved magic each time that saved value changes.
    [HarmonyPatch(typeof(MagicItemComponent), "Parse")]
    internal static class LoadedSocketPower
    {
        private static void Postfix(MagicItemComponent __instance, MagicItem __result)
        {
            RuneExtractionPower.LegacySockets(__result);
            if (RuneStacks.IsRune(__instance.Item) && __result != null)
                foreach (var effect in __result.Effects)
                    RuneExtractionPower.EnsureRuneHistory(effect);
        }
    }

    [HarmonyPatch(typeof(TemperData), MethodType.Constructor, new[] { typeof(ItemDrop.ItemData), typeof(string) })]
    internal static class TemperedRunePower
    {
        internal static readonly AccessTools.FieldRef<TemperData, MagicItem> Updated = AccessTools.FieldRefAccess<TemperData, MagicItem>("_tempItem");
        private static void Postfix(TemperData __instance, ItemDrop.ItemData itemData)
        {
            RuneExtractionPower.LegacyRune(itemData);
            var source = itemData.GetMagicItem();
            RuneExtractionPower.LegacySockets(source);
            var result = Updated(__instance);
            if (result == null) return;
            for (int index = 0; index < source.Effects.Count; index++) RuneExtractionPower.Copy(source.Effects[index], result.Effects[index]);
            for (int index = 0; index < source.Sockets.Count; index++) RuneExtractionPower.Copy(source.Sockets[index]?.Effect, result.Sockets[index]?.Effect);
        }
    }

    [HarmonyPatch(typeof(TemperData), nameof(TemperData.GetTemperRange))]
    internal static class ExtractedTemperingRange
    {
        private static void Postfix(MagicItemEffect effect, ref MagicItemEffectDefinition.ValueDef __result)
        {
            if (__result == null || MagicItemEffectDefinitions.IsValuelessEffect(effect.EffectType)) return;
            float power = RuneExtractionPower.Highest(effect);
            __result = new MagicItemEffectDefinition.ValueDef
            {
                MinValue = __result.MinValue * power,
                MaxValue = __result.MaxValue * power,
                Increment = __result.Increment * power
            };
        }
    }

    [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
    internal static class RunePowerDescription
    {
        private static void Postfix(ItemDrop.ItemData item, ref string __result)
        {
            if (!RuneStacks.IsRune(item)) return;
            var effects = item.GetMagicItem()?.Effects;
            if (effects == null || effects.Count != 1 || MagicItemEffectDefinitions.IsValuelessEffect(effects[0].EffectType)) return;
            // The base 100% sets no rune apart, so only another power shows.
            string power = (RuneExtractionPower.RuneHistory(effects[0]) * 100f).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            if (power != "100") __result += "\n\nBest extraction power: " + power + "%";
        }
    }

    [HarmonyPatch(typeof(TemperData), nameof(TemperData.GetTooltip))]
    internal static class ExtractedTemperingPreview
    {
        private static MagicItemEffectDefinition.ValueDef Range(MagicItem item, MagicItemEffectDefinition definition, MagicItemEffect effect) =>
            TemperData.GetTemperRange(item, effect);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var definition = AccessTools.Method(typeof(MagicItemEffectDefinitions), "Get", new[] { typeof(string) });
            var effectType = AccessTools.PropertyGetter(typeof(MagicItemEffect), "EffectType");
            var range = AccessTools.Method(typeof(TemperData), "ResolveValues");
            int definitionIndex = code.FindIndex(instruction => instruction.Calls(definition));
            if (definitionIndex < 2 || !code[definitionIndex - 1].Calls(effectType) || !code[definitionIndex - 2].IsLdloc())
                throw new InvalidOperationException("EpicLoot's tempering preview affix changed.");
            var effect = code[definitionIndex - 2];
            int found = 0;
            foreach (var instruction in code)
            {
                if (instruction.Calls(range))
                {
                    found++;
                    yield return new CodeInstruction(effect.opcode, effect.operand).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(ExtractedTemperingPreview), nameof(Range));
                }
                yield return instruction;
            }
            if (found != 1) throw new InvalidOperationException("EpicLoot's tempering preview range changed.");
        }
    }
}
