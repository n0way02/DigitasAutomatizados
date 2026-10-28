using System;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace KeyboundTyper.Services;

public static unsafe class AddonHelper
{
    public static void InteractWithGameObject(nint address)
    {
        if (address == nint.Zero) return;
        var ts = TargetSystem.Instance();
        if (ts != null)
        {
            ts->InteractWithObject((GameObject*)address);
        }
    }

    public static void FireCallback(AtkUnitBase* addon, params object[] values)
    {
        if (addon == null || values.Length == 0) return;

        var atkValues = stackalloc AtkValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            var val = values[i];
            if (val is int intVal)
            {
                atkValues[i].Type = AtkValueType.Int;
                atkValues[i].Int = intVal;
            }
            else if (val is uint uintVal)
            {
                atkValues[i].Type = AtkValueType.UInt;
                atkValues[i].UInt = uintVal;
            }
            else if (val is bool boolVal)
            {
                atkValues[i].Type = AtkValueType.Bool;
                atkValues[i].Byte = (byte)(boolVal ? 1 : 0);
            }
            else
            {
                atkValues[i].Type = AtkValueType.Int;
                atkValues[i].Int = 0;
            }
        }

        addon->FireCallback((uint)values.Length, atkValues, true);
    }

    public static bool ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || button == null || !button->IsEnabled)
            return false;

        var ownerNode = button->AtkComponentBase.OwnerNode;
        if (ownerNode == null)
            return false;

        var evt = ownerNode->AtkResNode.AtkEventManager.Event;
        if (evt == null)
            return false;

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        return true;
    }

    public static bool ClickContentsFinderCommence()
    {
        var ptr = Plugin.GameGui.GetAddonByName("ContentsFinderConfirm");
        if (ptr.Address == nint.Zero) return false;

        var addon = (FFXIVClientStructs.FFXIV.Client.UI.AddonContentsFinderConfirm*)ptr.Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible) return false;

        // 1. Tenta clique nativo direto no CommenceButton
        if (addon->CommenceButton != null && ClickButton(&addon->AtkUnitBase, addon->CommenceButton))
        {
            Plugin.Log.Information("[AddonHelper] Clicou no botão Commence nativo via AtkButtonClicker.");
            return true;
        }

        // 2. Fallback: FireCallback com index 8 (Commence no ContentsFinderConfirm)
        FireCallback(&addon->AtkUnitBase, 8);
        return true;
    }
}
