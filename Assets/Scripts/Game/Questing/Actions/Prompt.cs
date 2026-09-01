// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    
// 
// Notes:
//

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Utility;
using FullSerializer;

namespace DaggerfallWorkshop.Game.Questing.Actions
{
    /// <summary>
    /// Prompt which displays a yes/no dialog that executes a different task based on user input.
    /// </summary>
    public class Prompt : ActionTemplate
    {
        int id;
        Symbol yesTaskSymbol;
        Symbol noTaskSymbol;

        // Rebuilt whenever this prompt is displayed. Used only to identify which
        // quest task owns the multiplayer yes/no choice.
        string runtimeOwnerTaskSymbol = string.Empty;
        DaggerfallMessageBox runtimeMessageBox = null;

        // A replicated NPC click can open the same modal prompt on several machines.
        // QuestNetSync closes the non-source copies when the authoritative choice arrives,
        // otherwise their local QuestMachine remains paused and the selected branch never
        // reaches PlaceItem/StartTimer/Log/Say.
        static readonly Dictionary<string, Prompt> activeNetworkPrompts =
            new Dictionary<string, Prompt>(StringComparer.OrdinalIgnoreCase);

        static string MakeNetworkPromptKey(
            ulong questUID,
            string ownerTaskSymbol,
            int promptMessageId)
        {
            return questUID.ToString() + "|" +
                (ownerTaskSymbol ?? string.Empty) + "|" +
                promptMessageId.ToString();
        }

        void RegisterNetworkPrompt()
        {
            if (ParentQuest == null || string.IsNullOrEmpty(runtimeOwnerTaskSymbol))
                return;

            activeNetworkPrompts[
                MakeNetworkPromptKey(
                    ParentQuest.UID,
                    runtimeOwnerTaskSymbol,
                    id)] = this;
        }

        void UnregisterNetworkPrompt()
        {
            if (ParentQuest == null || string.IsNullOrEmpty(runtimeOwnerTaskSymbol))
                return;

            string key = MakeNetworkPromptKey(
                ParentQuest.UID,
                runtimeOwnerTaskSymbol,
                id);

            Prompt registered;
            if (activeNetworkPrompts.TryGetValue(key, out registered) &&
                object.ReferenceEquals(registered, this))
                activeNetworkPrompts.Remove(key);
        }

        void CloseRegisteredMessageBox()
        {
            DaggerfallMessageBox messageBox = runtimeMessageBox;
            runtimeMessageBox = null;
            UnregisterNetworkPrompt();

            if (messageBox == null)
                return;

            try { messageBox.OnButtonClick -= MessageBox_OnButtonClick; }
            catch { }
            try { messageBox.CloseWindow(); }
            catch { }
        }

        public static void CloseNetworkPrompt(
            ulong questUID,
            string ownerTaskSymbol,
            int promptMessageId)
        {
            if (questUID == 0UL || string.IsNullOrEmpty(ownerTaskSymbol))
                return;

            string key = MakeNetworkPromptKey(
                questUID,
                ownerTaskSymbol,
                promptMessageId);

            Prompt prompt;
            if (!activeNetworkPrompts.TryGetValue(key, out prompt) || prompt == null)
                return;

            activeNetworkPrompts.Remove(key);
            prompt.CloseRegisteredMessageBox();
        }

        public override string Pattern
        {
            get { return @"prompt (?<id>\d+) yes (?<yesTaskName>[a-zA-Z0-9_.]+) no (?<noTaskName>[a-zA-Z0-9_.]+)|" +
                         @"prompt (?<idName>\w+) yes (?<yesTaskName>[a-zA-Z0-9_.]+) no (?<noTaskName>[a-zA-Z0-9_.]+)"; }
        }

        public Prompt(Quest parentQuest)
            : base(parentQuest)
        {
            allowRearm = false;
        }

