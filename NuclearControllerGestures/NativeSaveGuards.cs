using System;
using System.Reflection;
using HarmonyLib;
using Rewired;
namespace NuclearControllerGestures {
    internal static class NativeSaveGuards {
        [HarmonyPatch(typeof(Rewired.Player), nameof(Rewired.Player.GetSaveData), new[] { typeof(bool) })]
        private static class SavePlayerPatch
        {

            private static void Postfix(ref PlayerSaveData __result) => Plugin.Instance?.Pulse.FilterSave(ref __result);
        }
        [HarmonyPatch(typeof(Rewired.Player.ControllerHelper.MapHelper), "GetMapSaveData", new[] { typeof(ControllerType), typeof(int), typeof(bool) })]
        private static class SaveMapCollectionPatch
        {

            private static void Postfix(ref ControllerMapSaveData[] __result) => Plugin.Instance?.Pulse.FilterSave(ref __result);
        }
        [HarmonyPatch(typeof(Rewired.Player.ControllerHelper.MapHelper), "GetAllMapSaveData", new[] { typeof(ControllerType), typeof(bool) })]
        private static class SaveTypedMapCollectionPatch
        {

            private static void Postfix(ref ControllerMapSaveData[] __result) => Plugin.Instance?.Pulse.FilterSave(ref __result);
        }
        [HarmonyPatch]
        private static class SaveAllMapCollectionPatch
        {
            private static MethodBase TargetMethod()
            {
                // A generic overload has the same argument types. Select the native array
                // overload explicitly rather than leave Harmony with an ambiguous lookup.
                foreach (var method in typeof(Rewired.Player.ControllerHelper.MapHelper).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (method.Name != "GetAllMapSaveData" || method.IsGenericMethod || method.ReturnType != typeof(ControllerMapSaveData[])) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool)) return method;
                }
                throw new MissingMethodException("Native Rewired GetAllMapSaveData(bool) array overload is missing.");
            }

            private static void Postfix(ref ControllerMapSaveData[] __result) => Plugin.Instance?.Pulse.FilterSave(ref __result);
        }
        [HarmonyPatch(typeof(ControllerMap), MethodType.Constructor, new[] { typeof(ControllerMap) })]
        private static class CopyMapPatch
        {
            private static void Prefix(ControllerMap __0, out bool __state)
            { __state = Plugin.Instance?.Pulse.Owns(__0) == true; }
            private static void Postfix(ControllerMap __instance, bool __state)
            { if (__state) Plugin.Instance?.Pulse.RememberCopy(__instance); }
        }
    }
}
