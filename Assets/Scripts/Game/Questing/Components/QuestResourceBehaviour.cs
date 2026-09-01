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
using UnityEngine;
using DaggerfallConnect.Save;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.Items;
using FullSerializer;

namespace DaggerfallWorkshop.Game.Questing
{
    /// <summary>
    /// Helper behaviour to pass information between GameObjects and Quest system.
    /// Used to trigger resource events in quest systems like ClickedNpc, InjuredFoe, KilledFoe, etc.
    /// </summary>
    public class QuestResourceBehaviour : MonoBehaviour
    {
        #region Fields

        ulong questUID;
        Symbol targetSymbol;
        bool isFoeDead = false;
        bool restraintApplied = false;
        int foeSpellQueuePosition = 0;
        int foeItemQueuePosition = 0;
        bool isAttackableByAI = false;

        [NonSerialized] Quest targetQuest;
        [NonSerialized] QuestResource targetResource = null;
        [NonSerialized] DaggerfallEntityBehaviour enemyEntityBehaviour = null;

        // Multiplayer: remote/client-requested quest foes are real networked enemies,
        // but the vanilla QuestResource.QuestResourceBehaviour back-reference is global
        // per quest resource. On the listen-host, a host-side quest tick can hot-remove
        // that global behaviour just because the HOST is not at the remote client's site.
        // Keep these QRBs able to process death/injured/item queues locally, but do not
        // advertise them through the shared QuestResource back-reference.
        [NonSerialized] bool suppressQuestResourceBackReference = false;

        #endregion

        #region Structures

        [fsObject("v1")]
        public struct QuestResourceSaveData_v1
        {
            public ulong questUID;
            public Symbol targetSymbol;
            public bool isFoeDead;
            public int foeSpellQueuePosition;
            public int foeItemQueuePosition;
            public bool isAttackableByAI;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets assigned Quest UID.
        /// </summary>
        public ulong QuestUID
        {
            get { return questUID; }
        }

        /// <summary>
        /// Gets assigned target Symbol.
        /// </summary>
        public Symbol TargetSymbol
        {
            get { return targetSymbol; }
        }

        /// <summary>
        /// Flag stating if this Foe is dead .
        /// </summary>
        public bool IsFoeDead
        {
            get { return isFoeDead; }
        }

        /// <summary>
        /// Gets target Quest object. Can return null.
        /// </summary>
        public Quest TargetQuest
        {
            get { return targetQuest; }
        }

        /// <summary>
        /// Gets target QuestResource object. Can return null.
        /// </summary>
        public QuestResource TargetResource
        {
            get { return targetResource; }
        }

        /// <summary>
        /// Gets DaggerfallEntityBehaviour on enemy.
        /// Will be null if not an enemy quest resource.
        /// </summary>
        DaggerfallEntityBehaviour EnemyEntityBehaviour
        {
            get { return enemyEntityBehaviour; }
        }

        /// <summary>
        /// Gets or sets flag allowing enemy resource to be attacked by another mobile AI.
        /// Never set in core. Must be set by a custom quest action.
        /// </summary>
        public bool IsAttackableByAI
        {
            get { return isAttackableByAI; }
            set { isAttackableByAI = value; }
        }

        #endregion

        #region Unity

        private void Start()
        {
            // Cache target resource
            // This will fail if targetQuest and targetSymbol are not set before Start()
            if (!CacheTarget())
                return;
        }

