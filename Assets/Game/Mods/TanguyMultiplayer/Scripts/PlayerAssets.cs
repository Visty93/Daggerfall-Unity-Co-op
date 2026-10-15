using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.MagicAndEffects;
using System;
using TMPro;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects;


public class PlayerAssets : NetworkBehaviour
{
    [Serializable]
    public struct Asset{
        public string Name;
        public List<string> Jobs;
        public int Gender;
        public List<Sprite> idleSprites;
        public int walkCount;
        public List<Sprite> walkSprites;
        public int attackCount;
        public List<Sprite> attackSprites;
    }


    public List<Asset> assets;

    [SyncVar]
    public int gender;
    [SyncVar]
    public string job;
    [SyncVar]
    public string playerName;

    // Party-HUD portrait identity. faceArchive is synced directly so custom/modded
    // race templates can provide their own paper-doll head archive without the HUD
    // having to hard-code only the eight vanilla race classes.
    [SyncVar]
    public int faceIndex;
    [SyncVar]
    public int race;
    [SyncVar]
    public string faceArchive;

    // Cosmetic multiplayer mirror of DFU's local TransportManager state.
    // This means "currently riding a horse", not merely "owns a horse".
    [SyncVar(hook = nameof(OnHorseMountedChanged))]
    public bool horseMounted;

    // Cosmetic-only remote presentation of DFU's real local lycanthrope transformation.
    // 0 = normal, 1 = werewolf, 2 = wereboar.
    [SyncVar(hook = nameof(OnLycanthropeVisualStateChanged))]
    public int lycanthropeVisualState = 0;

    // Presentation-only crouch flag for aligning remote paperdoll feet. The DFU
    // PlayerHeightChanger shifts the local player root by half the height change
    // (1.8 -> 0.9 => -0.45), but the remote proxy collider stays unchanged.
    // Do not modify remote collider height, network position, or gameplay state.
    [SyncVar]
    public bool paperdollCrouching;


    public static int hostLevel = 0;

    Asset actualAsset;

    public TextMeshPro nameText;
    public GameObject torch;
    public SpriteMultiplayer spriteMultiplayer;
    DaggerfallEntity pEntity;

    private int lastSentGender = -1;
    private string lastSentJob = string.Empty;
    private string lastSentPlayerName = string.Empty;
    private int lastSentFaceIndex = -1;
    private int lastSentRace = -1;
    private string lastSentFaceArchive = string.Empty;
    private int lastSentLevel = -1;
    private float nextProfileCheckTime = 0f;
    private const float PROFILE_CHECK_INTERVAL = 0.50f;

    private bool lastSentHorseMounted = false;
    private bool hasSentHorseMounted = false;
    private float nextHorseStateCheckTime = 0f;
    private const float HORSE_STATE_CHECK_INTERVAL = 0.10f;

    private int lastSentLycanthropeVisualState = -1;
    private bool hasSentLycanthropeVisualState = false;
    private float nextLycanthropeStateCheckTime = 0f;
    private const float LYCANTHROPE_STATE_CHECK_INTERVAL = 0.10f;

    private bool lastSentPaperdollCrouching;
    private bool hasSentPaperdollCrouching;
    private float nextPaperdollCrouchCheckTime;
    private const float PAPERDOLL_CROUCH_CHECK_INTERVAL = 0.10f;

    // Character loading can briefly expose DFU placeholder data such as Nameless/Mage.
    // Do not publish a new MP profile until loading has settled and the same profile has
    // been observed twice. This prevents remote players from seeing a temporary wrong
    // sprite/name during F9/load, even when the loaded character is actually unchanged.
    private float localProfileHoldUntilRealtime = 0f;
    private int pendingGender = -999;
    private string pendingJob = null;
    private string pendingPlayerName = null;
    private int pendingFaceIndex = -999;
    private int pendingRace = -999;
    private string pendingFaceArchive = null;
    private int pendingLevel = -999;
    private int pendingStableSamples = 0;
    private const float LOCAL_PROFILE_LOAD_SETTLE_SECONDS = 1.75f;
    private const int LOCAL_PROFILE_STABLE_SAMPLE_COUNT = 2;

    private int lastAppliedGender = -999;
    private string lastAppliedJob = null;
    private string lastAppliedPlayerName = null;
    private int lastAppliedFaceIndex = -999;
    private int lastAppliedRace = -999;
    private string lastAppliedFaceArchive = null;

    // Remote SyncVars can also arrive in short transitional states. Require a short
    // stable window before rebuilding the visual, and ignore obvious placeholder names.
    private int observedRemoteGender = -999;
    private string observedRemoteJob = null;
    private string observedRemotePlayerName = null;
    private int observedRemoteFaceIndex = -999;
    private int observedRemoteRace = -999;
    private string observedRemoteFaceArchive = null;
    private float observedRemoteProfileSinceRealtime = 0f;
    private const float REMOTE_PROFILE_STABLE_SECONDS = 0.35f;


    void Start()
    {
        if (isLocalPlayer)
            init();
        else
            Invoke("initProperAsset", 0.5f); //Delaying assets finding because sometimes the SyncVar aren't already set on start
    }

    void Update()
    {
        if (isLocalPlayer)
        {
            TrackLocalCharacterProfile();
            TrackLocalHorseMountedState();
            TrackLocalLycanthropeVisualState();
            TrackLocalPaperdollCrouch();
            TickLocalPaperdoll();
        }
        else
        {
            RefreshRemoteProfileIfChanged();
            TickRemotePaperdoll();
        }
    }

    void init()
    {
        TrackLocalCharacterProfile(true);
        TrackLocalHorseMountedState(true);
        TrackLocalLycanthropeVisualState(true);
        TrackLocalPaperdollCrouch(true);

        if (isServer){
            StartCoroutine(TrackLevel());
        }else
            cmdApplyHostLevel();

        if (isLocalPlayer)
            StartCoroutine(trackTorch());
        /*if (!OptionsMultiplayer.useHighestLevel){

        }else{
            if (isLocalPlayer)
                rpcSendHostLevel(pEntity.Level);
        }*/
    }

    private bool IsSaveLoadInProgressNow()
    {
        try
        {
            return SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress;
        }
        catch { return false; }
    }

