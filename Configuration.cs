using System;
using Dalamud.Configuration;

namespace KeyboundTyper;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public string Language { get; set; } = "pt"; // "pt" (padrão) ou "en"
    public bool Enabled { get; set; } = false;
    public bool OnlyInKeyboundDuty { get; set; } = true;

    // Auto-Loop / Auto-Queue configuration
    public bool AutoLoopEnabled { get; set; } = false;
    public bool InfiniteLoops { get; set; } = true;
    public int LoopCount { get; set; } = 10;
    public int RemainingLoops { get; set; } = 10;
    public int CompletedLoops { get; set; } = 0;
    public bool AutoAcceptQueue { get; set; } = true;
    public bool AutoRegister { get; set; } = true;
    public bool AutoExitDuty { get; set; } = true;

    // Typing speed configuration (in milliseconds per character)
    public int InitialDelayMs { get; set; } = 100;
    public int MinCharDelayMs { get; set; } = 65;
    public int MaxCharDelayMs { get; set; } = 114;
    public int DelayBeforeEnterMs { get; set; } = 100;
    public int DelayPostEnterMs { get; set; } = 120;
    public int DelayAfterWordMs { get; set; } = 50;

    // Delay before choosing/starting the next word (in milliseconds, 0 = Instant)
    public int ReactionDelayMs { get; set; } = 48;

    // Minimum delay before allowing the exact same word to be typed again (prevents spamming before death)
    public int SameWordRetryDelayMs { get; set; } = 400;

    // Prioritize smaller words (mobs pequenos) before long boss words (padrão desligado conforme solicitado)
    public bool PrioritizeShortWords { get; set; } = false;

    // Smart priority: Barrier (escudo) > Casts (inimigos castando) > Adds normais > Boss
    public bool PrioritizeBarrier { get; set; } = true;
    public bool PrioritizeCasts { get; set; } = true;

    // Filter out enemy cast names (e.g. Bad Breath, Tankbuster) so they are never typed as target words
    public bool FilterCastNames { get; set; } = true;

    // Gaze / Stop Typing mechanics: pause typing when a dangerous cast is completing
    public bool RespectGaze { get; set; } = true;

    public bool AutoPressEnter { get; set; } = true;
    public bool RespectLetterlock { get; set; } = true;

    // Input method: 1 = SendInput (Hardware direto - Previne AFK kick & Não come letras), 0 = PostMessage
    public int InputMethod { get; set; } = 1;

    // Presses a harmless key periodically so the game does not kick for inactivity.
    public bool AntiAfk { get; set; } = true;
    public int AntiAfkIntervalSec { get; set; } = 15;

    // Custom addon name override if needed
    public string CustomAddonName { get; set; } = "TurnBreakGame";

    // StringArrayData index that holds the on-screen target word (found via diagnostic: 128).
    public int WordStringArrayIndex { get; set; } = 128;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