        private void Update()
        {
            // Ensure target resource has this behaviour assigned.
            // Coupling is otherwise lost when reloading a game.
            // MP exception: remote/client-requested network quest foes must not be written
            // into the shared QuestResource.QuestResourceBehaviour slot on the host. That
            // slot is used by vanilla hot-remove when the LOCAL player is not at the site,
            // which would instantly destroy a valid remote player's networked quest foe.
            if (targetResource != null)
            {
                if (!suppressQuestResourceBackReference)
                {
                    if (!targetResource.QuestResourceBehaviour)
                        targetResource.QuestResourceBehaviour = this;
                }
                else if (targetResource.QuestResourceBehaviour == this)
                {
                    targetResource.QuestResourceBehaviour = null;
                    Debug.LogWarning($"[QuestResourceMP] Detached remote/network quest foe back-reference to prevent host-side hot-remove. go='{name}' questUID={questUID} symbol={(targetSymbol != null ? targetSymbol.Name : "<null>")}");
                }
            }

            // Handle NPC checks
            if (targetResource is Person && targetResource.QuestResourceBehaviour)
            {
                // Disable person resource if hidden or destroyed
                // Normally this is done via QuestResource.Tick() but this stops receiving ticks when quest terminates
                // Sometimes a quest person is hidden at same time quest is ended, e.g. $CUREWER when spawning lycanthrope foe
                // Also disabling here to handle this situation
                Person targetPerson = (Person)targetResource;
                if (targetPerson.IsHidden || targetPerson.IsDestroyed)
                    targetPerson.QuestResourceBehaviour.gameObject.SetActive(false);
            }

            // Handle enemy checks
            if (enemyEntityBehaviour)
            {
                // Get foe resource
                Foe foe = (Foe)targetResource;
                if (foe == null)
                    return;

                // If foe is hidden then remove self from game
                if (foe.IsHidden)
                {
                    Destroy(gameObject);
                    return;
                }

                // Process spell and item queues
                CastSpellQueue(foe, enemyEntityBehaviour);
                AddItemQueue(foe, enemyEntityBehaviour);

                // Handle restrained check
                // This might need some tuning in relation to injured and death checks
                if (foe.IsRestrained && !restraintApplied)
                {
                    // Make enemy non-hostile
                    EnemyMotor enemyMotor = transform.GetComponent<EnemyMotor>();
                    if (enemyMotor)
                        enemyMotor.IsHostile = false;

                    restraintApplied = true;
                }

                // Handle injured check
                // This has to happen before death or script actions attached to injured event will not trigger
                if (enemyEntityBehaviour.Entity.CurrentHealth < enemyEntityBehaviour.Entity.MaxHealth && !foe.InjuredTrigger)
                {
                    foe.SetInjured();
                    return;
                }

                // Handle death checks
                if (!isFoeDead && foe.DeathTrigger)
                    enemyEntityBehaviour.Entity.CurrentHealth = 0;

                if (enemyEntityBehaviour.Entity.CurrentHealth <= 0 && !isFoeDead)
                {
                    foe.IncrementKills();
                    isFoeDead = true;
                }
            }
        }