    private bool IsObviousPlaceholderProfile(string currentJob, string currentName)
    {
        if (string.IsNullOrEmpty(currentJob) || string.IsNullOrEmpty(currentName))
            return true;

        // DFU can expose this while a save is still settling. Do not sync it as a real
        // MP character identity. If somebody actually names a character Nameless, this
        // can be relaxed later with a longer settle rule.
        if (string.Equals(currentName, "Nameless", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private bool HasStableLocalProfileCandidate(
        int currentGender, string currentJob, string currentName,
        int currentFaceIndex, int currentRace, string currentFaceArchive,
        int currentLevel)
    {
        if (currentGender == pendingGender &&
            currentFaceIndex == pendingFaceIndex &&
            currentRace == pendingRace &&
            currentLevel == pendingLevel &&
            string.Equals(currentJob, pendingJob, StringComparison.Ordinal) &&
            string.Equals(currentName, pendingPlayerName, StringComparison.Ordinal) &&
            string.Equals(currentFaceArchive, pendingFaceArchive, StringComparison.Ordinal))
        {
            pendingStableSamples++;
        }
        else
        {
            pendingGender = currentGender;
            pendingJob = currentJob;
            pendingPlayerName = currentName;
            pendingFaceIndex = currentFaceIndex;
            pendingRace = currentRace;
            pendingFaceArchive = currentFaceArchive;
            pendingLevel = currentLevel;
            pendingStableSamples = 1;
        }

        return pendingStableSamples >= LOCAL_PROFILE_STABLE_SAMPLE_COUNT;
    }

    private void TrackLocalCharacterProfile(bool force = false)
    {
        if (!isLocalPlayer)
            return;

        float now = Time.realtimeSinceStartup;

        if (IsSaveLoadInProgressNow())
        {
            localProfileHoldUntilRealtime = now + LOCAL_PROFILE_LOAD_SETTLE_SECONDS;
            pendingStableSamples = 0;
            return;
        }

        if (!force && now < localProfileHoldUntilRealtime)
            return;

        if (!force && Time.realtimeSinceStartup < nextProfileCheckTime)
            return;

        nextProfileCheckTime = Time.realtimeSinceStartup + PROFILE_CHECK_INTERVAL;

        if (GameManager.Instance == null || GameManager.Instance.PlayerEntity == null)
            return;

        pEntity = GameManager.Instance.PlayerEntity as DaggerfallEntity;
        if (pEntity == null)
            return;

        PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
        Genders genders = pEntity.Gender;
        DFCareer career = pEntity.Career;
        int currentGender = (genders == Genders.Male ? 0 : 1);
        string currentJob = career != null ? career.Name : string.Empty;
        string currentName = pEntity.Name ?? string.Empty;
        int currentLevel = pEntity.Level;

        RaceTemplate birthRaceTemplate = playerEntity.BirthRaceTemplate;
        int currentFaceIndex = Mathf.Clamp(playerEntity.FaceIndex, 0, 9);
        int currentRace = birthRaceTemplate != null ? (int)birthRaceTemplate.ID : (int)Races.Breton;
        string currentFaceArchive = string.Empty;
        if (birthRaceTemplate != null)
        {
            currentFaceArchive = genders == Genders.Male
                ? birthRaceTemplate.PaperDollHeadsMale
                : birthRaceTemplate.PaperDollHeadsFemale;
        }

        if (!force)
        {
            if (IsObviousPlaceholderProfile(currentJob, currentName))
                return;

            if (!HasStableLocalProfileCandidate(
                currentGender, currentJob, currentName,
                currentFaceIndex, currentRace, currentFaceArchive,
                currentLevel))
                return;
        }

        if (!force &&
            currentGender == lastSentGender &&
            string.Equals(currentJob, lastSentJob, StringComparison.Ordinal) &&
            string.Equals(currentName, lastSentPlayerName, StringComparison.Ordinal) &&
            currentFaceIndex == lastSentFaceIndex &&
            currentRace == lastSentRace &&
            string.Equals(currentFaceArchive, lastSentFaceArchive, StringComparison.Ordinal) &&
            currentLevel == lastSentLevel)
            return;

        lastSentGender = currentGender;
        lastSentJob = currentJob;
        lastSentPlayerName = currentName;
        lastSentFaceIndex = currentFaceIndex;
        lastSentRace = currentRace;
        lastSentFaceArchive = currentFaceArchive;
        lastSentLevel = currentLevel;

        if (isServer)
        {
            ServerSetInfos(
                currentGender, currentJob, currentName,
                currentFaceIndex, currentRace, currentFaceArchive);
            rpcSendHostLevel(currentLevel);
        }
        else
        {
            cmdSendInfos(
                currentGender, currentJob, currentName,
                currentFaceIndex, currentRace, currentFaceArchive);
            cmdSendHighLevel(currentLevel);
        }
    }

    [Command]
    public void cmdSendInfos(int g, string j, string n, int f, int r, string archive)
    {
        ServerSetInfos(g, j, n, f, r, archive);
    }

    [Server]
    private void ServerSetInfos(int g, string j, string n, int f, int r, string archive)
    {
        gender = Mathf.Clamp(g, 0, 1);
        job = j ?? string.Empty;
        playerName = n ?? string.Empty;
        faceIndex = Mathf.Clamp(f, 0, 9);
        race = r;
        faceArchive = archive ?? string.Empty;
    }


    private void TrackLocalHorseMountedState(bool force = false)
    {
        if (!isLocalPlayer)
            return;

        float now = Time.realtimeSinceStartup;
        if (!force && now < nextHorseStateCheckTime)
            return;

        nextHorseStateCheckTime = now + HORSE_STATE_CHECK_INTERVAL;

        bool currentHorseMounted = false;
        try
        {
            TransportManager transportManager =
                GameManager.Instance != null ? GameManager.Instance.TransportManager : null;

            currentHorseMounted =
                transportManager != null &&
                transportManager.TransportMode == TransportModes.Horse;
        }
        catch
        {
            // DFU can briefly rebuild GameManager/player state during loads.
            // Keep the last network state until TransportManager is readable again.
            return;
        }

        if (!force &&
            hasSentHorseMounted &&
            currentHorseMounted == lastSentHorseMounted)
            return;

        hasSentHorseMounted = true;
        lastSentHorseMounted = currentHorseMounted;

        if (isServer)
            ServerSetHorseMounted(currentHorseMounted);
        else
            cmdSetHorseMounted(currentHorseMounted);
    }

    [Command]
    private void cmdSetHorseMounted(bool mounted)
    {
        ServerSetHorseMounted(mounted);
    }

    [Server]
    private void ServerSetHorseMounted(bool mounted)
    {
        horseMounted = mounted;
    }

    private void OnHorseMountedChanged(bool oldValue, bool newValue)
    {
        if (isLocalPlayer)
            return;

        if (spriteMultiplayer != null)
            spriteMultiplayer.SetMountedState(newValue);
    }

    private void TrackLocalLycanthropeVisualState(bool force = false)
    {
        if (!isLocalPlayer)
            return;

        // Do not publish the temporary effect-less PlayerEntity window exposed during F9/load.
        // Keep the previous remote beast/human appearance until the real saved effects return.
        if (IsSaveLoadInProgressNow())
            return;

        float now = Time.realtimeSinceStartup;
        if (!force && now < nextLycanthropeStateCheckTime)
            return;

        nextLycanthropeStateCheckTime = now + LYCANTHROPE_STATE_CHECK_INTERVAL;

        int currentState = 0;
        try
        {
            EntityEffectManager effectManager =
                GameManager.Instance != null ? GameManager.Instance.PlayerEffectManager : null;

            if (effectManager != null && effectManager.IsTransformedLycanthrope())
            {
                LycanthropyTypes type = effectManager.LycanthropyType();
                if (type == LycanthropyTypes.Werewolf)
                    currentState = 1;
                else if (type == LycanthropyTypes.Wereboar)
                    currentState = 2;
            }
        }
        catch
        {
            // Player/effect state can be rebuilt briefly during save/load. Keep the previous
            // synchronized cosmetic state until DFU's real effect manager is readable again.
            return;
        }

        if (!force &&
            hasSentLycanthropeVisualState &&
            currentState == lastSentLycanthropeVisualState)
            return;

        hasSentLycanthropeVisualState = true;
        lastSentLycanthropeVisualState = currentState;

        if (isServer)
            ServerSetLycanthropeVisualState(currentState);
        else
            cmdSetLycanthropeVisualState(currentState);
    }

    [Command]
    private void cmdSetLycanthropeVisualState(int state)
    {
        ServerSetLycanthropeVisualState(state);
    }

    [Server]
    private void ServerSetLycanthropeVisualState(int state)
    {
        lycanthropeVisualState = Mathf.Clamp(state, 0, 2);
    }

    private void OnLycanthropeVisualStateChanged(int oldValue, int newValue)
    {
        if (isLocalPlayer)
            return;

        if (spriteMultiplayer != null)
            spriteMultiplayer.SetLycanthropeVisualState(newValue);

        // Switch the visual immediately on transformation/reversion rather than
        // waiting for the 0.15-second appearance poll. The last human paperdoll
        // stays cached while the animated beast sprite is being used.
        nextRemotePaperdollCheck = 0f;
        if (NetworkClient.active && spriteMultiplayer != null)
            TickRemotePaperdoll();
    }

    // Only send a small Boolean when stance changes, regardless of the viewer's
    // paperdoll preference. A newly connected viewer also gets the SyncVar value.
    private void TrackLocalPaperdollCrouch(bool force = false)
    {
        if (!isLocalPlayer)
            return;
        float now = Time.unscaledTime;
        if (!force && now < nextPaperdollCrouchCheckTime)
            return;
        nextPaperdollCrouchCheckTime = now + PAPERDOLL_CROUCH_CHECK_INTERVAL;

        if (GameManager.Instance == null || GameManager.Instance.PlayerMotor == null)
            return;
        bool crouched = GameManager.Instance.PlayerMotor.IsCrouching;
        if (!force && hasSentPaperdollCrouching && crouched == lastSentPaperdollCrouching)
            return;
        lastSentPaperdollCrouching = crouched;
        hasSentPaperdollCrouching = true;
        if (isServer)
            paperdollCrouching = crouched;
        else
            CmdUpdatePaperdollCrouch(crouched);
    }

    [Command]
    private void CmdUpdatePaperdollCrouch(bool crouched)
    {
        paperdollCrouching = crouched;
    }

    [Command]
    public void cmdApplyHostLevel()
    {
        rpcSendHostLevel(hostLevel);
    }

    [ClientRpc]
    public void rpcSendHostLevel(int l)
    {
        hostLevel = l;
    }

    [Command]
    public void cmdSendHighLevel(int l)
    {
        rpcSendHighLevel(l);
    }

    [ClientRpc]
    public void rpcSendHighLevel(int l)
    {
        if (hostLevel < l)
            hostLevel = l;
    }

    IEnumerator TrackLevel()
    {
        while (true){
            yield return new WaitForSeconds(1.1f);

            if (IsSaveLoadInProgressNow())
                continue;

            if (GameManager.Instance == null || GameManager.Instance.PlayerEntity == null)
                continue;

            DaggerfallEntity currentEntity = GameManager.Instance.PlayerEntity as DaggerfallEntity;
            if (currentEntity == null)
                continue;

            pEntity = currentEntity;
            if (hostLevel != currentEntity.Level)
                rpcSendHostLevel(currentEntity.Level);
        }
    }

    IEnumerator trackTorch()
    {
        yield return new WaitForSeconds(1.653f);
        GameObject playerTorch = PlayerMultiplayer.playerObject.GetComponent<EnablePlayerTorch>().PlayerTorch;
        bool lastState = false;
        while (true)
        {
            bool torchEnable = playerTorch.activeSelf;
            if (torchEnable != lastState){
                cmdEnableTorch(torchEnable);
                lastState = torchEnable;
            }
            yield return new WaitForSeconds(0.1f);
        }
    }


    [Command]
    void cmdEnableTorch(bool b)
    {
        rpcEnableTorch(b);
    }

    [ClientRpc]
    void rpcEnableTorch(bool b)
    {
        if (!isLocalPlayer)
            torch.SetActive(b);
    }


    void initProperAsset()
    {
        RefreshRemoteProfileIfChanged(true);
    }

    private bool IsRemoteProfileReadyToApply()
    {
        if (string.IsNullOrEmpty(job) || string.IsNullOrEmpty(playerName))
            return false;

        if (string.Equals(playerName, "Nameless", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private bool HasRemoteProfileBeenStableLongEnough()
    {
        float now = Time.realtimeSinceStartup;

        if (gender != observedRemoteGender ||
            faceIndex != observedRemoteFaceIndex ||
            race != observedRemoteRace ||
            !string.Equals(job, observedRemoteJob, StringComparison.Ordinal) ||
            !string.Equals(playerName, observedRemotePlayerName, StringComparison.Ordinal) ||
            !string.Equals(faceArchive, observedRemoteFaceArchive, StringComparison.Ordinal))
        {
            observedRemoteGender = gender;
            observedRemoteJob = job;
            observedRemotePlayerName = playerName;
            observedRemoteFaceIndex = faceIndex;
            observedRemoteRace = race;
            observedRemoteFaceArchive = faceArchive;
            observedRemoteProfileSinceRealtime = now;
            return false;
        }

        return now - observedRemoteProfileSinceRealtime >= REMOTE_PROFILE_STABLE_SECONDS;
    }

    private void RefreshRemoteProfileIfChanged(bool force = false)
    {
        // Never run remote visual setup on the local player. The local PlayerAdvanced is
        // the real visible body/camera; enabling this MP sprite locally makes the player
        // see their own fake enemy sprite.
        if (isLocalPlayer)
            return;

        if (!IsRemoteProfileReadyToApply())
            return;

        if (!force && !HasRemoteProfileBeenStableLongEnough())
            return;

        if (!force &&
            gender == lastAppliedGender &&
            faceIndex == lastAppliedFaceIndex &&
            race == lastAppliedRace &&
            string.Equals(job, lastAppliedJob, StringComparison.Ordinal) &&
            string.Equals(playerName, lastAppliedPlayerName, StringComparison.Ordinal) &&
            string.Equals(faceArchive, lastAppliedFaceArchive, StringComparison.Ordinal))
            return;

        lastAppliedGender = gender;
        lastAppliedJob = job;
        lastAppliedPlayerName = playerName;
        lastAppliedFaceIndex = faceIndex;
        lastAppliedRace = race;
        lastAppliedFaceArchive = faceArchive;

        if (OptionsMultiplayer.displayName)
        {
            if (nameText != null)
            {
                nameText.gameObject.SetActive(true);
                nameText.SetText(playerName ?? string.Empty);
            }
        }
        else if (nameText != null)
        {
            nameText.gameObject.SetActive(false);
        }

        actualAsset = (assets != null && assets.Count > 0) ? assets[0] : actualAsset;

        if (spriteMultiplayer != null)
        {
            if (!spriteMultiplayer.gameObject.activeSelf)
                spriteMultiplayer.gameObject.SetActive(true);

            // Apply all synchronized cosmetic inputs. SpriteMultiplayer owns visual priority:
            // transformed werebeast > mounted rider > normal class.
            spriteMultiplayer.SetMountedState(horseMounted);
            spriteMultiplayer.RefreshProfile(job, gender);
            spriteMultiplayer.SetLycanthropeVisualState(lycanthropeVisualState);
        }
    }









    public Sprite getIdleSprite(int i){
        return actualAsset.idleSprites[i];
    }

    public Sprite getWalkSprite(int i, int j){
        return actualAsset.walkSprites[i*4+j];
    }

    public Sprite getAttackSprite(int i, int j){
        return actualAsset.attackSprites[j + i*4];
    }

    public int getWalkCount()
    {
        return actualAsset.walkCount;
    }
    public int getAttackCount()
    {
        return actualAsset.attackCount;
    }


    // --- Optional cosmetic paperdoll representation of OTHER players ---
    // Paperdolls are a presentation preference on EACH viewer. The sender transmits
    // only an appearance description; no remote inventory or gameplay objects are touched.
    const string PaperdollPreferenceKey = "DFUCoop.RemotePaperdolls";
    const int MaxSnapshotCharacters = 8192;
    const int MaxAppearanceItems = 64;
    const float AppearanceCheckSeconds = 0.55f;
    const float AppearanceUploadSeconds = 2.50f;
    const float PaperdollWorldHeight = 2.25f;
    // Remote player prefab CharacterController: height 1.8, center Y=0, so
    // standing capsule bottom is -0.9 relative to the synced player root.
    // DFU PlayerHeightChanger shortens to 0.9 and moves that root DOWN by 0.45
    // on crouch; compensate visually without touching gameplay collision.
    // A small additional visual correction for the transparent margin beneath
    // paperdoll feet. Both poses share the same correction to keep ground contact.
    // This affects only this local billboard; never the remote networked root.
    const float PaperdollFootGroundCorrection = -0.06f;
    const float PaperdollStandingFootY = -0.90f + PaperdollFootGroundCorrection;
    const float PaperdollCrouchingFootY = -0.45f + PaperdollFootGroundCorrection;
    const float PaperdollFootApproachSpeed = 3.0f;
    // Do not squash the paperdoll when crouching: its artwork has no crouch pose.
    // Crouch synchronization is used ONLY to maintain the foot-to-ground offset.
    const string PaperdollTurnPreferenceKey = "DFUCoop.RemotePaperdollTurn";
    const string PaperdollBobPreferenceKey = "DFUCoop.RemotePaperdollBob";
    const string PaperdollAttackPreferenceKey = "DFUCoop.RemotePaperdollAttacks";
    const float PaperdollAttackDuration = 0.44f;
    static bool paperdollPreferenceLoaded;
    static bool paperdollPreference;

    public static bool UseRemotePaperdolls
    {
        get
        {
            if (!paperdollPreferenceLoaded)
            {
                paperdollPreference = PlayerPrefs.GetInt(PaperdollPreferenceKey, 0) != 0;
                paperdollPreferenceLoaded = true;
            }
            return paperdollPreference;
        }
        set
        {
            if (UseRemotePaperdolls == value) return;
            paperdollPreference = value;
            PlayerPrefs.SetInt(PaperdollPreferenceKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    // Completely local presentation options: never sent to the server.
    // 0 = always face the camera (original behavior).
    // 1 = rotate with the remote player's yaw; back appears mirrored.
    // 2 = rotate with player; viewer sees an unmirrored face on the back.
    public static int RemotePaperdollTurnMode
    {
        get { return Mathf.Clamp(PlayerPrefs.GetInt(PaperdollTurnPreferenceKey, 0), 0, 2); }
        set
        {
            PlayerPrefs.SetInt(PaperdollTurnPreferenceKey, Mathf.Clamp(value, 0, 2));
            PlayerPrefs.Save();
        }
    }

    public static bool RemotePaperdollBobEnabled
    {
        get { return PlayerPrefs.GetInt(PaperdollBobPreferenceKey, 1) != 0; }
        set
        {
            PlayerPrefs.SetInt(PaperdollBobPreferenceKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    // Disabled by default until the viewer chooses to add subtle attack motion.
    // Entirely local: does not change the attack, hit detection, or player position.
    public static bool RemotePaperdollAttacksEnabled
    {
        get { return PlayerPrefs.GetInt(PaperdollAttackPreferenceKey, 0) != 0; }
        set
        {
            PlayerPrefs.SetInt(PaperdollAttackPreferenceKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    [Serializable]
    public sealed class PaperdollVisualItem
    {
        public int slot;
        public int group;
        public int groupIndex;
        public int nativeMaterial;
        public int dye;
        public int variant;
        public int drawOrder;
        public int artifactIndex;
    }

    [Serializable]
    public sealed class PaperdollVisualSnapshot
    {
        public int revision;
        public string nudeBody;
        public string clothedBody;
        public string headArchive;
        public int headRecord;
        public bool upperClothed;
        public bool lowerClothed;
        public int werebeast; // 0 = human/vampire, 1 = wolf, 2 = boar
        public PaperdollVisualItem[] items;
    }

    static readonly Dictionary<uint, PaperdollVisualSnapshot> receivedAppearances =
        new Dictionary<uint, PaperdollVisualSnapshot>();
    // Preserve the most recent *human* appearance across beast snapshots. DFU's
    // transformation SyncVar can arrive before the next human appearance upload.
    // This is viewer-local cached artwork, not another Mirror synchronization field.
    static readonly Dictionary<uint, PaperdollVisualSnapshot> receivedHumanAppearances =
        new Dictionary<uint, PaperdollVisualSnapshot>();
    static PaperDollRenderer sharedRemotePaperdollRenderer;
    string serverLatestAppearanceJson;
    int serverLatestAppearanceRevision;
    float serverNextAppearanceUpload;
    bool serverWantsPaperdolls;
    float nextLocalPaperdollCheck;
    float nextLocalPaperdollSend;
    int lastLocalPaperdollSignature = int.MinValue;
    int localPaperdollRevision;
    bool lastPaperdollInterestSent;
    bool sentPaperdollInterest;

    GameObject remotePaperdollObject;
    SpriteRenderer remotePaperdollRenderer;
    Sprite remotePaperdollSprite;
    Texture2D remotePaperdollTexture;
    int displayedPaperdollRevision = -1;
    float nextRemotePaperdollCheck;
    Vector3 lastPaperdollRootPosition;
    bool paperdollRootPositionInitialized;
    bool paperdollCameraBehind;
    float paperdollStepPhase;
    float paperdollMovingBlend;
    float paperdollMovingBlendVelocity;
    float paperdollCurrentFootY = PaperdollStandingFootY;
    PositionMultiplayer cachedPaperdollPosition;
    float paperdollAttackStartedAt = float.NegativeInfinity;
    int paperdollAttackSide;
    bool paperdollAttackUpwards;

    /// <summary>
    /// Presentation-only reaction to the existing PlayerAnimations attack RPC.
    /// Direction is the DFU WeaponStates value captured by the owning player.
    /// The transform modified by the effect is the viewer's paperdoll child, never
    /// the player's Mirror root or collision proxy.
    /// </summary>
    public void TriggerRemotePaperdollAttack(bool melee, byte direction)
    {
        if (!melee || isLocalPlayer || !UseRemotePaperdolls ||
            !RemotePaperdollAttacksEnabled || spriteMultiplayer == null ||
            spriteMultiplayer.IsMountedVisualActive ||
            spriteMultiplayer.IsLycanthropeVisualActive ||
            spriteMultiplayer.IsDownedVisualActive)
            return;

        // DFU WeaponStates: 1 Down, 2 DownLeft, 3 Left, 4 Right,
        // 5 DownRight, 6 Up. Unknown swing directions get a small lunge only.
        paperdollAttackSide = direction == 2 || direction == 3 ? -1 :
            (direction == 4 || direction == 5 ? 1 : 0);
        paperdollAttackUpwards = direction == 6;
        paperdollAttackStartedAt = Time.unscaledTime;
    }

    // Limit archive names to simple resource names. Incoming packets are untrusted.
    static bool SafeResourceName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                  (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-'))
                return false;
        }
        return name.EndsWith(".IMG", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".CIF", StringComparison.OrdinalIgnoreCase);
    }

    static bool SafeAppearance(PaperdollVisualSnapshot snapshot)
    {
        if (snapshot == null || snapshot.revision <= 0 ||
            snapshot.headRecord < 0 || snapshot.headRecord > 255 ||
            snapshot.werebeast < 0 || snapshot.werebeast > 2 ||
            !SafeResourceName(snapshot.nudeBody) ||
            !SafeResourceName(snapshot.clothedBody) ||
            !SafeResourceName(snapshot.headArchive) ||
            snapshot.items == null || snapshot.items.Length > MaxAppearanceItems)
            return false;
        for (int i = 0; i < snapshot.items.Length; i++)
        {
            PaperdollVisualItem item = snapshot.items[i];
            if (item == null || item.slot < 0 || item.slot >= MaxAppearanceItems ||
                item.group < 0 || item.group > 1024 ||
                item.groupIndex < 0 || item.groupIndex > 100000 ||
                item.nativeMaterial < 0 || item.nativeMaterial > 10000 ||
                item.dye < 0 || item.dye > 256 ||
                item.variant < 0 || item.variant > 64 ||
                item.drawOrder < -2000 || item.drawOrder > 2000 ||
                item.artifactIndex < 0 || item.artifactIndex > 100000)
                return false;
        }
        return true;
    }

    int GetLocalPaperdollSignature(PlayerEntity entity)
    {
        unchecked
        {
            int hash = 19;
            hash = hash * 31 + entity.FaceIndex;
            hash = hash * 31 + (int)entity.Gender;
            hash = hash * 31 + (entity.BirthRaceTemplate != null ? (int)entity.BirthRaceTemplate.ID : 0);
            EntityEffectManager em = GameManager.Instance.PlayerEffectManager;
            RacialOverrideEffect special = em != null ? em.GetRacialOverrideEffect() : null;
            hash = hash * 31 + (special != null ? special.GetType().Name.GetHashCode() : 0);
            hash = hash * 31 + (em != null && em.IsTransformedLycanthrope() ? 1 : 0);
            DaggerfallUnityItem[] items = entity.ItemEquipTable != null ? entity.ItemEquipTable.EquipTable : null;
            if (items != null)
                for (int i = 0; i < items.Length; i++)
                {
                    DaggerfallUnityItem item = items[i];
                    hash = hash * 31 + i;
                    if (item == null) continue;
                    hash = hash * 31 + item.UID.GetHashCode();
                    hash = hash * 31 + (int)item.ItemGroup;
                    hash = hash * 31 + item.GroupIndex;
                    hash = hash * 31 + item.nativeMaterialValue;
                    hash = hash * 31 + (int)item.dyeColor;
                    hash = hash * 31 + item.CurrentVariant;
                    hash = hash * 31 + item.drawOrder;
                }
            return hash;
        }
    }

    PaperdollVisualSnapshot CaptureLocalAppearance()
    {
        PlayerEntity entity = GameManager.Instance.PlayerEntity;
        if (entity == null || entity.BirthRaceTemplate == null || entity.ItemEquipTable == null)
            return null;
        RaceTemplate raceTemplate = entity.BirthRaceTemplate;
        bool male = entity.Gender == Genders.Male;
        var snapshot = new PaperdollVisualSnapshot();
        snapshot.nudeBody = male ? raceTemplate.PaperDollBodyMaleUnclothed : raceTemplate.PaperDollBodyFemaleUnclothed;
        snapshot.clothedBody = male ? raceTemplate.PaperDollBodyMaleClothed : raceTemplate.PaperDollBodyFemaleClothed;
        snapshot.headArchive = male ? raceTemplate.PaperDollHeadsMale : raceTemplate.PaperDollHeadsFemale;
        snapshot.headRecord = entity.FaceIndex;
        snapshot.upperClothed = entity.ItemEquipTable.IsUpperClothed();
        snapshot.lowerClothed = entity.ItemEquipTable.IsLowerClothed();

        EntityEffectManager em = GameManager.Instance.PlayerEffectManager;
        RacialOverrideEffect special = em != null ? em.GetRacialOverrideEffect() : null;
        if (special is VampirismEffect)
        {
            // DFU's vampire heads are local assets, so each viewer gets their own replacement.
            snapshot.headArchive = "VAMP00I0.CIF";
            snapshot.headRecord = (male ? 8 : 0) + (int)raceTemplate.ID - 1;
        }
        if (em != null && em.IsTransformedLycanthrope())
        {
            LycanthropyTypes beast = em.LycanthropyType();
            snapshot.werebeast = beast == LycanthropyTypes.Werewolf ? 1 :
                                beast == LycanthropyTypes.Wereboar ? 2 : 0;
        }

        DaggerfallUnityItem[] equipped = entity.ItemEquipTable.EquipTable;
        List<PaperdollVisualItem> visualItems = new List<PaperdollVisualItem>();
        for (int i = 0; i < equipped.Length && i < MaxAppearanceItems; i++)
        {
            DaggerfallUnityItem item = equipped[i];
            if (item == null) continue;
            visualItems.Add(new PaperdollVisualItem {
                slot = i, group = (int)item.ItemGroup,
                groupIndex = item.GroupIndex,
                nativeMaterial = item.nativeMaterialValue,
                dye = (int)item.dyeColor, variant = item.CurrentVariant,
                drawOrder = item.drawOrder,
                artifactIndex = item.IsArtifact ? item.ArtifactIndexBitfield : 0,
            });
        }
        snapshot.items = visualItems.ToArray();
        return snapshot;
    }

    void TickLocalPaperdoll()
    {
        if (!NetworkClient.active || !isLocalPlayer || netId == 0)
            return;
        if (!sentPaperdollInterest || lastPaperdollInterestSent != UseRemotePaperdolls)
        {
            sentPaperdollInterest = true;
            lastPaperdollInterestSent = UseRemotePaperdolls;
            CmdSetPaperdollInterest(lastPaperdollInterestSent);
        }

        float now = Time.unscaledTime;
        if (now < nextLocalPaperdollCheck || now < nextLocalPaperdollSend || IsSaveLoadInProgressNow())
            return;
        nextLocalPaperdollCheck = now + AppearanceCheckSeconds;
        if (GameManager.Instance == null || GameManager.Instance.PlayerEntity == null)
            return;
        PlayerEntity entity = GameManager.Instance.PlayerEntity;
        if (IsObviousPlaceholderProfile(entity.Career != null ? entity.Career.Name : "", entity.Name))
            return;
        int signature = GetLocalPaperdollSignature(entity);
        if (signature == lastLocalPaperdollSignature)
            return;
        try
        {
            PaperdollVisualSnapshot snapshot = CaptureLocalAppearance();
            if (snapshot == null) return;
            snapshot.revision = ++localPaperdollRevision;
            if (!SafeAppearance(snapshot)) return;
            string json = JsonUtility.ToJson(snapshot);
            if (json.Length > MaxSnapshotCharacters) return;
            CmdSendPaperdollAppearance(json);
            lastLocalPaperdollSignature = signature;
            nextLocalPaperdollSend = now + AppearanceUploadSeconds;
        }
        catch (Exception e)
        {
            nextLocalPaperdollSend = now + 5f;
            Debug.LogWarning("[PaperdollMP] Failed to collect appearance: " + e.Message);
        }
    }

    [Command]
    void CmdSetPaperdollInterest(bool interested)
    {
        serverWantsPaperdolls = interested;
        if (!interested || connectionToClient == null) return;
        // One cached snapshot per player, including late joiners and runtime toggles.
        PlayerAssets[] players = FindObjectsOfType<PlayerAssets>();
        for (int i = 0; i < players.Length; i++)
        {
            PlayerAssets source = players[i];
            if (source != null && source.netId != netId && !string.IsNullOrEmpty(source.serverLatestAppearanceJson))
                TargetReceivePaperdollAppearance(connectionToClient, source.netId, source.serverLatestAppearanceJson);
        }
    }

    [Command]
    void CmdSendPaperdollAppearance(string json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxSnapshotCharacters ||
            Time.unscaledTime < serverNextAppearanceUpload)
            return;
        PaperdollVisualSnapshot snapshot;
        try { snapshot = JsonUtility.FromJson<PaperdollVisualSnapshot>(json); }
        catch { return; }
        if (!SafeAppearance(snapshot) || snapshot.revision <= serverLatestAppearanceRevision)
            return;
        serverLatestAppearanceJson = json;
        serverLatestAppearanceRevision = snapshot.revision;
        serverNextAppearanceUpload = Time.unscaledTime + 0.70f;
        PlayerAssets[] players = FindObjectsOfType<PlayerAssets>();
        for (int i = 0; i < players.Length; i++)
        {
            PlayerAssets viewer = players[i];
            if (viewer != null && viewer.netId != netId && viewer.serverWantsPaperdolls &&
                viewer.connectionToClient != null)
                viewer.TargetReceivePaperdollAppearance(viewer.connectionToClient, netId, json);
        }
    }

    [TargetRpc]
    void TargetReceivePaperdollAppearance(NetworkConnection target, uint subjectId, string json)
    {
        if (!UseRemotePaperdolls || subjectId == 0 || string.IsNullOrEmpty(json) ||
            json.Length > MaxSnapshotCharacters)
            return;
        PaperdollVisualSnapshot snapshot;
        try { snapshot = JsonUtility.FromJson<PaperdollVisualSnapshot>(json); }
        catch { return; }
        if (!SafeAppearance(snapshot)) return;
        PaperdollVisualSnapshot old;
        if (receivedAppearances.TryGetValue(subjectId, out old) && old.revision >= snapshot.revision)
            return;
        receivedAppearances[subjectId] = snapshot;
        if (snapshot.werebeast == 0)
            receivedHumanAppearances[subjectId] = snapshot;
    }

    void TickRemotePaperdoll()
    {
        if (!NetworkClient.active || isLocalPlayer || spriteMultiplayer == null) return;
        UpdateRemotePaperdollPose(); // Every frame, independent of appearance-message cadence.
        if (Time.unscaledTime < nextRemotePaperdollCheck) return;
        nextRemotePaperdollCheck = Time.unscaledTime + 0.15f;
        PaperdollVisualSnapshot snapshot;
        bool available = receivedAppearances.TryGetValue(netId, out snapshot);
        if (available && snapshot.werebeast != 0)
        {
            // The latest upload may still be a werewolf/boar snapshot when the
            // separate transformation SyncVar already says human. In that case,
            // reuse the cached human artwork immediately; replace it normally
            // when the new human snapshot finally arrives.
            PaperdollVisualSnapshot humanSnapshot;
            if (receivedHumanAppearances.TryGetValue(netId, out humanSnapshot))
                snapshot = humanSnapshot;
            else
                available = false; // No human appearance received yet (e.g. late join).
        }
        if (cachedPaperdollPosition == null)
            cachedPaperdollPosition = GetComponent<PositionMultiplayer>();
        PositionMultiplayer position = cachedPaperdollPosition;
        bool inVisibleWorld = position == null ||
            (position.ShouldShowRemoteVisualForCurrentCoordinates() &&
             (position.visual == null || position.visual.activeInHierarchy));
        // The werewolf/wereboar inventory image has a rectangular background.
        // Show SpriteMultiplayer's existing animated beast instead. Once back in
        // human form, restore the last human paperdoll without waiting for the
        // slower appearance upload; never draw the rectangular beast artwork.
        bool show = UseRemotePaperdolls && available && inVisibleWorld &&
            !spriteMultiplayer.IsMountedVisualActive &&
            !spriteMultiplayer.IsLycanthropeVisualActive &&
            !spriteMultiplayer.IsDownedVisualActive &&
            snapshot.werebeast == 0;
        if (show && snapshot.revision != displayedPaperdollRevision)
            BuildRemotePaperdoll(snapshot);
        if (remotePaperdollObject != null)
            remotePaperdollObject.SetActive(show && remotePaperdollSprite != null);
        spriteMultiplayer.SetPaperdollVisualActive(show && remotePaperdollSprite != null);
    }

    void UpdateRemotePaperdollPose()
    {
        if (remotePaperdollObject == null || !remotePaperdollObject.activeSelf)
        {
            paperdollRootPositionInitialized = false;
            paperdollMovingBlend = 0f;
            return;
        }
        Camera cam = Camera.main;
        if (cam == null) return;
        float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.05f);
        Vector3 rootPosition = transform.position;
        if (!paperdollRootPositionInitialized)
        {
            lastPaperdollRootPosition = rootPosition;
            paperdollRootPositionInitialized = true;
        }
        Vector3 delta = rootPosition - lastPaperdollRootPosition;
        lastPaperdollRootPosition = rootPosition;
        delta.y = 0f;
        // Teleports / exterior seam corrections do not count as walking.
        bool walking = dt > 0.001f && delta.magnitude > 0.003f && delta.magnitude / dt < 8f;
        paperdollMovingBlend = Mathf.SmoothDamp(paperdollMovingBlend,
            walking ? 1f : 0f, ref paperdollMovingBlendVelocity, 0.22f);
        if (paperdollMovingBlend > 0.01f)
            paperdollStepPhase += dt * 9f;
        // Feet stay grounded in both poses. DFU shifts the player's root down
        // when crouching; shift this billboard's foot anchor up by the same amount.
        // Keep full image proportions: there is no actual crouch animation frame.
        bool animateBob = RemotePaperdollBobEnabled;
        float bob = animateBob ? 0.021f * paperdollMovingBlend * Mathf.Abs(Mathf.Sin(paperdollStepPhase)) : 0f;
        float tilt = animateBob ? 0.80f * paperdollMovingBlend * Mathf.Sin(paperdollStepPhase * 0.5f) : 0f;
        float targetFootY = paperdollCrouching ? PaperdollCrouchingFootY : PaperdollStandingFootY;
        paperdollCurrentFootY = Mathf.MoveTowards(
            paperdollCurrentFootY, targetFootY, PaperdollFootApproachSpeed * dt);
        remotePaperdollObject.transform.localScale = Vector3.one;

        // This renderer is visual-only; it NEVER changes PlayerMultiplayer rotation.
        // SpriteRenderer's FRONT normal is local -Z (not +Z). The previous
        // LookRotation(+direction) exposed its back, mirroring even camera mode.
        // Aim -Z towards the viewer for the real paperdoll orientation.
        // Two-sided modes still show the back mirrored in mode 1 and
        // compensate with flipX when behind in mode 2.
        Vector3 direction = cam.transform.position - remotePaperdollObject.transform.position;
        direction.y = 0f; // Keep completely upright, even when looking up or down.
        if (direction.sqrMagnitude < 0.001f) return;
        int turnMode = RemotePaperdollTurnMode;
        Quaternion facing;
        if (turnMode == 0)
        {
            facing = Quaternion.LookRotation(-direction.normalized, Vector3.up);
            remotePaperdollRenderer.flipX = false;
        }
        else
        {
            // The networked proxy already carries the remote character's facing.
            // Use yaw only so looking vertically never tips the paperdoll.
            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            forward.Normalize();
            facing = Quaternion.LookRotation(-forward, Vector3.up);
            // Use hysteresis so the backside texture does not flicker when the
            // camera crosses the paperdoll's edge-on angle.
            float facingDot = Vector3.Dot(forward, direction.normalized);
            if (facingDot < -0.12f) paperdollCameraBehind = true;
            else if (facingDot > 0.12f) paperdollCameraBehind = false;
            remotePaperdollRenderer.flipX = (turnMode == 2 && paperdollCameraBehind);
        }
        // A quick wind-up, strike and recovery. The curve starts by moving in
        // the opposite direction, then briefly leans into the hit before returning.
        // The paperdoll artwork is never squashed or deformed.
        float attackAmount = 0f;
        if (RemotePaperdollAttacksEnabled)
        {
            float elapsed = Time.unscaledTime - paperdollAttackStartedAt;
            if (elapsed >= 0f && elapsed < PaperdollAttackDuration)
            {
                if (elapsed < 0.10f)
                    attackAmount = -0.30f * Mathf.SmoothStep(0f, 1f, elapsed / 0.10f);
                else if (elapsed < 0.23f)
                    attackAmount = Mathf.Lerp(-0.30f, 1f,
                        Mathf.SmoothStep(0f, 1f, (elapsed - 0.10f) / 0.13f));
                else
                    attackAmount = 1f - Mathf.SmoothStep(0f, 1f,
                        (elapsed - 0.23f) / (PaperdollAttackDuration - 0.23f));
            }
        }

        // Follow the ATTACKER's left/right direction in world space, rather than
        // guessing from the viewer camera angle. The tiny forward lunge follows
        // their facing too. The root/collider and networked yaw do not move.
        Vector3 playerRight = transform.right;
        playerRight.y = 0f;
        if (playerRight.sqrMagnitude < 0.001f) playerRight = Vector3.right;
        playerRight.Normalize();
        Vector3 attackWorld = playerRight * (paperdollAttackSide * 0.043f * attackAmount);
        Vector3 playerForward = transform.forward;
        playerForward.y = 0f;
        if (playerForward.sqrMagnitude > 0.001f)
            attackWorld += playerForward.normalized * (0.032f * attackAmount);
        Vector3 attackLocal = transform.InverseTransformDirection(attackWorld);
        remotePaperdollObject.transform.localPosition =
            new Vector3(0f, paperdollCurrentFootY + bob, 0f) + attackLocal;

        // Convert attack side to the billboard's visible direction, including
        // camera-facing and both two-sided modes.
        float visibleAttackSide = paperdollAttackSide *
            Vector3.Dot(playerRight, facing * Vector3.right);
        float attackRoll = -4.0f * visibleAttackSide * attackAmount;
        float attackPitch = (paperdollAttackUpwards ? -3.0f : 3.0f) * attackAmount;
        Quaternion targetRotation = facing * Quaternion.Euler(attackPitch, 0f, tilt + attackRoll);
        // Smooth cosmetic turning, do not alter the network proxy's rotation.
        float rotationBlend = 1f - Mathf.Exp(-14f * dt);
        remotePaperdollObject.transform.rotation = Quaternion.Slerp(
            remotePaperdollObject.transform.rotation, targetRotation, rotationBlend);
    }

    void BuildRemotePaperdoll(PaperdollVisualSnapshot snapshot)
    {
        try
        {
            if (sharedRemotePaperdollRenderer == null)
                sharedRemotePaperdollRenderer = new PaperDollRenderer(2f);
            var items = new List<DaggerfallUnityItem>();
            foreach (PaperdollVisualItem record in snapshot.items)
            {
                try
                {
                    // These objects are never equipped, placed in inventory, or sent to
                    // gameplay systems. They only satisfy DFU's item-image resolver.
                    DaggerfallUnityItem item = record.artifactIndex > 0 && (record.artifactIndex & 1) == 1
                        ? new DaggerfallUnityItem(ItemGroups.Artifacts, record.artifactIndex >> 1)
                        : new DaggerfallUnityItem((ItemGroups)record.group, record.groupIndex);
                    item.nativeMaterialValue = record.nativeMaterial;
                    item.dyeColor = (DyeColors)record.dye;
                    item.CurrentVariant = record.variant;
                    item.drawOrder = record.drawOrder;
                    item.EquipSlot = (EquipSlots)record.slot;
                    items.Add(item);
                }
                catch (Exception)
                {
                    // Unknown modded item on this client: omit it rather than break a
                    // player's entire paperdoll or try to load a foreign asset bundle.
                }
            }
            sharedRemotePaperdollRenderer.RefreshRemote(
                snapshot.nudeBody, snapshot.clothedBody,
                snapshot.headArchive, snapshot.headRecord,
                snapshot.upperClothed, snapshot.lowerClothed,
                snapshot.werebeast, items);
            Texture2D composed = sharedRemotePaperdollRenderer.PaperDollTexture;
            if (composed == null) return;
            Texture2D copy = new Texture2D(composed.width, composed.height, TextureFormat.ARGB32, false);
            copy.SetPixels32(composed.GetPixels32());
            copy.filterMode = DaggerfallUI.Instance != null ? DaggerfallUI.Instance.GlobalFilterMode : FilterMode.Point;
            copy.Apply();
            if (remotePaperdollObject == null)
            {
                remotePaperdollObject = new GameObject("MP Paperdoll (visual only)");
                remotePaperdollObject.transform.SetParent(transform, false);
                paperdollCurrentFootY = paperdollCrouching
                    ? PaperdollCrouchingFootY : PaperdollStandingFootY;
                remotePaperdollObject.transform.localScale = Vector3.one;
                remotePaperdollObject.transform.localPosition = new Vector3(0f, paperdollCurrentFootY, 0f);
                remotePaperdollRenderer = remotePaperdollObject.AddComponent<SpriteRenderer>();
                remotePaperdollRenderer.sortingOrder = 0;
                // No collider, Rigidbody, NetworkIdentity or gameplay component.
            }
            if (remotePaperdollSprite != null) Destroy(remotePaperdollSprite);
            if (remotePaperdollTexture != null) Destroy(remotePaperdollTexture);
            remotePaperdollTexture = copy;
            remotePaperdollSprite = Sprite.Create(copy,
                new Rect(0, 0, copy.width, copy.height),
                new Vector2(0.5f, 0f), copy.height / PaperdollWorldHeight);
            remotePaperdollRenderer.sprite = remotePaperdollSprite;
            displayedPaperdollRevision = snapshot.revision;
        }
        catch (Exception e)
        {
            // Fail back to the classic player sprite instead of logging every 0.15 s.
            displayedPaperdollRevision = snapshot.revision;
            ReleasePaperdollPresentation();
            Debug.LogWarning("[PaperdollMP] Remote appearance could not be drawn: " + e.Message);
        }
    }

    void ReleasePaperdollPresentation()
    {
        if (remotePaperdollObject != null) Destroy(remotePaperdollObject);
        if (remotePaperdollSprite != null) Destroy(remotePaperdollSprite);
        if (remotePaperdollTexture != null) Destroy(remotePaperdollTexture);
        remotePaperdollObject = null;
        remotePaperdollRenderer = null;
        remotePaperdollSprite = null;
        remotePaperdollTexture = null;
    }

    void OnDestroy()
    {
        ReleasePaperdollPresentation();
        if (netId != 0 && NetworkClient.active)
        {
            receivedAppearances.Remove(netId);
            receivedHumanAppearances.Remove(netId);
        }
        if (isServer)
            hostLevel = 0;
    }

}
