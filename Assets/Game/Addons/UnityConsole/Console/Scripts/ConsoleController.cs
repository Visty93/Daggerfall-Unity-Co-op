using UnityEngine;
using System;
using System.Linq;
using System.Collections.Generic;


namespace Wenzil.Console
{
    /// <summary>
    /// The behavior of the console.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ConsoleController))]
    public class ConsoleController : MonoBehaviour
    {
        private const int inputHistoryCapacity = 20;

        public ConsoleUI ui;
        public KeyCode toggleKey = KeyCode.BackQuote;
        public bool closeOnEscape = false;
        public string AwakeMessage = "Type Help for list of commands and Help <command> for more details on a specific command";

        private ConsoleInputHistory inputHistory = new ConsoleInputHistory(inputHistoryCapacity);

        void Awake()
        {
            /* This instantiation causes a bug when Unity rebuilds the project while in play mode
               Solution: move it to class level initialization, and make inputHistoryCapacity a const */
            // inputHistory = new ConsoleInputHistory(inputHistoryCapacity);
        }

        void Start()
        {
            DaggerfallWorkshop.Game.InputManager.OnSavedKeyBinds += GetConsoleKeyBind;
        }

        void OnEnable()
        {
            DaggerfallWorkshop.Game.InputManager.OnLoadedKeyBinds += GetConsoleKeyBind;
            Console.OnConsoleLog += ui.AddNewOutputLine;
            ui.onSubmitCommand += ExecuteCommand;
            ui.onClearConsole += inputHistory.Clear;
            ui.AddNewOutputLine(AwakeMessage);
        }

        void OnDisable()
        {
            DaggerfallWorkshop.Game.InputManager.OnLoadedKeyBinds -= GetConsoleKeyBind;
            Console.OnConsoleLog -= ui.AddNewOutputLine;
            ui.onSubmitCommand -= ExecuteCommand;
            ui.onClearConsole -= inputHistory.Clear;
        }

        void Update()
        {
            if (global::OptionsMultiplayer.ClientConsoleCommandsBlocked)
            {
                // Block new commands and undo the reversible persistent cheats
                // if they were enabled before connecting or before a host changed
                // this permission. Do not override normal levitation unless noclip
                // was actually enabled.
                ResetClientConsoleCheats();
                // If the host disables commands while the console is open,
                // close via ToggleConsole so DFU restores the pause state.
                if (ui != null && ui.isConsoleOpen) ui.ToggleConsole(true);
                return;
            }
            if (DaggerfallWorkshop.Game.InputManager.Instance.GetKeyDown(toggleKey))
                ui.ToggleConsole();
            else if (DaggerfallWorkshop.Game.InputManager.Instance.GetBackButtonDown() && closeOnEscape)
                ui.CloseConsole();
            else if (Input.GetKeyDown(KeyCode.UpArrow))
                NavigateInputHistory(true);
            else if (Input.GetKeyDown(KeyCode.DownArrow))
                NavigateInputHistory(false);
        }

        private static void ResetClientConsoleCheats()
        {
            var game = DaggerfallWorkshop.Game.GameManager.Instance;
            if (game == null || game.PlayerEntity == null)
                return;

            // tai disables local game AI, leaving it disabled even after the console
            // can no longer be opened. Reset it as well when the host disallows commands.
            if (game.DisableAI)
                game.DisableAI = false;

            // tgm / nt: local player flags toggled by DFU's console commands.
            if (game.PlayerEntity.GodMode)
                game.PlayerEntity.GodMode = false;
            if (game.PlayerEntity.NoTargetMode)
                game.PlayerEntity.NoTargetMode = false;

            // tcl changes three things: the NoClipMode flag, levitation and
            // the player collision layer. Reverse all three as one operation.
            if (game.PlayerEntity.NoClipMode && game.PlayerMotor != null &&
                game.PlayerController != null)
            {
                var levitate = game.PlayerMotor.GetComponent<DaggerfallWorkshop.Game.LevitateMotor>();
                game.PlayerEntity.NoClipMode = false;
                if (levitate != null) levitate.IsLevitating = false;
                game.PlayerController.gameObject.layer = LayerMask.NameToLayer("Player");
            }
        }

        private void NavigateInputHistory(bool up)
        {
            string navigatedToInput = inputHistory.Navigate(up);
            ui.SetInputText(navigatedToInput);
        }

        private void ExecuteCommand(string input)
        {
            if (global::OptionsMultiplayer.ClientConsoleCommandsBlocked)
            {
                Console.Log("Console commands are disabled by the host.");
                return;
            }
            string[] parts = input.Split(' ');
            string command = parts[0];
            string[] args = parts.Skip(1).ToArray();

            Console.Log("> " + input);
            Console.Log(ConsoleCommandsDatabase.ExecuteCommand(command, args));
            inputHistory.AddNewInputEntry(input);
        }


        public void GetConsoleKeyBind()
        {
            toggleKey = DaggerfallWorkshop.Game.InputManager.Instance.GetBinding(DaggerfallWorkshop.Game.InputManager.Actions.ToggleConsole);
            if (toggleKey == KeyCode.None)
                toggleKey = KeyCode.BackQuote;
        }



    }
}
