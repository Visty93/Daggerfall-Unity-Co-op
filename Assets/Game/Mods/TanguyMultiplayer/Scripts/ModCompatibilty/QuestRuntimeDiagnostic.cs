using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.Questing.Actions;
using Mirror;
using UnityEngine;

public sealed class QuestRuntimeDiagnostic : MonoBehaviour
{
    static QuestRuntimeDiagnostic instance;

    static readonly FieldInfo QuestTasksField =
        typeof(Quest).GetField("tasks", BindingFlags.Instance | BindingFlags.NonPublic);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (instance != null || !Requested())
            return;

        GameObject go = new GameObject("QuestRuntimeDiagnostic");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<QuestRuntimeDiagnostic>();
    }


    static bool Requested()
    {
        foreach (string arg in Environment.GetCommandLineArgs())
            if (string.Equals(arg, "-dfu-quest-diagnostics", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-dfu-verbose-logs", StringComparison.OrdinalIgnoreCase))
                return true;

        try
        {
            string root = Path.GetDirectoryName(Application.dataPath);
            return File.Exists(Path.Combine(root, "QuestDiagnostics.enabled"));
        }
        catch { return false; }
    }

    void OnEnable()
    {
        QuestMachine.OnQuestStarted += OnQuestStarted;
        QuestMachine.OnQuestEnded += OnQuestEnded;
        Debug.Log("[QuestDiag] Runtime quest diagnostic enabled.");
    }

    void OnDisable()
    {
        QuestMachine.OnQuestStarted -= OnQuestStarted;
        QuestMachine.OnQuestEnded -= OnQuestEnded;
    }

    void OnQuestStarted(Quest quest)
    {
        if (NetworkServer.active || NetworkClient.active || quest == null)
            return;

        Debug.Log(string.Format(
            "[QuestDiag] QUEST START name='{0}' display='{1}' uid={2}",
            quest.QuestName ?? "<null>",
            quest.DisplayName ?? "<null>",
            quest.UID));

        DumpQuestStructure(quest);

        StartCoroutine(DelayedStateDump(quest, 0.10f));
        StartCoroutine(DelayedStateDump(quest, 0.50f));
    }

    void OnQuestEnded(Quest quest)
    {
        if (NetworkServer.active || NetworkClient.active || quest == null)
            return;

        Debug.Log(string.Format(
            "[QuestDiag] QUEST END name='{0}' uid={1} success={2}",
            quest.QuestName ?? "<null>",
            quest.UID,
            quest.QuestSuccess));
    }

    IEnumerator DelayedStateDump(Quest quest, float delay)
    {
        yield return new WaitForSecondsRealtime(delay);

        if (quest == null)
            yield break;

        Debug.Log(string.Format(
            "[QuestDiag] STATE +{0:0.00}s name='{1}' uid={2}",
            delay,
            quest.QuestName ?? "<null>",
            quest.UID));

        DumpTaskStates(quest);
    }

    static Dictionary<string, DaggerfallWorkshop.Game.Questing.Task> GetTasks(Quest quest)
    {
        if (QuestTasksField == null)
        {
            Debug.LogError("[QuestDiag] Could not find Quest.tasks private field.");
            return null;
        }

        return QuestTasksField.GetValue(quest) as
            Dictionary<string, DaggerfallWorkshop.Game.Questing.Task>;
    }

    static void DumpQuestStructure(Quest quest)
    {
        var tasks = GetTasks(quest);
        if (tasks == null)
            return;

        Debug.Log(string.Format("[QuestDiag]   Tasks found: {0}", tasks.Count));

        foreach (var pair in tasks.OrderBy(x => x.Key))
        {
            var task = pair.Value;
            if (task == null)
            {
                Debug.Log(string.Format("[QuestDiag]   TASK '{0}' = <null>", pair.Key));
                continue;
            }

            var actions = task.Actions != null ? task.Actions.ToArray() : new IQuestAction[0];

            Debug.Log(string.Format(
                "[QuestDiag]   TASK '{0}' type={1} triggered={2} dropped={3} actions={4}",
                pair.Key,
                task.Type,
                task.IsTriggered,
                task.IsDropped,
                actions.Length));

            for (int i = 0; i < actions.Length; i++)
            {
                IQuestAction action = actions[i];
                if (action == null)
                {
                    Debug.Log(string.Format("[QuestDiag]      [{0}] <null>", i));
                    continue;
                }

                Debug.Log(string.Format(
                    "[QuestDiag]      [{0}] {1}",
                    i,
                    DescribeAction(action)));
            }
        }
    }

    static void DumpTaskStates(Quest quest)
    {
        var tasks = GetTasks(quest);
        if (tasks == null)
            return;

        foreach (var pair in tasks.OrderBy(x => x.Key))
        {
            var task = pair.Value;
            if (task == null)
                continue;

            if (task.IsTriggered || task.IsDropped)
            {
                Debug.Log(string.Format(
                    "[QuestDiag]   ACTIVE/CHANGED TASK '{0}' type={1} triggered={2} dropped={3}",
                    pair.Key,
                    task.Type,
                    task.IsTriggered,
                    task.IsDropped));
            }
        }
    }

    static string DescribeAction(IQuestAction action)
    {
        PickOneOf pick = action as PickOneOf;
        if (pick != null)
        {
            string branches = pick.taskSymbols == null
                ? "<null>"
                : string.Join(", ", pick.taskSymbols
                    .Where(x => x != null)
                    .Select(x => x.Name)
                    .ToArray());

            return "PickOneOf branches=[" + branches + "]";
        }

        StartQuest startQuest = action as StartQuest;
        if (startQuest != null)
        {
            string target = ReadPrivateString(startQuest, "questName");
            int index1 = ReadPrivateInt(startQuest, "questIndex1");

            if (!string.IsNullOrEmpty(target))
                return "StartQuest target='" + target + "'";

            if (index1 > 0)
                return "StartQuest index=" + index1;

            return "StartQuest";
        }

        EndQuest endQuest = action as EndQuest;
        if (endQuest != null)
        {
            int textId = ReadPrivateInt(endQuest, "textId");
            return textId != 0
                ? "EndQuest saying=" + textId
                : "EndQuest";
        }

        return action.GetType().Name;
    }

    static string ReadPrivateString(object obj, string fieldName)
    {
        if (obj == null)
            return null;

        FieldInfo field = obj.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        return field != null ? field.GetValue(obj) as string : null;
    }

    static int ReadPrivateInt(object obj, string fieldName)
    {
        if (obj == null)
            return 0;

        FieldInfo field = obj.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (field == null)
            return 0;

        object value = field.GetValue(obj);
        return value is int ? (int)value : 0;
    }
}