        public override IQuestAction CreateNew(string source, Quest parentQuest)
        {
            // Source must match pattern
            Match match = Test(source);
            if (!match.Success)
                return null;

            // Factory new prompt
            Prompt prompt = new Prompt(parentQuest);
            prompt.id = Parser.ParseInt(match.Groups["id"].Value);
            prompt.yesTaskSymbol = new Symbol(match.Groups["yesTaskName"].Value);
            prompt.noTaskSymbol = new Symbol(match.Groups["noTaskName"].Value);

            // Resolve static message back to ID
            string idName = match.Groups["idName"].Value;
            if (prompt.id == 0 && !string.IsNullOrEmpty(idName))
            {
                Table table = QuestMachine.Instance.StaticMessagesTable;
                prompt.id = Parser.ParseInt(table.GetValue("id", idName));
            }

            return prompt;
        }

        public override void Update(Task caller)
        {
            runtimeOwnerTaskSymbol =
                caller != null && caller.Symbol != null
                    ? caller.Symbol.Name
                    : string.Empty;

            DaggerfallMessageBox messageBox = QuestMachine.Instance.CreateMessagePrompt(ParentQuest, id);
            if (messageBox != null)
            {
                runtimeMessageBox = messageBox;
                RegisterNetworkPrompt();
                messageBox.OnButtonClick += MessageBox_OnButtonClick;
                messageBox.Show();
            }

            SetComplete();
        }

        private void MessageBox_OnButtonClick(DaggerfallMessageBox sender, DaggerfallMessageBox.MessageBoxButtons messageBoxButton)
        {
            // A remote authoritative answer can arrive while this machine still has
            // the same prompt window open. Close that stale window without selecting
            // a second branch.
            try
            {
                if (global::QuestNetSync.HasAppliedPromptChoice(
                        ParentQuest != null ? ParentQuest.UID : 0UL,
                        runtimeOwnerTaskSymbol,
                        id))
                {
                    runtimeMessageBox = null;
                    UnregisterNetworkPrompt();
                    sender.CloseWindow();
                    return;
                }
            }
            catch { }

            // QuestNetSync returns false only for non-owner copies of the two
            // M0B11Y18 letter turn-in flows. Every other prompt keeps vanilla input.
            try
            {
                if (!global::QuestNetSync.CanLocalPlayerAnswerPrompt(
                        ParentQuest != null ? ParentQuest.UID : 0UL,
                        runtimeOwnerTaskSymbol,
                        id))
                    return;
            }
            catch { }

            runtimeMessageBox = null;
            UnregisterNetworkPrompt();

            Symbol selectedTaskSymbol =
                messageBoxButton == DaggerfallMessageBox.MessageBoxButtons.Yes
                    ? yesTaskSymbol
                    : noTaskSymbol;

            // Close the modal window before executing the selected branch. QuestNetSync
            // now runs that task immediately so PlaceItem and the other branch actions
            // cannot remain blocked behind this prompt's pause state.
            sender.CloseWindow();

            // Preserve vanilla/SP behavior locally, then report the exact selected
            // branch to the multiplayer quest synchronizer.
            if (selectedTaskSymbol != null)
            {
                ParentQuest.StartTask(selectedTaskSymbol);

                try
                {
                    global::QuestNetSync.ReportLocalPromptChoice(
                        ParentQuest != null ? ParentQuest.UID : 0UL,
                        runtimeOwnerTaskSymbol,
                        id,
                        selectedTaskSymbol.Name);
                }
                catch { }
            }
        }

        #region Serialization

        [fsObject("v1")]
        public struct SaveData_v1
        {
            public int id;
            public Symbol yesTaskSymbol;
            public Symbol noTaskSymbol;
        }

        public override object GetSaveData()
        {
            SaveData_v1 data = new SaveData_v1();
            data.id = id;
            data.yesTaskSymbol = yesTaskSymbol;
            data.noTaskSymbol = noTaskSymbol;

            return data;
        }

        public override void RestoreSaveData(object dataIn)
        {
            if (dataIn == null)
                return;

            SaveData_v1 data = (SaveData_v1)dataIn;
            id = data.id;
            yesTaskSymbol = data.yesTaskSymbol;
            noTaskSymbol = data.noTaskSymbol;
        }

        #endregion
    }
}
