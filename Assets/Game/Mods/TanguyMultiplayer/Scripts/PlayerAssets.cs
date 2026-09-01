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
        }
        else
        {
            RefreshRemoteProfileIfChanged();
        }
    }

    void init()
    {
        TrackLocalCharacterProfile(true);
        TrackLocalHorseMountedState(true);
        TrackLocalLycanthropeVisualState(true);

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

    void OnDestroy()
    {
        if (isServer)
            hostLevel = 0;
    }

}
