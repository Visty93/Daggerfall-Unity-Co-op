# Daggerfall Untiy Co-op

This is a heavily modified version of [DFU-Tanguy-Multiplayer](https://github.com/EmptyBottleInc/DFU-Tanguy-Multiplayer), which was based on an older, 0.14.5 version of [Daggerfall Unity](https://github.com/Interkarma/daggerfall-unity). This version has been moved to DFU 1.1.1 and heavily modifies both Tanguy's original files and actual DFU files to support newly added features.

## Contents

- [Disclaimer](#disclaimer)
- [Differences/features](#differencesfeatures)
- [Save compatibility](#save-compatibility)
- [Issues / limitations](#issues--limitations)
- [Mod support](#mod-support)
- [Technical details](#technical-details)

---

## Disclaimer

All new code and modifications in this version were written by ChatGPT. I know there are people who hate AI-related stuff, so I'll leave this here. I can't code, and because of that, I have limited code-reading ability. I mostly did playtesting and "designed" certain systems. It took me 1.5 years. The earliest code was written by the ChatGPT 4 non-thinking model, while later code was written using newer models, up to the 5.6 thinking model.

The difference in their capabilities is huge, so code quality is probably all over the place. There might be scripts that are not in use, and there might be blocks of code that are commented out or never called (past experiments).

Codex was only used to find DFU features in the codebase, figure out how they work, and make a plan for the MP implementation of said features. Then the plan was given to ChatGPT to actually implement it. Codex did a good job collecting and planning stuff, but I didn't use it for coding because most of the time it was actually worse than what ChatGPT gave me. (Also, I'm still not very comfortable using GitHub.)

Spawning a large number of enemies (for example, generating a large dungeon with 100+ enemies) currently causes a few seconds of performance degradation, so I recommend using small dungeons. When playing with large dungeons, I do not recommend having more than 2 active large dungeons at the same time. Hundreds of enemies can actually cause performance issues, especially if one player owns all of them.

Also, this was the case in Tanguy's version too, but all players have to use the same dungeon settings (small/large dungeons), or you might have desyncs.

Random wandering NPC sync was added pretty late, just to improve immersion. Performance-wise, they aren't a problem, and their network traffic is significantly lower than it was in the earlier prototypes, but it is still not quite optimal. By default, it is ON, but some people might still find it too much, so I added an option to the MP settings that allows the host to turn it off.

I tried to keep the SP part of the game untouched in every way, so it **SHOULD** play like your normal DFU 1.1.1 (May 9, 2024 release) version, but there might be things that were accidentally edited and I forgot to restore.

> **I suggest saving often, but loading rarely.** With the respawn system, loading shouldn't be needed too often anyway.

It was designed for co-operative gameplay. Quests will be shared with everybody, but players can freely do their own thing. There is no need to be together all the time, even though everything is synced.

---

## Differences/features

### 1) Based on DFU 1.1.1

Built with Unity 2019.4.41f2, including the Unity security patch.

### 2) Completely re-made enemy sync system

Enemies are now proper networked objects, just like players were:

- There should be much less desync.
- Better movement sync.
- Animation sync (movement, ranged attacks, hits).
- Sound sync.
- Current/Max Health sync.
- Enemies can properly target and switch targets between players. In MP, they target `PlayerMultiplayer` GameObjects instead of the SP `PlayerAdvanced` GameObject.
- Enemy loot is synced.
- QuestFoes work correctly.
- Every single enemy is spawned by the host. This does not mean the host "owns" all of them, there is a dynamic authority system behind it.

### 3) Quest sync

Every newly taken quest gets shared with everybody present in the game. If one player progresses a quest, it progresses for everyone, regardless of where the other players are. This includes quest completion and rewards.

Previously taken quests are not synced by default, but they can be shared from the journal with the **"[Share Quest]"** button.

Since the host spawns all enemies, and right now the host can only spawn enemies with a quest script attached if the host has that quest too, if a client wants to spawn a quest enemy from a quest taken in SP (so not shared/in sync), the quest will automatically be shared with the host at the time of the enemy spawn.

Every player spawns (or requests the spawn of) their own quest enemy, so if multiple players are doing the same quest, there will be multiple quest enemies. Killing only one of them is enough to complete the quest, though. Since quests can spawn multiple enemies even in SP, it wouldn't be wise to limit the number of enemies to 1 per quest. This might look silly when the quest foe is a named character, but until I find a better solution, that's how it is.

**Quests that are deliberately blacklisted (not synced) from the quest system:**

The Dark Brotherhood and Thieves Guild intro quests are blacklisted since finishing them would auto-join everyone, so each player has to do them individually. The quests to cure Vampirism/Lycanthropy are also blacklisted.

**Tested quests:**

- All the main quests with 3 players.
- All Dark Brotherhood quests (except the intro quest) with 2 players.
- All Merchant quests with 2 or 3 players.
- All Noble quests with 2 or 3 players.
- All Temple quests with 2 or 3 players.
- All Knightly Orders quests with 2-3 players.
- All Fighters Guild quests with 3 players.
- All Mages Guild quests with 3 players.
- All Thieves Guild quests with 3 players.
- 3 Vampire quests with 3 players.
- 2 Witch Coven quests with 3 players.
- 1 Daedric quest with 3 players. It syncs from the moment the quest is added to the journal, so the animated dialogue is only shown to the player who takes the quest.

*(In tests with 3 players, the 3rd player always progressed the quest passively while staying in some random town the whole time.)*
Vampire quests were only tested briefly with all 3 players having the same vampire bloodline, so they might need some more testing.
Letters can arrive at different times for each player, which can be especially annoying during vampire/the main quests.

The quest system is basically one big 19k-line file full of spaghetti code, with some edits in the original quest files. It was developed alongside testing over the past 8 months or so, and we didn't re-test every single quest after every change to see what we might have broken.

If anyone finds a bug with any quest, please report it with as much detail as possible.

### 4) Random Town NPC sync

If 2 players are in different towns, everyone has their own set of mobile NPCs.

If 2 or more players are in the same town, the NPCs are divided between each player, so each player "owns" a portion of them. Everyone can interact with or kill every player's NPCs. If you're interested in more technical reasons, read below.

### 5) Loot sync

Enemy drops are now synced, including quest items. Random containers in the world are **NOT** synced and are not planned to be synced.

### 6) Player down / Respawn system

If the player dies in MP, the game no longer gives you the end-game cutscene. Instead, you will be "downed". The camera will go black, and other players have 30 seconds to click on you to bring you back with 30% health.

If the 30 seconds pass, or you click the left mouse button during that time, you will be respawned at different locations with 100% HP, depending on where you died.

- If you died in a dungeon, you will be respawned inside at the entrance of the dungeon. The original idea was to respawn outside, but if there are no players inside a dungeon for 10+ seconds, the dungeon ceases to exist. To make it easier to keep the dungeon alive, you will instead respawn at the entrance inside the dungeon.
- If you died outside a dungeon, you will respawn at the fast-travel point.
- If you died in a town or inside an interior, you will be respawned inside the closest local temple, except if you are a vampire. If there is no temple in the town, you will respawn in one of the nearest tavern's rooms, which is also the default for vampires. If there are no taverns either, you will respawn at the town's fast-travel location.
- If you died in the exterior wilderness, you will respawn at the latest location you visited, such as a dungeon entrance or town.

### 7) Party window

All connected players' names, faces, health, locations, and stat bars are visible in the top-right corner, or you can access slightly more detailed information in the Party window.

The Party window can be accessed from the menu or the chat box.

It shows all players' faces, names, magic, fatigue, exact current locations, classes, and levels.

Clicking on a location in the Party window will initiate fast travel to the other player.

- If the other player is in the exterior wilderness or a town, it will travel to their exact location.
- If the other player is inside an interior, it will take you to the entrance of that interior.
- If the other player is inside a dungeon, it will take you to the exact location of that player inside the dungeon.

### 8) Limited player interactions

Although the players are still just "shells" representing the other players' SP versions, they are interactive enough that you can use certain spells on them. There is player collision and player health/magic/fatigue sync.

Currently, only "positive spells", such as healing, curing, and similar effects, work on other players. Also, between players, they have a 100% chance to work (no save vs. spell).

To prevent friendly fire, all damaging spells are disabled. Spells simply send spell "effect bundles" to the other player's MP shell, which then get forwarded to their own SP character, where everything is calculated locally.

Even though health is always visible in the top-right corner, if you plan to use healing, I still recommend using some kind of health bar mod.

[NPC Health Indicators](https://www.nexusmods.com/daggerfallunity/mods/63) was used extensively during testing for both enemy and player health sync, and it works great.

#### No PvP

Magic-related PvP would already be possible with those "effect bundles" and a friendly-fire ON/OFF toggle, but it wouldn't make much sense without proper melee/bow PvP as well.

To be honest, I have no idea what the actual damage calculations should look like for melee/bows between player characters. Probably a lot more aspects of the player characters would need to be synced to make a proper PvP experience.

### 9) Chat window

Just a basic chat window. Whatever you type is also shown as a notification at the top. You can use it with the Enter key.

If you use the game with classic controls (mouse clicking), turn off the **"Enter opens chat"** toggle on the chat UI.

The Party button opens the Party window.

### 10) Smaller changes / bug fixes

- Much stricter time sync, especially when connecting, loading saves, or travelling.
- If the host controls the time, the client's time no longer passes during travel. This was introduced to prevent quest failures due to time limits. In the old system, if a quest's time limit was 10 days and the host travelled for 6 days, the host would sync the time. Then, if the client followed with another 6-day trip, the client's local time would advance before syncing back to the host's time. By that point, it was already too late because the quest had failed for everyone.
- If players are in the same town, locations marked on the map by NPCs are synced.
- Interior/dungeon minimaps, the exterior minimap, and the world map now mark the other players.
- The original LootCatcher system (dropping an item on the ground for another player in the form of a container, with all items inside it) was changed to use real-time sync instead of simply spawning/deleting the whole thing, and now works the same way as the enemy loot sync.
- Fixed an issue where, during certain weather conditions such as rain, the skybox changed every few seconds for clients.
- If it is snowing for the host, weather sync no longer gives snow to clients who are in a desert biome. It uses rain instead.
- Fixed a bug where reaching the border of a terrain tile caused disconnects.

### 11) Alternative KCP transport

Although the mod is still intended to be used through Steam, I made the alternative KCP transport option available in the build for those who want to use LAN/direct connections without Steam.

However, I won't be able to provide support for KCP-related networking issues such as port forwarding, Hamachi, or similar setups. Just use it for LAN, or better yet, use the Steam networking option.

---

## Save compatibility

Regular DFU saves should generally work when loaded in this co-op fork.

Going the other way is a bit trickier. Exterior saves made with the co-op fork should generally work in a normal/non-co-op DFU version. However, interiors and dungeons are moved underground in the co-op version, so loading a co-op save made inside an interior or dungeon in normal DFU will place the player in the void, as the normal version has no knowledge of those altered positions.

See the **Issues / limitations** section below for more details about joining an MP game from inside an SP dungeon, or loading SP and MP dungeon saves.


## Issues / limitations

- Since players' in-game dates and times can be vastly different, syncing time to the host after joining the game, or after loading a save with too large a time difference, can cause quests to fail for the client. If both the host and client want to load an older save, I recommend that the host loads first. Otherwise, the client can sync their time to the host before the host loads, causing quest failure, and the quest system could then sync that quest failure back to the host too.
- Joining an MP game will destroy all non-MP enemies in the scene **(Intended)**.
- Leaving an MP game will destroy all MP enemies, along with all other networked objects, players, dungeons, etc. **(Intended)**
- Leaving an MP game will kick you out of a dungeon because the dungeon is a networked object and gets destroyed. **(Intended)**
- Joining an MP game while inside an SP dungeon will transform that dungeon into an MP version, with respawned enemies. If someone has already created that dungeon, it will simply move your player to the equivalent position in the existing dungeon. The exact same behaviour applies if you load a save made inside a dungeon.
- Only the owner of an enemy can push enemies back, such as during melee hand-to-hand combat.
- In the capital cities, palaces are considered dungeons by the respawn system, so dying there will respawn you at the fast-travel point.

Enemy respawning is intentional because the MP/SP dungeon position differences during loading are already complicated enough to make work correctly. The player's position will be correct, and the state of switches and other things should be what it was in the save.

If another player has already generated that dungeon, it won't respawn any new enemies. It will basically just move the loading player to their saved position and should use the dungeon creator's state of switches.

- You can load an MP-saved dungeon in SP. It will basically re-create it as an SP version and respawn SP enemies, while keeping the dungeon's original MP position.
- Loading an SP interior with a QuestFoe present will respawn that enemy as an MP enemy.
- As mentioned above, quest enemies spawn for each player.

> **Again:**  
> "I suggest saving often, but loading rarely. With the respawn system, loading shouldn't be needed too often anyway."

---

## Mod support

Not really tested.

I wanted to make the "Transparent Window" mod work with the new interior positions, so I made a script that checks whether the mod is installed and, if it is, creates a fake exterior world around the new interior positions.

However, it is only a fake exterior, so no NPCs or enemies are actually visible through the window.

Also, that mod caused issues for me in a few instances, such as falling through the exterior world after the player was teleported. So overall, I do not recommend using it.

---

## Technical details

<details>
<summary><strong>More technical stuff, in case someone is interested in how it works under the hood in Unity</strong></summary>

### Notable changes and how they work in the background

- Due to network limitations, enemies are no longer children of Dungeon/Interior/Exterior objects. They are **ALWAYS** in the root scene of Unity.
- Disabling a networked GameObject makes the host lose access to it, making the host unable to reactivate or even destroy the object later during cleanup. This creates many opportunities for desync. Because of this, networked GameObjects should not be disabled in MP, which is also why both enemies and dungeons are kept in the root scene. When enemies die, they are destroyed through network commands instead.

This means the wave-spawned enemy spawning mechanism also had to be modified in MP, because SP tends to disable them until it finds a valid position for them to spawn.

Enemies have DF world coordinates calculated from the requester player's DF world coordinates.

There is a custom distance-based authority-changing script. This changes which player owns and controls an enemy.

This is important because if 2 players are at 2 different points in the world, and the host were to spawn and own a client's enemy without having that enemy's actual terrain loaded, the enemy could fall through the ground into the "void" for the client.

This was a serious issue, was a real pain to solve, and came back multiple times during the past 1.5 years.

The same script also handles deactivating the AI/gravity of distant enemies, as well as cleanup. In most cases, the script uses DF world coordinates, with a mix of Unity height.

### Stages

1. The closest player owns the spawned enemy. In some cases, the requester owns it instead.
2. If the owner moves 100+ m away from the enemy, the script tries to transfer authority to the next closest player.
3. If there are no other players within 100m, it gives authority back to the host. When the host owns the enemy and the closest player is 100m+ away in DF coordinates, it freezes the enemy AI and gravity so it won't start falling into the void.
4. If there are no players within 400m, it starts a 10-second countdown. After that, it destroys the enemy.

If there are no players within 200m in Unity height, it also starts destroying the enemy. This was added to help clean up dungeon/interior enemies.

### Interiors

- All interiors are moved 250m beneath their original positions. Since enemies are no longer children of the interior, they aren't disabled when the player leaves. With the original interior coordinates, they could clip or move through the walls into the exterior after the player left. Disabling them is not an option (see above).
- Moving them down also helps with enemy cleanup.
- The interiors themselves are not networked.

### Dungeons

All dungeons are networked objects. This is partly due to earlier experiments, but also because dungeon enemies work a little differently.

Normally, enemies get their DF world coordinates calculated from the requester player's DF world coordinates. However, dungeon enemies' DF world coordinates are always tied to the dungeon's entrance coordinates. The way this is calculated can differ depending on whether the player manually enters, gets teleported, or loads that dungeon.

Since the player's DF coordinates don't change while inside a dungeon, even in single-player, and MP enemies normally use DF coordinates for targeting, dungeon enemies instead use Unity coordinates for targeting, like they do in SP.

- In SP, every dungeon spawns at 0/0/0 coordinates. In multiplayer, dungeons start spawning at -500 height so other players in the world won't see a floating dungeon in the middle of nowhere. Every additional existing dungeon then has another -300 height offset. So: -500, -800, -1100, etc.
- Since dungeons are networked, this also prevents duplicate monster spawns if multiple players enter the same dungeon.
- Dungeons are destroyed if there are no players inside for 10 seconds.

### Details about Random Town NPC sync

In DFU, NPCs spawn around the player in the town block the player is currently in. If the player moves to a different block, they despawn from the previous block and respawn in the next one.

Shared ownership of NPCs was introduced to prevent a situation where a single player owns all of the NPCs, making them appear only in a single block of the city.

The other solution would have been to spawn the full number of NPCs that would normally appear for each individual player. However, that wasn't a very network-traffic-friendly option, and even with just 3 players, the number of NPCs became ridiculous.

So currently, if a town has 96 NPCs and there are 3 players, each of them will own 32. With 8 players, each will own 12, and so on.

This also means that if a lot of players are in the same town and all of them are in different blocks of the city, the number of NPCs in each individual block can actually become quite sparse.

### Other stuff

Most of the time, guards work fine, but there were a few cases where they spawned infinitely every frame. I never really figured out why, so as a workaround, there is a limit on the number of guards per player in MP.

Wave-spawned enemies, for example during the infested house quest, work a little differently in MP, especially for clients. It was a pain to make this work. For clients, although it is rare, enemies can still sometimes spawn stuck inside a wall.

At one point they could spawn outside the interior and fall into the void, but that is supposed to be fixed. If for some reason they don't want to spawn correctly, I suggest moving around a little inside until they start spawning. Quests no longer fail if quest enemies get destroyed (deleted), such as when they spawn outside the house, fall, and get cleaned up. So even if something goes wrong, the quest should still be finishable.

There are also smaller tweaks. For example, the floating origin was greatly expanded because the original amount caused issues when too many dungeons existed at the same time.

Also, after jail time, enemies are **NOT** automatically deleted because doing so caused enemy desyncs and even made other players invisible.

</details> 
