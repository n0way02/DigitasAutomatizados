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

    public static bool TryClickButton(AtkUnitBase* addon, uint nodeId)
    {
        if (addon == null) return false;
        var node = addon->GetNodeById(nodeId);
        if (node == null) return false;

        if (node->Type >= NodeType.Component)
        {
            var compNode = (AtkComponentNode*)node;
            if (compNode->Component != null)
            {
                var btn = (AtkComponentButton*)compNode->Component;
                return ClickButton(addon, btn);
            }
        }
        return false;
    }

    public static bool TryClickButtonByText(AtkUnitBase* addon, string targetText)
    {
        if (addon == null || addon->UldManager.NodeList == null) return false;

        for (int i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || !node->IsVisible()) continue;

            if (node->Type >= NodeType.Component)
            {
                var compNode = (AtkComponentNode*)node;
                if (compNode->Component == null) continue;

                var subUld = &compNode->Component->UldManager;
                if (subUld != null && subUld->NodeList != null)
                {
                    for (int j = 0; j < subUld->NodeListCount; j++)
                    {
                        var subNode = subUld->NodeList[j];
                        if (subNode != null && subNode->Type == NodeType.Text)
                        {
                            var textNode = (AtkTextNode*)subNode;
                            var nodeText = textNode->NodeText.ToString();
                            if (!string.IsNullOrWhiteSpace(nodeText) &&
                                nodeText.Equals(targetText, StringComparison.OrdinalIgnoreCase))
                            {
                                var btn = (AtkComponentButton*)compNode->Component;
                                return ClickButton(addon, btn);
                            }
                        }
                    }
                }
            }
        }
        return false;
    }

    public static bool ClickKtgResultExit()
    {
        var ptr = Plugin.GameGui.GetAddonByName("KTGResult");
        if (ptr.Address == nint.Zero) return false;
        var addon = (AtkUnitBase*)ptr.Address;
        if (addon == null || !addon->IsVisible) return false;

        // 1. Saucy Node 48 (ResultCloseButtonId = 48)
        if (TryClickButton(addon, 48))
        {
            Plugin.Log.Information("[AddonHelper] Clicou no botão Exit do KTGResult via Node 48.");
            return true;
        }

        // 2. Busca dinâmica por texto "Exit"
        if (TryClickButtonByText(addon, "Exit"))
        {
            Plugin.Log.Information("[AddonHelper] Clicou no botão Exit do KTGResult via busca por texto.");
            return true;
        }

        // 3. Fallbacks de callback
        FireCallback(addon, 0);
        FireCallback(addon, 48);
        return true;
    }

    public static bool ClickSelectYesnoYes()
    {
        var ptr = Plugin.GameGui.GetAddonByName("SelectYesno");
        if (ptr.Address == nint.Zero) return false;

        var addon = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectYesno*)ptr.Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible) return false;

        if (addon->YesButton != null && ClickButton(&addon->AtkUnitBase, addon->YesButton))
        {
            Plugin.Log.Information("[AddonHelper] Clicou no botão Yes do SelectYesno nativo.");
            return true;
        }

        FireCallback(&addon->AtkUnitBase, 0);
        return true;
    }
}
