using HarmonyLib;
using UnityEngine;
using Verse;

namespace HotSwap;

[HarmonyPatch(typeof(DebugWindowsOpener), nameof(DebugWindowsOpener.DevToolStarterOnGUI))]
internal static class AddDebugButtonPatch
{
    private static void Prefix()
    {
        if (Event.current.type == EventType.Repaint && --HotSwapMain.runInFrames == 0)
            HotSwapMain.HotSwapAll();

        if (HotSwapMain.HotSwapKey.KeyDownEvent)
        {
            HotSwapMain.ScheduleHotSwap();
            Event.current.Use();
        }
    }
}

[HarmonyPatch(typeof(DebugWindowsOpener), nameof(DebugWindowsOpener.DrawButtons))]
internal static class DebugButtonsPatch
{
    private static void Draw(WidgetRow row)
    {
        if (row.ButtonIcon(TexButton.Paste, $"Hot swap [{HotSwapMain.HotSwapKey.MainKeyLabel}]"))
            HotSwapMain.ScheduleHotSwap();
    }

    private static void Postfix(DebugWindowsOpener __instance)
    {
        Draw(__instance.widgetRow);
        __instance.widgetRowFinalX = __instance.widgetRow.FinalX;
    }        
}
