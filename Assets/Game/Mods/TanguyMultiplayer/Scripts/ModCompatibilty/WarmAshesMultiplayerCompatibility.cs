// Original multiplayer adapter. Contains no Warm Ashes source or quest assets.
// Requires Warm Ashes to supply and execute its own quests.
// Uses the existing QuestNetSync exclusion list; no QuestNetSync file edits.
// Install on all peers and start a fresh network session.
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.Questing.Actions;
using Mirror;
using UnityEngine;

public sealed class WarmAshesMultiplayerCompatibility : MonoBehaviour
{
    static WarmAshesMultiplayerCompatibility instance;
    const string TowerIntroduction = "WAQ_GUARDTOWER_WOD";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (instance != null) return;
        var go = new GameObject("WarmAshesMultiplayerCompatibility");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<WarmAshesMultiplayerCompatibility>();
    }

    HashSet<string> sharingExclusions;
    bool addedIntroductionExclusion;

    void OnEnable()
    {
        // Only the introductory dispatcher is private. Its actual job, payment,
        // enemies and journal entries retain the normal multiplayer sharing path.
        // Register before a local player subscribes to quest-start events.
        var field = typeof(QuestNetSync).GetField("_questSharingBlacklist",
            BindingFlags.Static | BindingFlags.NonPublic);
        sharingExclusions = field != null ? field.GetValue(null) as HashSet<string> : null;
        if (sharingExclusions == null)
        {
            Debug.LogError("[WarmAshesMP v3] Existing quest-sharing exclusion list unavailable; adapter disabled.");
            return;
        }
        addedIntroductionExclusion = sharingExclusions.Add(TowerIntroduction);
        QuestMachine.OnQuestStarted += OnQuestStarted;
        Debug.Log("[WarmAshesMP v3] Tower introduction is local; generated jobs use normal quest sharing.");
    }

    void OnDisable()
    {
        QuestMachine.OnQuestStarted -= OnQuestStarted;
        if (addedIntroductionExclusion && sharingExclusions != null)
            sharingExclusions.Remove(TowerIntroduction);
        addedIntroductionExclusion = false;
        sharingExclusions = null;
    }

    void OnQuestStarted(Quest quest)
    {
        if ((!NetworkServer.active && !NetworkClient.active) || quest == null) return;
        string name = quest.QuestName ?? string.Empty;
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - 4);
        if (!string.Equals(name, TowerIntroduction, StringComparison.OrdinalIgnoreCase)) return;

        // Inspect the installed quest's actions rather than providing a replacement
        // script or selecting/starting any of its follow-up quests ourselves.
        var task = quest.GetTask(new Symbol("_accepttask_"));
        if (task == null) return;
        var actions = task.Actions.ToArray();
        if (actions.Length != 2) { Unsupported(); return; }
        var choice = actions[0] as PickOneOf;
        var earlyEnd = actions[1] as EndQuest;
        if (choice == null || earlyEnd == null || choice.taskSymbols == null ||
            choice.taskSymbols.Length == 0) { Unsupported(); return; }

        // Only suppress the redundant early end if EVERY possible selected branch
        // starts a follow-up then ends the introduction itself. Preserve native
        // StartQuest actions so the fork's existing chain authority still applies.
        foreach (Symbol symbol in choice.taskSymbols)
        {
            var branch = quest.GetTask(symbol);
            if (branch == null) { Unsupported(); return; }
            var branchActions = branch.Actions.ToArray();
            if (branchActions.Length != 2 || !(branchActions[0] is StartQuest) ||
                !(branchActions[1] is EndQuest)) { Unsupported(); return; }
        }

        // Retain the original adapter's safe ordering: the selected branch
        // schedules its job and then ends this local introduction. No helper code
        // selects a branch, starts a job, or deduplicates jobs by their template name.
        if (!earlyEnd.IsComplete)
        {
            earlyEnd.SetComplete();
            Debug.Log("[WarmAshesMP v3] Tower introduction: deferred ending to the selected follow-up branch.");
        }
    }

    static void Unsupported()
    {
        Debug.LogWarning("[WarmAshesMP v3] Tower introduction has an unexpected action layout; left unchanged.");
    }
}