        private void OnDestroy()
        {
            RaiseOnGameObjectDestroyEvent();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Assign this behaviour a QuestResource object.
        /// </summary>
        public void AssignResource(QuestResource questResource)
        {
            if (questResource != null)
            {
                questUID = questResource.ParentQuest.UID;
                targetSymbol = questResource.Symbol;
            }
        }

        /// <summary>
        /// Multiplayer-only: keeps this behaviour bound to quest UID/symbol locally, but prevents
        /// it from occupying QuestResource.QuestResourceBehaviour. That shared back-reference is
        /// local-player-scoped in vanilla quest cleanup and is unsafe for remote/client-requested
        /// network quest foes on a listen-host.
        /// </summary>
        public void SuppressQuestResourceBackReferenceForMultiplayerRemoteFoe(string reason)
        {
            suppressQuestResourceBackReference = true;

            if (targetResource != null && targetResource.QuestResourceBehaviour == this)
                targetResource.QuestResourceBehaviour = null;

            Debug.Log($"[QuestResourceMP] Suppressing QuestResource back-reference for remote/network quest foe. go='{name}' questUID={questUID} symbol={(targetSymbol != null ? targetSymbol.Name : "<null>")} reason={reason}");
        }

        /// <summary>
        /// Called by PlayerActivate when clicking on this GameObject.
        /// </summary>
        /// <returns>True if this resource was found in any active quests.</returns>
        public bool DoClick()
        {
            bool foundInActiveQuest = false;

            // Handle linked resource
            if (targetResource != null)
            {
                try
                {
                    bool traceHidden = false;
                    try { traceHidden = targetResource.GetResourceSaveData().isHidden; } catch { }
                    Debug.LogWarning(
                        $"[MPQuestTrace][QRBClick][Before] go='{gameObject.name}' uid={questUID} " +
                        $"symbol='{(targetSymbol != null ? targetSymbol.Name : "<null>")}' " +
                        $"type='{targetResource.GetType().Name}' activeSelf={gameObject.activeSelf} " +
                        $"activeHierarchy={gameObject.activeInHierarchy} hidden={traceHidden} " +
                        $"server={Mirror.NetworkServer.active} client={Mirror.NetworkClient.active}");
                }
                catch { }

                // Click resource
                targetResource.SetPlayerClicked();

                // If this an item then transfer item to player and hide resource
                if (targetResource is Item)
                    TransferWorldItemToPlayer();

                try
                {
                    bool traceHidden = false;
                    try { traceHidden = targetResource.GetResourceSaveData().isHidden; } catch { }
                    Debug.LogWarning(
                        $"[MPQuestTrace][QRBClick][After] go='{gameObject.name}' uid={questUID} " +
                        $"symbol='{(targetSymbol != null ? targetSymbol.Name : "<null>")}' " +
                        $"type='{targetResource.GetType().Name}' activeSelf={gameObject.activeSelf} " +
                        $"activeHierarchy={gameObject.activeInHierarchy} hidden={traceHidden} " +
                        $"server={Mirror.NetworkServer.active} client={Mirror.NetworkClient.active}");
                }
                catch { }

                foundInActiveQuest = true;
            }

            // Possible for NPC to start a direct follow-up quest and new quest needs a bootstrap click
            // But if resource is still associated with old quest from previous layout then click never sent to new quest
            // So if behaviour is peered with an individual StaticNPC then send click to all quests using this NPC
            // This allows new quest to receive click and NPC will be re-linked on next layout or by "add NPC as questor"
            StaticNPC npc = GetComponent<StaticNPC>();
            if (npc)
            {
                int factionID = npc.Data.factionID;
                if (QuestMachine.Instance.IsIndividualNPC(factionID))
                    foundInActiveQuest = ClickAllIndividualNPCs(factionID);
            }

            return foundInActiveQuest;
        }

        /// <summary>
        /// Gets save data for serialization.
        /// </summary>
        public QuestResourceSaveData_v1 GetSaveData()
        {
            QuestResourceSaveData_v1 data = new QuestResourceSaveData_v1();
            data.questUID = questUID;
            data.targetSymbol = targetSymbol;
            data.isFoeDead = isFoeDead;
            data.foeSpellQueuePosition = foeSpellQueuePosition;
            data.foeItemQueuePosition = foeItemQueuePosition;
            data.isAttackableByAI = isAttackableByAI;

            return data;
        }

        /// <summary>
        /// Restores deserialized save data.
        /// Must be called after quest system state restored.
        /// </summary>
        public void RestoreSaveData(QuestResourceSaveData_v1 data)
        {
            questUID = data.questUID;
            targetSymbol = data.targetSymbol;
            isFoeDead = data.isFoeDead;
            foeSpellQueuePosition = data.foeSpellQueuePosition;
            foeItemQueuePosition = data.foeItemQueuePosition;
            isAttackableByAI = data.isAttackableByAI;
            CacheTarget();
        }

        public void CastSpellQueue(Foe foe, DaggerfallEntityBehaviour enemyEntityBehaviour)
        {
            // Validate
            if (!enemyEntityBehaviour || foe == null || foe.SpellQueue == null || foeSpellQueuePosition == foe.SpellQueue.Count)
                return;

            // Target entity must be alive
            if (enemyEntityBehaviour.Entity.CurrentHealth == 0)
                return;

            // Get effect manager on enemy
            EntityEffectManager enemyEffectManager = enemyEntityBehaviour.GetComponent<EntityEffectManager>();
            if (!enemyEffectManager)
                return;

            // Cast queued spells on foe from current position
            for (int i = foeSpellQueuePosition; i < foe.SpellQueue.Count; i++)
            {
                SpellReference spell = foe.SpellQueue[i];
                EntityEffectBundle spellBundle = null;

                // Create classic or custom spell bundle
                if (string.IsNullOrEmpty(spell.CustomKey))
                {
                    // Get classic spell data
                    SpellRecord.SpellRecordData spellData;
                    if (!GameManager.Instance.EntityEffectBroker.GetClassicSpellRecord(spell.ClassicID, out spellData))
                        continue;

                    // Create classic spell bundle settings
                    EffectBundleSettings bundleSettings;
                    if (!GameManager.Instance.EntityEffectBroker.ClassicSpellRecordDataToEffectBundleSettings(spellData, BundleTypes.Spell, out bundleSettings))
                        continue;

                    // Create classic spell bundle
                    spellBundle = new EntityEffectBundle(bundleSettings, enemyEntityBehaviour);
                }
                else
                {
                    // Create custom spell bundle - must be previously registered to broker
                    try
                    {
                        EntityEffectBroker.CustomSpellBundleOffer offer = GameManager.Instance.EntityEffectBroker.GetCustomSpellBundleOffer(spell.CustomKey);
                        spellBundle = new EntityEffectBundle(offer.BundleSetttings, enemyEntityBehaviour);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogErrorFormat("QuestResourceBehaviour.CastSpellQueue() could not find custom spell offer with key: {0}, exception: {1}", spell.CustomKey, ex.Message);
                    }
                }

                // Assign spell bundle to enemy
                if (spellBundle != null)
                    enemyEffectManager.AssignBundle(spellBundle, AssignBundleFlags.BypassSavingThrows);
            }

            // Set index positon to end of queue
            foeSpellQueuePosition = foe.SpellQueue.Count;
        }

        public void AddItemQueue(Foe foe, DaggerfallEntityBehaviour enemyEntityBehaviour)
        {
            // Validate
            if (!enemyEntityBehaviour || foe == null || foe.ItemQueueCount == 0 || foeItemQueuePosition == foe.ItemQueueCount)
                return;

            // Get item queue as cloned items with new UIDs
            DaggerfallUnityItem[] clonedItems = foe.GetClonedItemQueue();

            // Assign all items for player to find
            //  * Some quests assign item to Foe at create time, others on injured event
            //  * It's possible for target enemy to be one-shot or to be killed by other means (such as "killall")
            //  * This assignment will direct quest loot item either to live enemy or corpse loot container
            if (enemyEntityBehaviour.CorpseLootContainer)
            {
                // If enemy is already dead then place item in corpse loot container
                enemyEntityBehaviour.CorpseLootContainer.Items.AddItems(clonedItems);
            }
            else
            {
                // Otherwise add quest Item to Entity item collection
                // It will be transferred to corpse marker loot container when dropped
                enemyEntityBehaviour.Entity.Items.AddItems(clonedItems);
            }

            // Set index position to end of queue
            foeItemQueuePosition = foe.ItemQueueCount;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Cache target quest and resource objects.
        /// If true then TargetQuest and TargetResource objects are cached and available.
        /// </summary>
        bool CacheTarget()
        {
            // Check already cached
            if (targetQuest != null && targetResource != null)
                return true;

            // Must have a questUID and targetSymbol
            if (questUID == 0 || targetSymbol == null)
                return false;

            // Get the quest this resource belongs to
            targetQuest = QuestMachine.Instance.GetQuest(questUID);
            if (targetQuest == null)
                return false;

            // Get the resource from quest
            targetResource = targetQuest.GetResource(targetSymbol);
            if (targetResource == null)
                return false;

            // Cache local EnemyEntity behaviour if resource is a Foe
            if (targetResource != null && targetResource is Foe)
                enemyEntityBehaviour = gameObject.GetComponent<DaggerfallEntityBehaviour>();

            return true;
        }

        void TransferWorldItemToPlayer()
        {
            Item item = (Item)targetResource;
            if (item == null || item.DaggerfallUnityItem == null)
                return;

            bool multiplayerActive = false;
            try
            {
                multiplayerActive = Mirror.NetworkClient.active || Mirror.NetworkServer.active;
            }
            catch { }

            DaggerfallUnityItem inventoryItem;

            if (!multiplayerActive)
            {
                // Preserve vanilla DFU identity in single-player. MakePermanent(), HaveItem,
                // and other quest actions operate on Item.DaggerfallUnityItem, so the exact
                // same object must be the one carried by the player.
                inventoryItem = item.DaggerfallUnityItem;
            }
            else
            {
                // Multiplayer-safe pickup: keep the existing clean-copy workaround for
                // network-reconstructed placed items, but immediately rebind the virtual
                // quest Item to that carried copy. Without this rebind, later vanilla
                // MakePermanent() modifies the old prototype while inventory stays green.
                try
                {
                    inventoryItem = new DaggerfallUnityItem(item.DaggerfallUnityItem.GetSaveData());
                }
                catch
                {
                    inventoryItem = item.DaggerfallUnityItem;
                }

                if (inventoryItem == null)
                    return;

                try
                {
                    if (!inventoryItem.IsOfTemplate(ItemGroups.Currency, (int)Currency.Gold_pieces))
                        inventoryItem.stackCount = Mathf.Max(1, inventoryItem.stackCount);
                }
                catch
                {
                    inventoryItem.stackCount = Mathf.Max(1, inventoryItem.stackCount);
                }

                try
                {
                    if (item.ParentQuest != null && item.Symbol != null)
                        inventoryItem.LinkQuestItem(item.ParentQuest.UID, item.Symbol.Clone());
                }
                catch { }

                // Critical identity repair: from this point onward the quest resource and
                // player inventory refer to the same physical item, just like vanilla SP.
                item.RebindDaggerfallUnityItem(inventoryItem);
            }

            if (inventoryItem == null)
                return;

            // Give item to player
            GameManager.Instance.PlayerEntity.Items.AddItem(inventoryItem, ItemCollection.AddPosition.Front);

            // Multiplayer: remember the exact physical object that entered the local
            // inventory. This is the world-pickup equivalent of GivePc's rewardLoot
            // reference and lets MakePermanent operate on the real carried object even
            // if later quest/resource snapshots rebuild or rebind their prototype.
            if (multiplayerActive)
            {
                try
                {
                    if (item.ParentQuest != null && item.Symbol != null && !string.IsNullOrEmpty(item.Symbol.Name))
                    {
                        global::QuestNetSync.RegisterLocalPhysicalQuestInventoryItem(
                            item.ParentQuest.UID,
                            item.Symbol.Name,
                            inventoryItem);
                    }
                }
                catch { }
            }

            // Hide item so player cannot pickup again
            // This will cause it not to display in world again despite being placed by SiteLink
            item.IsHidden = true;

            try
            {
                Debug.LogWarning(
                    $"[MPQuestTrace][WorldItemPickup] uid={(item.ParentQuest != null ? item.ParentQuest.UID : 0UL)} " +
                    $"quest='{(item.ParentQuest != null ? item.ParentQuest.QuestName : "<null>")}' " +
                    $"symbol='{(item.Symbol != null ? item.Symbol.Name : "<null>")}' " +
                    $"go='{gameObject.name}' itemHidden={item.IsHidden} activeSelf={gameObject.activeSelf} " +
                    $"activeHierarchy={gameObject.activeInHierarchy} qrbBackrefIsThis={object.ReferenceEquals(item.QuestResourceBehaviour, this)} " +
                    $"server={Mirror.NetworkServer.active} client={Mirror.NetworkClient.active}");
            }
            catch { }

            // Multiplayer: placed quest items such as Defamation's _letter_ and Rare Book's
            // _book_ do not run through GetItem. This is the real pickup path used by
            // PlaceItem-created world items, so report the exact quest UID + item symbol here.
            if (multiplayerActive)
            {
                try
                {
                    if (item.ParentQuest != null && item.Symbol != null && !string.IsNullOrEmpty(item.Symbol.Name))
                        global::QuestNetSync.ReportLocalQuestItemInventoryChanged(item.ParentQuest.UID, item.Symbol.Name, true, inventoryItem);
                }
                catch { }
            }
        }

        bool ClickAllIndividualNPCs(int factionID)
        {
            // Check active quests to see if any are using this NPC
            ulong[] questIDs = QuestMachine.Instance.GetAllActiveQuests();
            bool matched = false;
            foreach (ulong questID in questIDs)
            {
                // Get quest object
                Quest quest = QuestMachine.Instance.GetQuest(questID);
                if (quest == null)
                    continue;

                // Get all the Person resources in this quest
                QuestResource[] personResources = quest.GetAllResources(typeof(Person));
                if (personResources == null || personResources.Length == 0)
                    continue;

                // Check each Person for a match
                foreach (QuestResource resource in personResources)
                {
                    // Set click if individual matches Person factionID
                    Person person = (Person)resource;
                    if (person.IsIndividualNPC && person.FactionData.id == factionID)
                    {
                        person.SetPlayerClicked();
                        matched = true;
                    }
                }
            }

            return matched;
        }

        #endregion

        #region Events

        public delegate void OnGameObjectDestroyHandler(QuestResourceBehaviour questResourceBehaviour);
        public event OnGameObjectDestroyHandler OnGameObjectDestroy;
        protected void RaiseOnGameObjectDestroyEvent()
        {
            if (OnGameObjectDestroy != null)
                OnGameObjectDestroy(this);
        }

        #endregion
    }
}