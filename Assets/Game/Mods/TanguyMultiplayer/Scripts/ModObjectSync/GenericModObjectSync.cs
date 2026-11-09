// Generic, opt-in synchronization for stationary mod props. Requires MultiplayerAPI v0.4.
// Mod names, selectors and asset IDs belong in profiles, not in this implementation.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Utility.AssetInjection;
using DaggerfallCoop.API;

namespace DaggerfallCoop.ObjectSync
{
    // Custom factories can defer an update while their presentation assets arrive.
    public sealed class ObjectSyncPendingException : Exception { }
    [Serializable] public sealed class ObjectSyncProfiles { public ObjectSyncProfile[] Profiles; }
    [Serializable] public sealed class ObjectSyncProfile
    {
        public string Id, ModTitle, ModGuid;
        public ObjectSyncRule[] Rules;
    }
    [Serializable] public sealed class ObjectSyncRule
    {
        public string Id, SourceType, StaticField, ModRootName, ObjectName, NamePrefix;
        // Optional instance-field selector; no source-mod methods are invoked.
        public string ReferenceRootName;
        public string[] ReferenceFieldContains;
        // Flat, opt-in presentation settings. No nullable nested-class sentinels.
        public string ColliderMode = "None";
        public bool CarryLocalPlayer = false;
        public float MaxPassengerStepMetres = 3f;
        public float PresentationSmoothingSeconds = .15f;
        public string SpriteComponentType, SpriteDirectionField, SpriteFrameField, SpriteFlipField, SpriteTexturePattern;
        public string[] SpriteTextureFields;
        public int SpriteDirections = 8, SpriteAngleSign = 1;
        // Read-only local diagnostics. Never enrolls, reparents or publishes the source.
        public bool InspectOnly = false;
        // Auto, Exterior, Interior, Dungeon, or InteriorOrDungeon.
        public string Scope = "Auto";
        // Optional static member on SourceType exposing an X/Y map-pixel value.
        // Used only before first registration, never to hide another peer's replica.
        public string MapPixelField;
        // Auto, DaggerfallModel, DaggerfallBillboard, ModPrefab, or Custom.
        public string Visual = "Auto";
        public int ModelId, Archive, Record;
        public string PrefabAsset, FactoryId;
        // Optional presentation-only shader replacement for reconstructed copies.
        public string ShaderOverride;
        public bool SyncMaterials = false;
        public bool SyncActiveState = true, IncludeLights = true, IncludeAudio = true;
        public bool SyncParentActiveState = false;
        public bool TrackOriginalParentLifetime = true;
        // Opt-in live movement. Fixed remains the default for placed props.
        public string Motion = "Fixed";
        public bool PreserveSourceParent = false;
        // The current world API creates visuals within 80 m and presents them within 250 m.
        // A rule may reduce the presentation radius. No enemy conversion is performed.
        public float VisibilityMetres = 250;
    }
    [Serializable] public sealed class ObjectSyncVisual
    {
        // Local factory context, assigned from the receiver's matching profile, never from wire data.
        [NonSerialized] public Mod InstalledMod;
        [NonSerialized] public ObjectSyncRule LocalRule;
        [NonSerialized] public bool SmoothPresentation;
        public string Name, Visual, PrefabAsset, FactoryId, CustomData;
        public int ModelId, Archive, Record;
        public bool Active = true;
        public Vector3 Scale = Vector3.one;
        public ObjectSyncLight[] Lights;
        public ObjectSyncAudio[] Audio;
        public ObjectSyncMaterial[] Materials;
    }
    [Serializable] public sealed class ObjectSyncMaterial
    {
        public string Node, Shader, MainTexture;
        public int Slot;
        public Color Tint = Color.white;
        public Vector2 Tiling = Vector2.one, Offset;
    }
    [Serializable] public sealed class ObjectSyncLight
    {
        public Vector3 Position; public Quaternion Rotation;
        public Color Color; public float Intensity, Range, SpotAngle, ShadowStrength;
        public int Type, Shadows; public bool Enabled;
    }
    [Serializable] public sealed class ObjectSyncAudio
    {
        public Vector3 Position; public int Sound, Preset;
        public float Volume, MinDistance, MaxDistance, SpatialBlend;
        public bool Enabled, Mute;
    }

    // Names are deliberately prefixed so DFU does not send replica clicks to an unrelated
    // local mod's name-based activation callback. Gameplay interaction requires an adapter.
    public sealed class SyncedModObject : MonoBehaviour, IPlayerActivable
    {
        public string ProfileId, RuleId, Key;
        public uint OwnerPlayerId;
        public bool Replica;
        public void Activate(RaycastHit hit)
        {
            if (!Replica) return;
            if (!GenericModObjectSync.ActivateReplica(this, hit))
                DaggerfallUI.MessageBox("This is another player's synchronized object.");
        }
    }

    // Owned material copies avoid changing the target mod's shared prefab assets.
    public sealed class SyncedObjectMaterials : MonoBehaviour
    {
        public readonly List<Material> Owned = new List<Material>();
        void OnDestroy() { foreach (var material in Owned) if (material != null) Destroy(material); Owned.Clear(); }
    }

    [DefaultExecutionOrder(30000)]
    public sealed class GenericModObjectSync : MonoBehaviour
    {
        public static GenericModObjectSync Instance { get; private set; }
        public static event Action<SyncedModObject, RaycastHit> ReplicaActivated;
        // Profile-scoped adapters may decline a click without swallowing other profiles.
        static readonly Dictionary<string, Func<SyncedModObject, RaycastHit, bool>> activationHandlers =
            new Dictionary<string, Func<SyncedModObject, RaycastHit, bool>>(StringComparer.Ordinal);

        public static bool RegisterActivationHandler(string profileId, Func<SyncedModObject, RaycastHit, bool> handler)
        {
            if (string.IsNullOrEmpty(profileId) || handler == null || activationHandlers.ContainsKey(profileId)) return false;
            activationHandlers.Add(profileId, handler); return true;
        }
        public static void UnregisterActivationHandler(string profileId, Func<SyncedModObject, RaycastHit, bool> handler)
        {
            Func<SyncedModObject, RaycastHit, bool> current;
            if (profileId != null && activationHandlers.TryGetValue(profileId, out current) && current == handler)
                activationHandlers.Remove(profileId);
        }
        static readonly Dictionary<string, Func<ObjectSyncVisual, GameObject, GameObject>> factories =
            new Dictionary<string, Func<ObjectSyncVisual, GameObject, GameObject>>(StringComparer.Ordinal);
        static readonly Dictionary<string, Func<GameObject, string>> captureData =
            new Dictionary<string, Func<GameObject, string>>(StringComparer.Ordinal);
        static readonly Dictionary<string, Func<GameObject, ObjectSyncRule, string>> configuredCaptureData =
            new Dictionary<string, Func<GameObject, ObjectSyncRule, string>>(StringComparer.Ordinal);
        readonly HashSet<string> referenceReports = new HashSet<string>();
        readonly List<ProfileRuntime> profiles = new List<ProfileRuntime>();
        readonly Dictionary<int, Source> sources = new Dictionary<int, Source>();
        readonly List<Replica> replicas = new List<Replica>();
        readonly HashSet<string> inspected = new HashSet<string>();
        readonly HashSet<string> inspectedAssets = new HashSet<string>();
        readonly Dictionary<string, int> inspectionCounts = new Dictionary<string, int>();
        Transform objectRoot;
        GameObject localPlayer;
        float nextScan;
        bool stopped;
        const float ScanInterval = 1f;
        const int MaxSourceObjects = 256, MaxScanTransforms = 4096, MaxDataChars = 7000;

        sealed class ProfileRuntime
        {
            public ObjectSyncProfile Config; public ModWorld World;
            public readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();
            public readonly Dictionary<string, string> TextureAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        sealed class Source
        {
            public int InstanceId; public ProfileRuntime Profile; public ObjectSyncRule Rule;
            public GameObject Original, Proxy; public Transform OriginalParent;
            public Scene OriginalScene;
            public bool HadParent, Pending, Accepted, Removed;
            public string Key, LastData; public float RetryAt, UpdateAt;
            public ModWorldPose Pose; public Presentation Gate; public SyncedModObject Tag;
        }
        sealed class Replica
        {
            public GameObject Object; public ModWorldRecord Record;
            public ProfileRuntime Profile; public ObjectSyncRule Rule; public bool Active;
            public Transform Extras; public Light[] Lights; public DaggerfallAudioSource[] Sounds;
            public GameObject Presentation;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Instance != null) return;
            var go = new GameObject("Generic Mod Object Sync");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<GenericModObjectSync>();
        }
        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Debug.Log("[ModObjectSync] v0.15 loaded; waiting for gameplay before discovery or inspection.");
            MultiplayerAPI.SessionEnded += EndSession;
            var root = new GameObject("SyncedObjects");
            UnityEngine.Object.DontDestroyOnLoad(root);
            objectRoot = root.transform;
        }
        void Start()
        {
            foreach (TextAsset asset in Resources.LoadAll<TextAsset>("ModObjectSync")) LoadProfiles(asset.text, asset.name);
            string directory = Path.Combine(Application.streamingAssetsPath, "ModObjectSync");
            if (!Directory.Exists(directory)) return;
            foreach (string path in Directory.GetFiles(directory, "*.json"))
            {
                try
                {
                    if (new FileInfo(path).Length <= 262144) LoadProfiles(File.ReadAllText(path), Path.GetFileName(path));
                }
                catch (Exception error) { Debug.LogWarning("[ModObjectSync] Profile load failed: " + error.Message); }
            }
        }
        void LoadProfiles(string json, string name)
        {
            try
            {
                var file = JsonUtility.FromJson<ObjectSyncProfiles>(json);
                if (file == null || file.Profiles == null) return;
                foreach (var profile in file.Profiles) RegisterProfile(profile);
            }
            catch (Exception error) { Debug.LogWarning("[ModObjectSync] Invalid " + name + ": " + error.Message); }
        }
        public static bool RegisterFactory(string id, Func<ObjectSyncVisual, GameObject> factory)
        {
            return factory!=null && RegisterFactory(id,null,(visual,existing)=>existing!=null?existing:factory(visual));
        }
        public static bool RegisterFactory(string id, Func<GameObject,string> capture,
            Func<ObjectSyncVisual,GameObject,GameObject> factory)
        {
            if (string.IsNullOrEmpty(id) || id.Length>64 || factory == null || factories.ContainsKey(id)) return false;
            factories.Add(id,factory);
            if(capture!=null) captureData.Add(id,capture);
            return true;
        }
        public static bool RegisterProfile(ObjectSyncProfile profile)
        {
            if (Instance == null) Boot();
            return Instance.AddProfile(profile);
        }
        public static bool RegisterConfiguredFactory(string id, Func<GameObject,ObjectSyncRule,string> capture,
            Func<ObjectSyncVisual,GameObject,GameObject> factory)
        {
            if(capture==null || !RegisterFactory(id,null,factory))return false;
            configuredCaptureData.Add(id,capture);return true;
        }
        // Optional immediate enrollment for mods with a spawn callback. Discovery rules
        // remain available for closed-source mods; this never creates an SP object.
        public static bool Track(string profileId, string ruleId, GameObject source)
        {
            if (Instance == null || source == null || !Instance.LocalSceneReady()) return false;
            foreach (var profile in Instance.profiles) if (profile.Config.Id == profileId && profile.World.Ready && ActiveMod(profile.Config) != null)
            {
                var rule=FindRule(profile,ruleId);
                if(rule==null || !SameMapPixel(profile,rule)) return false;
                Instance.RegisterSource(profile,rule,source);
                return Instance.sources.ContainsKey(source.GetInstanceID());
            }
            return false;
        }
        // Snapshot factories must not publish this helper's temporary local culling
        // as an author-requested renderer change on another player's object.
        public static bool GetSourceRendererEnabled(Renderer renderer)
        {
            if (renderer == null) return false;
            if (Instance == null) return renderer.enabled;
            var marker = renderer.GetComponentInParent<SyncedModObject>();
            Source source;
            if (marker == null || marker.Replica || !Instance.sources.TryGetValue(marker.gameObject.GetInstanceID(), out source))
                return renderer.enabled;
            return source.Gate.RendererEnabled(renderer);
        }
        bool AddProfile(ObjectSyncProfile profile)
        {
            if (profile == null || string.IsNullOrEmpty(profile.Id) || profile.Id.Length > 40 ||
                profile.Rules == null || profile.Rules.Length == 0 ||
                (string.IsNullOrEmpty(profile.ModTitle) && string.IsNullOrEmpty(profile.ModGuid))) return false;
            foreach (var old in profiles) if (old.Config.Id == profile.Id) return false;
            var ids = new HashSet<string>();
            foreach (var rule in profile.Rules)
            {
                if (rule != null && string.IsNullOrEmpty(rule.Motion)) rule.Motion = "Fixed";
                if (rule != null && string.IsNullOrEmpty(rule.ColliderMode)) rule.ColliderMode = "None";
                if (rule != null && rule.MaxPassengerStepMetres == 0) rule.MaxPassengerStepMetres = 3f;
                if (rule != null && rule.PresentationSmoothingSeconds == 0) rule.PresentationSmoothingSeconds = .15f;
                if (rule != null && rule.SpriteDirections == 0) rule.SpriteDirections = 8;
                if (rule != null && rule.SpriteAngleSign == 0) rule.SpriteAngleSign = 1;
                if (rule == null || string.IsNullOrEmpty(rule.Id) || rule.Id.Length > 48 || !ids.Add(rule.Id) ||
                    (rule.ShaderOverride != null && rule.ShaderOverride.Length > 128) ||
                    (rule.Motion != "Fixed" && rule.Motion != "Smooth") ||
                    (rule.ColliderMode != "None" && rule.ColliderMode != "Mesh") ||
                    !Finite(rule.MaxPassengerStepMetres) || rule.MaxPassengerStepMetres < .1f || rule.MaxPassengerStepMetres > 10f ||
                    !Finite(rule.PresentationSmoothingSeconds) || rule.PresentationSmoothingSeconds < .05f || rule.PresentationSmoothingSeconds > .5f ||
                    (rule.ReferenceRootName != null && (rule.ReferenceRootName.Length > 128 || string.IsNullOrEmpty(rule.SourceType))) ||
                    (rule.ReferenceFieldContains != null && rule.ReferenceFieldContains.Length > 8) ||
                    float.IsNaN(rule.VisibilityMetres) || rule.VisibilityMetres < 1 || rule.VisibilityMetres > 250 ||
                    (rule.Scope!="Auto" && rule.Scope!="Exterior" && rule.Scope!="Interior" && rule.Scope!="Dungeon" && rule.Scope!="InteriorOrDungeon") ||
                    (rule.Visual=="ModPrefab" && string.IsNullOrEmpty(rule.PrefabAsset)) || (rule.Visual=="Custom" && string.IsNullOrEmpty(rule.FactoryId)) ||
                    (!string.IsNullOrEmpty(rule.StaticField) && string.IsNullOrEmpty(rule.SourceType)) ||
                    (string.IsNullOrEmpty(rule.StaticField) && string.IsNullOrEmpty(rule.ReferenceRootName) && string.IsNullOrEmpty(rule.ModRootName) && string.IsNullOrEmpty(rule.ObjectName) && string.IsNullOrEmpty(rule.NamePrefix))) return false;
            }
            var runtime = new ProfileRuntime { Config = profile };
            runtime.World = MultiplayerAPI.OpenWorld("dfucoop.objects." + profile.Id,
                (record, existing) => TryMakeReplica(runtime, record, existing));
            profiles.Add(runtime);
            foreach (var rule in profile.Rules) if (rule.InspectOnly)
                Debug.Log("[ModObjectSync][Inspect] Registered " + profile.Id + "/" + rule.Id + "; awaiting matching local object; read-only diagnostics, no synchronization.");
            return true;
        }
        static Mod ActiveMod(ObjectSyncProfile profile)
        {
            Mod mod = EnabledMod(profile);
            return mod != null && mod.IsReady ? mod : null;
        }
        static Mod EnabledMod(ObjectSyncProfile profile)
        {
            if (ModManager.Instance == null) return null;
            Mod mod = !string.IsNullOrEmpty(profile.ModGuid) ? ModManager.Instance.GetModFromGUID(profile.ModGuid) :
                ModManager.Instance.GetMod(profile.ModTitle);
            return mod != null && mod.Enabled ? mod : null;
        }
        static ObjectSyncRule FindRule(ProfileRuntime profile, string id)
        {
            foreach (var rule in profile.Config.Rules) if (rule.Id == id) return rule;
            return null;
        }
        static Type FindType(ProfileRuntime profile, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Type found;
            if (profile.Types.TryGetValue(name, out found) && found != null) return found;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                found = assembly.GetType(name, false);
                if (found != null) { profile.Types[name] = found; return found; }
            }
            return null;
        }
        static object ReadStatic(Type type, string member)
        {
            if (type == null || string.IsNullOrEmpty(member)) return null;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var field = type.GetField(member, flags);
            return field == null ? null : field.GetValue(null);
        }
        static GameObject AsObject(object value)
        {
            var go = value as GameObject;
            if (go != null) return go;
            var component = value as Component;
            return component != null ? component.gameObject : null;
        }
        static bool SameMapPixel(ProfileRuntime profile, ObjectSyncRule rule)
        {
            if (string.IsNullOrEmpty(rule.MapPixelField)) return true;
            var value = ReadStatic(FindType(profile, rule.SourceType), rule.MapPixelField);
            if (value == null) return false;
            var x = value.GetType().GetField("X"); var y = value.GetType().GetField("Y");
            var pixel = GameManager.Instance.PlayerGPS.CurrentMapPixel;
            return x != null && y != null && Convert.ToInt32(x.GetValue(value)) == pixel.X && Convert.ToInt32(y.GetValue(value)) == pixel.Y;
        }
        // Parent identity takes precedence over the observer's current coordinates.
        // A root/mod-root object has no historical room stamp: capture it while its own mod
        // presents it in the current room, or register it explicitly immediately after spawn.
        static bool SourceInCurrentArea(GameObject source, ObjectSyncRule rule)
        {
            Transform frame;
            string area = LootCatcher.CurrentCorpseArea(out frame);
            if (frame == null || area == null) return false;
            bool exterior = area == "E", dungeon = area.StartsWith("D:", StringComparison.Ordinal);
            if (rule.Scope == "Exterior" && !exterior || rule.Scope == "Interior" && (exterior || dungeon) ||
                rule.Scope == "Dungeon" && !dungeon || rule.Scope == "InteriorOrDungeon" && exterior) return false;
            for (Transform p = source.transform.parent; p != null; p = p.parent)
            {
                if (p.GetComponent<DaggerfallTerrain>() != null && !exterior) return false;
                if (p.GetComponent<DaggerfallInterior>() != null && (exterior || dungeon || p != frame)) return false;
                if (p.GetComponent<DaggerfallDungeon>() != null && (!dungeon || p != frame)) return false;
            }
            return true;
        }
        IEnumerable<GameObject> Discover(ProfileRuntime profile, ObjectSyncRule rule)
        {
            if (!string.IsNullOrEmpty(rule.ReferenceRootName))
            {
                var root=GameObject.Find(rule.ReferenceRootName);
                if(root==null){ReferenceReport(profile,rule,"root","controller root not present: '"+rule.ReferenceRootName+"'");yield break;}
                var candidates=new List<GameObject>();var emitted=new HashSet<int>();bool foundComponent=false;
                foreach(var component in root.GetComponents<Component>()){
                    if(component==null || (component.GetType().Name!=rule.SourceType && component.GetType().FullName!=rule.SourceType))continue;
                    foundComponent=true;
                    foreach(var reference in RuntimeObjectFields.Read(component)){
                        var go=AsObject(reference.Value);if(go==null||go==root||!go.scene.IsValid()||!go.scene.isLoaded||!BoundedVisual(go))continue;
                        bool matches=rule.ReferenceFieldContains==null||rule.ReferenceFieldContains.Length==0;
                        if(rule.ReferenceFieldContains!=null)foreach(string word in rule.ReferenceFieldContains)
                            if(!string.IsNullOrEmpty(word)&&word.Length<=64&&reference.Path.IndexOf(word,StringComparison.OrdinalIgnoreCase)>=0)matches=true;
                        ReferenceReport(profile,rule,"field:"+reference.Path+":"+go.GetInstanceID(),"field="+reference.Path+" object='"+InspectText(go.name)+"' active="+go.activeInHierarchy+" selected="+matches);
                        if(matches&&emitted.Add(go.GetInstanceID()))candidates.Add(go);
                    }
                }
                if(!foundComponent)ReferenceReport(profile,rule,"component","controller component not found: "+rule.SourceType);
                // Use the outermost selected visual, never a second record for its body/wheels.
                foreach(var go in candidates){bool nested=false;foreach(var parent in candidates)if(parent!=go&&go.transform.IsChildOf(parent.transform)){nested=true;break;}if(!nested)yield return go;}
                yield break;
            }
            if (!string.IsNullOrEmpty(rule.StaticField))
            {
                object value = ReadStatic(FindType(profile, rule.SourceType), rule.StaticField);
                var single = AsObject(value);
                if (single != null) { yield return single; yield break; }
                var sequence = value as IEnumerable;
                if (sequence != null) foreach (object element in sequence) { var item = AsObject(element); if (item != null) yield return item; }
                yield break;
            }
            if (!string.IsNullOrEmpty(rule.ModRootName))
            {
                GameObject root = GameObject.Find(rule.ModRootName);
                if (root == null) yield break;
                if(string.IsNullOrEmpty(rule.ObjectName) && string.IsNullOrEmpty(rule.NamePrefix))
                {
                    for(int i=0;i<root.transform.childCount && i<MaxScanTransforms;i++) yield return root.transform.GetChild(i).gameObject;
                    yield break;
                }
                var stack = new Stack<Transform>(); stack.Push(root.transform); int visited = 0;
                while (stack.Count > 0 && visited++ < MaxScanTransforms)
                {
                    var node = stack.Pop();
                    if (node.GetComponent<SyncedModObject>() != null) continue;
                    if (Matches(node.name, rule)) { yield return node.gameObject; continue; }
                    for (int i = node.childCount - 1; i >= 0; i--) stack.Push(node.GetChild(i));
                }
                yield break;
            }
            // A bounded scan of scene roots only; never a periodic walk over the whole world.
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) yield break;
            foreach (var root in scene.GetRootGameObjects()) if (Matches(root.name, rule)) yield return root;
        }
        static bool Matches(string name, ObjectSyncRule rule)
        {
            return !string.IsNullOrEmpty(rule.ObjectName) && name == rule.ObjectName ||
                !string.IsNullOrEmpty(rule.NamePrefix) && name.StartsWith(rule.NamePrefix, StringComparison.Ordinal);
        }
        void ReferenceReport(ProfileRuntime profile,ObjectSyncRule rule,string key,string message)
        {
            if(referenceReports.Count<128&&referenceReports.Add(profile.Config.Id+"/"+rule.Id+":"+key))
                Debug.Log("[ModObjectSync][References] "+profile.Config.Id+"/"+rule.Id+" "+message);
        }
        static bool BoundedVisual(GameObject go)
        {
            var pending=new Stack<Transform>();pending.Push(go.transform);int visited=0;bool rendered=false;
            while(pending.Count>0&&visited++<48){var node=pending.Pop();if(node.childCount>48)return false;if(node.GetComponent<MeshRenderer>()!=null)rendered=true;for(int i=0;i<node.childCount;i++)pending.Push(node.GetChild(i));}
            return rendered&&pending.Count==0;
        }
        void LateUpdate()
        {
            if (stopped || objectRoot == null) return;
            objectRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity); objectRoot.localScale = Vector3.one;
            if (!LocalSceneReady()) return;
            foreach (var source in new List<Source>(sources.Values))
            {
                try { TickSource(source); }
                catch(Exception error) { WarnOnce(source.Profile.Config.Id+"/"+source.Rule.Id,error.Message); RemoveSource(source,false); }
            }
            for (int i = replicas.Count - 1; i >= 0; i--)
            {
                var replica = replicas[i];
                if (replica.Object == null) { replicas.RemoveAt(i); continue; }
                Vector3 point; Quaternion rotation;
                bool enabled=ActiveMod(replica.Profile.Config)!=null;
                var binding=replica.Object.GetComponent<ModWorldBinding>();
                var current = binding != null && binding.Record != null ? binding.Record : replica.Record;
                if(enabled && binding!=null && binding.Record!=null && binding.Record.Data!=replica.Record.Data)
                    TryMakeReplica(replica.Profile,binding.Record,replica.Object);
                bool visible = enabled && current.Pose.TryLocal(out point, out rotation) &&
                    WithinRadius(point, replica.Rule.VisibilityMetres);
                // Do not independently toggle renderer/collider enabled flags: the API
                // already owns those flags and must retain its original culling states.
                replica.Object.SetActive(visible && replica.Active);
            }
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + ScanInterval;
            foreach (var profile in profiles)
            {
                foreach (var rule in profile.Config.Rules)
                {
                    if (rule.InspectOnly ? EnabledMod(profile.Config) == null : ActiveMod(profile.Config) == null)
                    {
                        if (rule.InspectOnly) InspectionWait(profile, rule, "matching mod is missing or disabled; check the exact ModTitle");
                        continue;
                    }
                    if (!rule.InspectOnly && !profile.World.Ready) continue;
                    if (!SameMapPixel(profile, rule)) continue;
                    bool found = false;
                    foreach (var go in Discover(profile, rule)) { found = true; RegisterSource(profile, rule, go); }
                    if (rule.InspectOnly && !found) InspectionWait(profile, rule, "no active object matches the selector yet");
                }
            }
        }
        static bool WithinRadius(Vector3 point, float radius)
        {
            if (Instance == null || Instance.localPlayer == null) return false;
            var delta = point - Instance.localPlayer.transform.position;
            Transform frame; if (LootCatcher.CurrentCorpseArea(out frame) == "E") delta.y = 0;
            return delta.sqrMagnitude <= radius * radius;
        }
        bool LocalSceneReady()
        {
            // Instance creates GameManager when absent, and PlayerObject logs/throws
            // when no tagged player exists. Neither is a harmless menu null check.
            if (!GameManager.HasInstance || SaveLoadManager.Instance == null || SaveLoadManager.Instance.LoadInProgress)
            {
                localPlayer = null;
                return false;
            }
            if (!GameManager.Instance.IsPlayingGame())
            {
                localPlayer = null;
                return false;
            }
            if (localPlayer == null || !localPlayer.activeInHierarchy)
                localPlayer = GameObject.FindGameObjectWithTag("Player");
            return localPlayer != null;
        }
        void RegisterSource(ProfileRuntime profile, ObjectSyncRule rule, GameObject go)
        {
            if (rule.InspectOnly)
            {
                InspectSource(profile, rule, go);
                return;
            }
            if (go == null || !go.scene.IsValid() || !go.scene.isLoaded || !go.activeInHierarchy || sources.ContainsKey(go.GetInstanceID()) || sources.Count >= MaxSourceObjects ||
                go.GetComponent<SyncedModObject>() != null || go.GetComponentInParent<SyncedModObject>() != null ||
                go.GetComponentInParent<Mirror.NetworkIdentity>() != null || go.GetComponent<DaggerfallLoot>() != null ||
                go.GetComponent<DaggerfallEntityBehaviour>() != null ||
                !SourceInCurrentArea(go, rule)) return;
            if(!string.IsNullOrEmpty(rule.ReferenceRootName))foreach(var tracked in sources.Values)if(tracked.Original!=null &&
                (go.transform.IsChildOf(tracked.Original.transform)||tracked.Original.transform.IsChildOf(go.transform)))return;
            var source = new Source { InstanceId = go.GetInstanceID(), Original = go, Profile = profile, Rule = rule,
                OriginalParent = go.transform.parent, HadParent = go.transform.parent != null,
                OriginalScene = go.scene,
                Pose = ModWorldPose.Capture(go.transform), Gate = new Presentation(go) };
            // Refuse an unsupported recipe before altering the original object.
            try { Capture(source); }
            catch (Exception error) { WarnOnce(profile.Config.Id + "/" + rule.Id, error.Message); return; }
            sources.Add(source.InstanceId, source); TickSource(source);
        }
        // Inspection reads metadata and already-created objects only. It does not load
        // prefabs, invoke mod methods/properties, instantiate visuals or read pixels.
        // Names and field references are clues, not proof of a loadable asset recipe.
        void InspectSource(ProfileRuntime profile, ObjectSyncRule rule, GameObject go)
        {
            if (go == null || !go.scene.IsValid() || !go.scene.isLoaded || !go.activeInHierarchy) return;
            if (go.GetComponentInParent<SyncedModObject>() != null) { InspectionWait(profile, rule, "matching object is already managed by object sync"); return; }
            if (!SourceInCurrentArea(go, rule)) { InspectionWait(profile, rule, "matching object is outside the current location/scope or its location frame is not ready"); return; }
            // Some SP mods never set IsReady. Existing-object metadata can still be
            // inspected once gameplay and an enabled matching mod are present.
            Mod mod = EnabledMod(profile.Config);
            if (mod == null) return;
            string key = profile.Config.Id + "/" + rule.Id;
            int count; inspectionCounts.TryGetValue(key, out count);
            if (count >= 8 || inspected.Count >= 128 || !inspected.Add(key + "/" + go.GetInstanceID())) return;
            inspectionCounts[key] = count + 1;
            Action<string> log = message => Debug.Log("[ModObjectSync][Inspect] " + key + " " + message);
            log("BEGIN object='" + InspectText(go.name) + "' modIsReady=" + mod.IsReady + " local-read-only=True position=" + go.transform.position +
                " rotation=" + go.transform.rotation + " scale=" + go.transform.lossyScale +
                " parent='" + (go.transform.parent != null ? InspectText(go.transform.parent.name) : "<scene root>") + "'");
            try
            {
                if (inspectedAssets.Add(profile.Config.Id))
                {
                    string[] names = mod.AssetNames ?? new string[0];
                    log("MOD-ASSETS total=" + names.Length + "; listing up to 512 visual/data asset names; no assets loaded");
                    int listed = 0;
                    foreach (string name in names)
                    {
                        string extension = Path.GetExtension(name ?? "").ToLowerInvariant();
                        if (extension != ".prefab" && extension != ".mat" && extension != ".png" && extension != ".dds" &&
                            extension != ".tga" && extension != ".jpg" && extension != ".jpeg" && extension != ".bmp" &&
                            extension != ".asset" && extension != ".shader" && extension != ".cif" && extension != ".img") continue;
                        if (listed++ >= 512) { log("MOD-ASSETS truncated"); break; }
                        log("ASSET '" + InspectText(name) + "'");
                    }
                }
                var stack = new Stack<Transform>(); stack.Push(go.transform);
                int visited = 0;
                while (stack.Count > 0 && visited++ < 48)
                {
                    Transform node = stack.Pop();
                    log("NODE '" + InspectText(node.name) + "' localPosition=" + node.localPosition +
                        " localRotation=" + node.localRotation + " localScale=" + node.localScale +
                        " activeSelf=" + node.gameObject.activeSelf);
                    var mesh = node.GetComponent<MeshFilter>();
                    if (mesh != null && mesh.sharedMesh != null)
                        log("MESH '" + InspectText(mesh.sharedMesh.name) + "' vertices=" + mesh.sharedMesh.vertexCount +
                            " bounds=" + mesh.sharedMesh.bounds);
                    int components = 0;
                    foreach (Component component in node.gameObject.GetComponents<Component>())
                    {
                        if (components++ >= 24) { log("COMPONENTS truncated"); break; }
                        if (component == null) { log("COMPONENT <missing script>"); continue; }
                        Type type = component.GetType();
                        log("COMPONENT " + type.FullName);
                        var billboard = component as DaggerfallBillboard;
                        if (billboard != null) log("BILLBOARD summaryArchive=" + billboard.Summary.Archive + " summaryRecord=" + billboard.Summary.Record);
                        var box = component as BoxCollider;
                        if (box != null) log("BOX center=" + box.center + " size=" + box.size + " trigger=" + box.isTrigger);
                        var renderer = component as Renderer;
                        if (renderer != null)
                        {
                            Material[] materials = renderer.sharedMaterials;
                            for (int i = 0; materials != null && i < materials.Length && i < 8; i++)
                            {
                                Material material = materials[i]; if (material == null) continue;
                                log("MATERIAL slot=" + i + " name='" + InspectText(material.name) + "' shader='" +
                                    (material.shader != null ? InspectText(material.shader.name) : "<none>") + "'");
                                foreach (string property in new[] { "_MainTex", "_BumpMap", "_EmissionMap" })
                                {
                                    if (!material.HasProperty(property)) continue;
                                    Texture texture = material.GetTexture(property);
                                    if (texture != null) log("TEXTURE property=" + property + " " + InspectValue(texture, 0) +
                                        " scale=" + material.GetTextureScale(property) + " offset=" + material.GetTextureOffset(property));
                                }
                            }
                        }
                        // Read only fields declared by mod components. No property getters,
                        // unknown enumerable iterators or gameplay methods are called.
                        if (!(component is MonoBehaviour) || type.Assembly == typeof(Transform).Assembly ||
                            type.Assembly == typeof(DaggerfallBillboard).Assembly || type.Assembly == typeof(GenericModObjectSync).Assembly) continue;
                        int fields = 0;
                        for (Type declared = type; declared != null && declared != typeof(MonoBehaviour); declared = declared.BaseType)
                        {
                            foreach (var field in declared.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                            {
                                if (fields++ >= 32) break;
                                try { log("FIELD " + declared.FullName + "." + field.Name + "=" + InspectValue(field.GetValue(component), 0)); }
                                catch (Exception error) { log("FIELD " + field.Name + " unreadable=" + error.GetType().Name); }
                            }
                            if (fields >= 32) { log("FIELDS truncated"); break; }
                        }
                    }
                    for (int i = node.childCount - 1; i >= 0 && i >= node.childCount - 48; i--) stack.Push(node.GetChild(i));
                }
                if (stack.Count > 0) log("NODES truncated after 48");
            }
            catch (Exception error) { log("READ-ERROR " + error.GetType().Name + ": " + InspectText(error.Message)); }
            log("END; no source changed and no network object published");
        }
        static string InspectText(string value)
        {
            if (value == null) return "<null>";
            value = value.Replace('\r', ' ').Replace('\n', ' ');
            return value.Length <= 180 ? value : value.Substring(0, 180) + "...";
        }

        readonly HashSet<string> inspectionWaits = new HashSet<string>();
        void InspectionWait(ProfileRuntime profile, ObjectSyncRule rule, string reason)
        {
            string key = profile.Config.Id + "/" + rule.Id + " " + reason;
            if (inspectionWaits.Count < 128 && inspectionWaits.Add(key))
                Debug.Log("[ModObjectSync][Inspect] WAIT " + key + " (reported once)");
        }
        static string InspectValue(object value, int depth)
        {
            if (value == null) return "<null>";
            var texture = value as Texture;
            if (texture != null) return texture.GetType().Name + " '" + InspectText(texture.name) + "' " + texture.width + "x" + texture.height;
            var asset = value as UnityEngine.Object;
            if (asset != null) return asset.GetType().Name + " '" + InspectText(asset.name) + "'";
            var array = value as Array;
            if (array != null)
            {
                string description = value.GetType().Name + " length=" + array.Length;
                if (depth >= 2 || array.Rank != 1) return description;
                var text = new System.Text.StringBuilder(description + " [");
                for (int i = 0; i < Math.Min(array.Length, 4); i++)
                    text.Append(i == 0 ? "" : ", ").Append(InspectValue(array.GetValue(i + array.GetLowerBound(0)), depth + 1));
                return text.Append(array.Length > 4 ? ", ...]" : "]").ToString();
            }
            Type type = value.GetType();
            if (value is string || type.IsPrimitive || type.IsEnum || value is Vector3 || value is Quaternion || value is Color)
                return InspectText(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
            return "<" + type.FullName + ">";
        }

        readonly HashSet<string> warned = new HashSet<string>();
        void WarnOnce(string key, string message) { if (warned.Add(key)) Debug.LogWarning("[ModObjectSync] " + key + ": " + message); }
        void TickSource(Source source)
        {
            if (source.Removed) return;
            if (source.Original == null || source.Rule.TrackOriginalParentLifetime &&
                (source.HadParent && source.OriginalParent == null || !source.OriginalScene.isLoaded))
            {
                RemoveSource(source, source.Original != null); return;
            }
            if (ActiveMod(source.Profile.Config) == null) { RemoveSource(source, false); return; }
            if (!source.Profile.World.Ready) { source.Gate.Show(true); return; }
            if (source.Proxy == null)
            {
                source.Pending = source.Accepted = false;
                if (Time.unscaledTime < source.RetryAt) return;
                Vector3 point; Quaternion rotation;
                if (!source.Pose.TryLocal(out point, out rotation) || !WithinRadius(point, 79)) return;
                source.RetryAt = Time.unscaledTime + 5;
                source.Proxy = new GameObject("MP_ObjectAnchor");
                source.Proxy.transform.SetParent(objectRoot, false);
                source.Proxy.transform.SetPositionAndRotation(point, rotation);
                source.Key = Guid.NewGuid().ToString("N"); source.Pending = true;
                source.Proxy.name="MP_ObjectAnchor_"+source.Profile.Config.Id+"_"+source.Rule.Id+"_"+source.Key.Substring(0,8);
                string data;
                try { data = Capture(source); } catch (Exception error) { RemoveSource(source, false); WarnOnce(source.Rule.Id, error.Message); return; }
                source.LastData = data;
                source.Profile.World.Publish(source.Proxy, source.Key, source.Rule.Id, data, true, false, 0,
                    (ok, record) => {
                        if (source.Removed || source.Original == null)
                        {
                            if (ok) source.Profile.World.Remove(source.Key);
                            return;
                        }
                        source.Pending = false; source.Accepted = ok;
                        if (!ok) { Destroy(source.Proxy); source.Proxy = null; return; }
                        source.Pose = record.Pose;
                        if (!source.Rule.PreserveSourceParent) source.Original.transform.SetParent(objectRoot, true);
                        if (source.Tag == null) source.Tag = source.Original.AddComponent<SyncedModObject>();
                        source.Tag.ProfileId = source.Profile.Config.Id; source.Tag.RuleId = source.Rule.Id;
                        source.Tag.Key = source.Key; source.Tag.OwnerPlayerId = record.OwnerPlayerId;
                        Debug.Log("[ModObjectSync] Published " + source.Profile.Config.Id + "/" + source.Rule.Id +
                            " owner=" + record.OwnerPlayerId + " motion=" + record.Motion + " source='" + InspectText(source.Original.name) + "'.");
                    }, source.Rule.Motion == "Smooth" ? ModWorldMotion.Smooth : ModWorldMotion.Fixed);
                return;
            }
            if (!source.Accepted) return;
            if (source.Rule.Motion == "Smooth")
            {
                // A moving source remains under its mod's control. The separate API anchor
                // follows it; never apply an older network pose back to the original.
                if (!SourceInCurrentArea(source.Original, source.Rule)) return;
                source.Proxy.transform.SetPositionAndRotation(source.Original.transform.position, source.Original.transform.rotation);
                source.Pose = ModWorldPose.Capture(source.Original.transform);
                if (Time.unscaledTime < source.UpdateAt) return;
                source.UpdateAt = Time.unscaledTime + .1f;
                string live = Capture(source);
                if (live != source.LastData) { source.LastData = live; source.Profile.World.UpdateData(source.Key, live); }
                return;
            }
            Vector3 target; Quaternion turn;
            bool sameArea = source.Pose.TryLocal(out target, out turn);
            if (sameArea)
            {
                // DFU billboards rotate toward the local camera in Update. Keep their
                // position anchored without replacing that presentation rotation later.
                if (source.Original.GetComponent<DaggerfallBillboard>() != null)
                    source.Original.transform.position = target;
                else source.Original.transform.SetPositionAndRotation(target, turn);
            }
            source.Gate.Show(sameArea && WithinRadius(target, source.Rule.VisibilityMetres) &&
                (!source.HadParent || source.OriginalParent == null || source.OriginalParent.gameObject.activeInHierarchy));
            if (Time.unscaledTime < source.UpdateAt) return;
            source.UpdateAt = Time.unscaledTime + .25f;
            string updated = Capture(source);
            if (updated != source.LastData) { source.LastData = updated; source.Profile.World.UpdateData(source.Key, updated); }
        }
        void RemoveSource(Source source, bool destroyOriginal)
        {
            source.Removed = true; sources.Remove(source.InstanceId);
            if (!string.IsNullOrEmpty(source.Key)) source.Profile.World.Remove(source.Key);
            source.Gate.Show(true);
            if (source.Original != null)
            {
                if (destroyOriginal) Destroy(source.Original);
                else source.Original.transform.SetParent(source.OriginalParent, true);
            }
            if (source.Tag != null) Destroy(source.Tag);
            if (source.Proxy != null) Destroy(source.Proxy);
        }
        public static bool ActivateReplica(SyncedModObject replica, RaycastHit hit)
        {
            if (replica == null || !replica.Replica) return false;
            Func<SyncedModObject, RaycastHit, bool> adapter;
            if (replica.ProfileId != null && activationHandlers.TryGetValue(replica.ProfileId, out adapter) && adapter(replica, hit))
                return true;
            var handler = ReplicaActivated;
            if (handler == null) return false;
            handler(replica, hit); return true;
        }
        void EndSession()
        {
            localPlayer = null;
            // The API owns disposable anchors, not the mod's original objects.
            foreach (var source in sources.Values)
            {
                source.Removed = true; source.Gate.Show(true);
                if (source.Original != null) source.Original.transform.SetParent(source.OriginalParent, true);
                if (source.Tag != null) Destroy(source.Tag);
            }
            sources.Clear(); replicas.Clear();
        }
        void OnDestroy()
        {
            if (Instance != this) return;
            stopped = true; MultiplayerAPI.SessionEnded -= EndSession; EndSession();
            foreach (var profile in profiles) profile.World.Dispose();
            if (objectRoot != null) Destroy(objectRoot.gameObject);
            Instance = null;
        }

        // Presentation has no world position or authority logic. It also gates lights/audio,
        // which ordinary mesh/collider culling would leave visible/audible in another area.
        sealed class Presentation
        {
            readonly Renderer[] renderers; readonly Collider[] colliders; readonly Light[] lights; readonly AudioSource[] audio;
            readonly bool[] r, c, l, a;
            public bool Hidden { get; private set; }
            public Presentation(GameObject go)
            {
                renderers = go.GetComponentsInChildren<Renderer>(true); colliders = go.GetComponentsInChildren<Collider>(true);
                lights = go.GetComponentsInChildren<Light>(true); audio = go.GetComponentsInChildren<AudioSource>(true);
                r = new bool[renderers.Length]; c = new bool[colliders.Length]; l = new bool[lights.Length]; a = new bool[audio.Length];
            }
            public void Show(bool show)
            {
                if (Hidden == !show) return;
                for (int i=0;i<r.Length;i++) if (renderers[i]!=null) { if(!show) r[i]=renderers[i].enabled; renderers[i].enabled=show?r[i]:false; }
                for (int i=0;i<c.Length;i++) if (colliders[i]!=null) { if(!show) c[i]=colliders[i].enabled; colliders[i].enabled=show?c[i]:false; }
                for (int i=0;i<l.Length;i++) if (lights[i]!=null) { if(!show) l[i]=lights[i].enabled; lights[i].enabled=show?l[i]:false; }
                for (int i=0;i<a.Length;i++) if (audio[i]!=null) { if(!show) a[i]=audio[i].mute; audio[i].mute=show?a[i]:true; }
                Hidden = !show;
            }
            public bool LightEnabled(Light light) { for(int i=0;i<lights.Length;i++) if(lights[i]==light) return Hidden?l[i]:light.enabled; return light.enabled; }
            public bool RendererEnabled(Renderer renderer) { for(int i=0;i<renderers.Length;i++) if(renderers[i]==renderer) return Hidden?r[i]:renderer.enabled; return renderer.enabled; }
            public bool AudioMute(AudioSource source) { for(int i=0;i<audio.Length;i++) if(audio[i]==source) return Hidden?a[i]:source.mute; return source.mute; }
        }

        string Capture(Source source)
        {
            var go = source.Original; var rule = source.Rule;
            var visual = new ObjectSyncVisual { Name = go.name, Scale = go.transform.lossyScale,
                Active = (!rule.SyncActiveState || go.activeSelf) && (!rule.SyncParentActiveState ||
                    !source.HadParent || source.OriginalParent == null || source.OriginalParent.gameObject.activeInHierarchy), Visual = rule.Visual,
                ModelId = rule.ModelId, Archive = rule.Archive, Record = rule.Record,
                PrefabAsset = rule.PrefabAsset, FactoryId = rule.FactoryId };
            if (visual.Visual == "Auto")
            {
                var billboard = go.GetComponent<DaggerfallBillboard>();
                if (billboard != null) { visual.Visual = "DaggerfallBillboard"; visual.Archive = billboard.Summary.Archive; visual.Record = billboard.Summary.Record; }
                else
                {
                    int first = go.name.IndexOf("[ID=", StringComparison.Ordinal), last = go.name.IndexOf(']'); int model;
                    if (first >= 0 && last > first+4 && int.TryParse(go.name.Substring(first+4,last-first-4), out model))
                    { visual.Visual = "DaggerfallModel"; visual.ModelId = model; }
                    else throw new InvalidOperationException("No reconstruction recipe. Configure a model, billboard, visual prefab or custom factory.");
                }
            }
            if (visual.Visual != "DaggerfallModel" && visual.Visual != "DaggerfallBillboard" && visual.Visual != "ModPrefab" && visual.Visual != "Custom")
                throw new InvalidOperationException("Unknown visual recipe: " + visual.Visual);
            if(visual.Visual=="Custom") {
                Func<GameObject,string> capture;
                Func<GameObject,ObjectSyncRule,string> configured;
                if(configuredCaptureData.TryGetValue(rule.FactoryId,out configured))visual.CustomData=configured(go,rule);
                else if(captureData.TryGetValue(rule.FactoryId,out capture)) visual.CustomData=capture(go);
                if(!factories.ContainsKey(rule.FactoryId)) throw new InvalidOperationException("Custom factory is not registered: "+rule.FactoryId);
            }
            if (rule.IncludeLights)
            {
                var lights = go.GetComponentsInChildren<Light>(true); if(lights.Length>8) throw new InvalidOperationException("More than eight lights; use a custom visual factory.");
                visual.Lights = new ObjectSyncLight[lights.Length];
                for (int i=0;i<lights.Length;i++) { var light=lights[i]; visual.Lights[i]=new ObjectSyncLight {
                    Position=go.transform.InverseTransformPoint(light.transform.position), Rotation=Quaternion.Inverse(go.transform.rotation)*light.transform.rotation,
                    Color=light.color, Intensity=light.intensity, Range=light.range, SpotAngle=light.spotAngle, ShadowStrength=light.shadowStrength,
                    Type=(int)light.type, Shadows=(int)light.shadows, Enabled=source.Gate.LightEnabled(light) }; }
            }
            if (rule.IncludeAudio)
            {
                var sounds=go.GetComponentsInChildren<DaggerfallAudioSource>(true); if(sounds.Length>4) throw new InvalidOperationException("More than four DFU sounds; use a custom visual factory.");
                visual.Audio=new ObjectSyncAudio[sounds.Length];
                for(int i=0;i<sounds.Length;i++) { var sound=sounds[i]; var audio=sound.AudioSource;
                    visual.Audio[i]=new ObjectSyncAudio { Position=go.transform.InverseTransformPoint(sound.transform.position), Sound=sound.SoundIndex, Preset=(int)sound.Preset,
                        Volume=audio.volume, MinDistance=audio.minDistance, MaxDistance=audio.maxDistance, SpatialBlend=audio.spatialBlend, Enabled=sound.enabled, Mute=source.Gate.AudioMute(audio) }; }
            }
            if (rule.SyncMaterials) visual.Materials = CaptureMaterials(source.Profile, go);
            string data=JsonUtility.ToJson(visual);
            if(!ValidVisual(visual)) throw new InvalidOperationException("Unsupported or out-of-bounds visual presentation data.");
            if(data.Length>MaxDataChars) throw new InvalidOperationException("Visual description exceeds the 7000-character helper limit.");
            return data;
        }
        GameObject TryMakeReplica(ProfileRuntime profile, ModWorldRecord record, GameObject existing)
        {
            try { return MakeReplica(profile,record,existing); }
            catch(ObjectSyncPendingException) { return existing; }
            catch(Exception error) { WarnOnce(profile.Config.Id+"/"+record.Kind,error.Message); return existing; }
        }
        GameObject MakeReplica(ProfileRuntime profile, ModWorldRecord record, GameObject existing)
        {
            var rule=FindRule(profile,record.Kind); Mod mod=ActiveMod(profile.Config);
            if(rule==null || rule.InspectOnly || mod==null || record.Data==null || record.Data.Length>MaxDataChars) return null;
            var visual=JsonUtility.FromJson<ObjectSyncVisual>(record.Data);
            if(visual==null || !ValidVisual(visual)) return null;
            visual.InstalledMod = mod;
            visual.LocalRule = rule; visual.SmoothPresentation = record.Motion == ModWorldMotion.Smooth;
            if(rule.Visual!="Auto" && visual.Visual!=rule.Visual) return null;
            if(rule.Visual=="DaggerfallModel" && visual.ModelId!=rule.ModelId ||
                rule.Visual=="DaggerfallBillboard" && (visual.Archive!=rule.Archive || visual.Record!=rule.Record)) return null;
            GameObject go=existing;
            if(go==null)
            {
                try
                {
                if(visual.Visual=="DaggerfallModel") {
                    go=MeshReplacement.ImportCustomGameobject((uint)visual.ModelId,null,Matrix4x4.identity);
                    if(go==null) go=GameObjectHelper.CreateDaggerfallMeshGameObject((uint)visual.ModelId,null);
                }
                else if(visual.Visual=="DaggerfallBillboard") {
                    go=MeshReplacement.ImportCustomFlatGameobject(visual.Archive,visual.Record,Vector3.zero,null);
                    if(go==null) go=GameObjectHelper.CreateDaggerfallBillboardGameObject(visual.Archive,visual.Record,null);
                }
                else if(visual.Visual=="ModPrefab" && visual.PrefabAsset==rule.PrefabAsset) {
                    var prefab=mod.GetAsset<GameObject>(rule.PrefabAsset);
                    if(prefab!=null) go=CloneVisualPrefab(prefab);
                }
                else if(visual.Visual=="Custom" && visual.FactoryId==rule.FactoryId) {
                    Func<ObjectSyncVisual,GameObject,GameObject> create;
                    if(factories.TryGetValue(rule.FactoryId,out create)) go=create(visual,null);
                }
                if(go==null) return null;
                GameObject presentation = go;
                if (visual.Visual == "DaggerfallBillboard") go = WrapBillboardPresentation(go);
                if (rule.SyncMaterials) ApplyMaterials(profile, rule, presentation, visual.Materials);
                else ApplyShaderOverride(go,rule.ShaderOverride);
                go.transform.SetParent(objectRoot,false);
                // Configured prefabs/factories must contain presentation code only.
                // This helper never serializes or attaches the source's gameplay scripts.
                foreach(var node in go.GetComponentsInChildren<Transform>(true)) node.name="MP_Synced_"+node.name;
                var tag=go.AddComponent<SyncedModObject>(); tag.ProfileId=profile.Config.Id; tag.RuleId=rule.Id;
                tag.Key=record.Key; tag.OwnerPlayerId=record.OwnerPlayerId; tag.Replica=true;
                foreach(var collider in go.GetComponentsInChildren<Collider>(true)) {
                    if(collider.GetComponent<IPlayerActivable>()==null) {
                        var click=collider.gameObject.GetComponent<SyncedModObject>();
                        if(click==null) click=collider.gameObject.AddComponent<SyncedModObject>();
                        click.ProfileId=tag.ProfileId; click.RuleId=tag.RuleId; click.Key=tag.Key; click.OwnerPlayerId=tag.OwnerPlayerId; click.Replica=true;
                    }
                }
                if(visual.Lights!=null) foreach(var old in go.GetComponentsInChildren<Light>(true)) old.enabled=false;
                if(visual.Audio!=null) foreach(var old in go.GetComponentsInChildren<AudioSource>(true)) old.mute=true;
                var replica=new Replica {Object=go,Presentation=presentation,Record=record,Rule=rule,Profile=profile,Active=visual.Active};
                ApplyPresentationExtras(go,visual,replica);
                replicas.Add(replica);
                }
                catch { if (go != null) Destroy(go); throw; }
            }
            else {
                if(visual.Visual=="Custom" && visual.FactoryId==rule.FactoryId) {
                    Func<ObjectSyncVisual,GameObject,GameObject> update;
                    if(!factories.TryGetValue(rule.FactoryId,out update) || update(visual,go)!=go)
                        throw new InvalidOperationException("A custom update must return the supplied existing visual.");
                }
                foreach(var replica in replicas) if(replica.Object==go) {
                    if (rule.SyncMaterials) ApplyMaterials(profile, rule, replica.Presentation, visual.Materials);
                    replica.Record=record; replica.Active=visual.Active; ApplyPresentationExtras(go,visual,replica); break;
                }
            }
            go.transform.localScale=visual.Scale;
            go.SetActive(visual.Active);
            return go;
        }
        static bool ValidVisual(ObjectSyncVisual visual)
        {
            bool valid=visual.Name!=null && visual.Name.Length<=256 && Finite(visual.Scale.x) && Finite(visual.Scale.y) && Finite(visual.Scale.z) &&
                Math.Abs(visual.Scale.x)<=10000 && Math.Abs(visual.Scale.y)<=10000 && Math.Abs(visual.Scale.z)<=10000 &&
                (visual.Lights==null || visual.Lights.Length<=8) && (visual.Audio==null || visual.Audio.Length<=4) &&
                (visual.Materials==null || visual.Materials.Length<=16) &&
                visual.ModelId>=0 && visual.ModelId<=999999 && visual.Archive>=0 && visual.Archive<=9999 && visual.Record>=0 && visual.Record<=9999;
            if(!valid) return false;
            if (visual.Materials != null) foreach (var material in visual.Materials)
                if (material == null || material.Node == null || material.Node.Length > 128 ||
                    material.Slot < 0 || material.Slot > 7 || string.IsNullOrEmpty(material.Shader) || material.Shader.Length > 128 ||
                    material.MainTexture != null && material.MainTexture.Length > 180 ||
                    !Finite(material.Tint.r) || !Finite(material.Tint.g) || !Finite(material.Tint.b) || !Finite(material.Tint.a) ||
                    !Finite(material.Tiling.x) || !Finite(material.Tiling.y) || !Finite(material.Offset.x) || !Finite(material.Offset.y)) return false;
            if(visual.Lights!=null) foreach(var light in visual.Lights) {
                if(light==null || !FiniteVector(light.Position) || !FiniteRotation(light.Rotation) ||
                    !Finite(light.Color.r) || !Finite(light.Color.g) || !Finite(light.Color.b) || !Finite(light.Color.a) ||
                    !Finite(light.Intensity) || light.Intensity<0 || light.Intensity>100 || !Finite(light.Range) || light.Range<0 || light.Range>10000 ||
                    !Finite(light.SpotAngle) || light.SpotAngle<0 || light.SpotAngle>180 || !Finite(light.ShadowStrength) || light.ShadowStrength<0 || light.ShadowStrength>1 ||
                    !Enum.IsDefined(typeof(LightType),light.Type) || !Enum.IsDefined(typeof(LightShadows),light.Shadows)) return false;
            }
            if(visual.Audio!=null) foreach(var audio in visual.Audio) {
                if(audio==null || !FiniteVector(audio.Position) || audio.Sound<0 || audio.Sound>=458 || !Enum.IsDefined(typeof(AudioPresets),audio.Preset) ||
                    !Finite(audio.Volume) || audio.Volume<0 || audio.Volume>1 || !Finite(audio.MinDistance) || audio.MinDistance<0 ||
                    !Finite(audio.MaxDistance) || audio.MaxDistance<audio.MinDistance || audio.MaxDistance>10000 ||
                    !Finite(audio.SpatialBlend) || audio.SpatialBlend<0 || audio.SpatialBlend>1) return false;
            }
            return true;
        }

        static string FindTextureAsset(ProfileRuntime profile, string textureName)
        {
            if (string.IsNullOrEmpty(textureName)) return null;
            string key = Path.GetFileNameWithoutExtension(textureName);
            string asset;
            if (profile.TextureAssets.TryGetValue(key, out asset)) return asset;
            Mod mod = EnabledMod(profile.Config);
            if (mod == null) return null;
            foreach (string name in mod.AssetNames ?? new string[0])
            {
                string ext = Path.GetExtension(name).ToLowerInvariant();
                if (ext != ".png" && ext != ".dds" && ext != ".tga" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp" && ext != ".asset") continue;
                if (string.Equals(Path.GetFileNameWithoutExtension(name), key, StringComparison.OrdinalIgnoreCase))
                { profile.TextureAssets[key] = name; return name; }
            }
            return null;
        }
        static string MaterialNodePath(Transform node, Transform root)
        {
            var parts = new List<string>();
            while (node != root)
            {
                if (node == null || node.parent == null || parts.Count >= 24)
                    throw new InvalidOperationException("Material hierarchy is outside the supported root/depth.");
                int index = 0;
                while (index < node.parent.childCount && node.parent.GetChild(index) != node) index++;
                parts.Insert(0, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                node = node.parent;
            }
            return string.Join("/", parts.ToArray());
        }
        static ObjectSyncMaterial[] CaptureMaterials(ProfileRuntime profile, GameObject source)
        {
            var records = new List<ObjectSyncMaterial>();
            foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                if (materials.Length > 8) throw new InvalidOperationException("More than eight material slots on a renderer.");
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    Material material = materials[slot]; if (material == null) continue;
                    if (records.Count >= 16) throw new InvalidOperationException("More than sixteen captured materials.");
                    if (material.shader == null) throw new InvalidOperationException("Source material has no shader.");
                    Texture texture = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
                    string asset = texture != null ? FindTextureAsset(profile, texture.name) : null;
                    if (texture != null && asset == null)
                        throw new InvalidOperationException("Source texture '" + texture.name + "' is not a named texture asset in this mod; use a custom factory.");
                    records.Add(new ObjectSyncMaterial { Node = MaterialNodePath(renderer.transform, source.transform), Slot = slot,
                        Shader = material.shader.name, MainTexture = asset,
                        Tint = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white,
                        Tiling = texture != null ? material.GetTextureScale("_MainTex") : Vector2.one,
                        Offset = texture != null ? material.GetTextureOffset("_MainTex") : Vector2.zero });
                }
            }
            if (records.Count == 0) throw new InvalidOperationException("No runtime source materials available for capture.");
            return records.ToArray();
        }
        static void ApplyMaterials(ProfileRuntime profile, ObjectSyncRule rule, GameObject presentation, ObjectSyncMaterial[] records)
        {
            if (records == null || records.Length == 0)
                throw new InvalidOperationException("Captured material data is missing; both peers need the SyncMaterials profile and a recreated source.");
            Mod mod = ActiveMod(profile.Config);
            if (mod == null) throw new InvalidOperationException("Material asset mod is unavailable.");
            var owner = presentation.GetComponent<SyncedObjectMaterials>();
            int created = 0;
            foreach (var record in records)
            {
                Transform node = presentation.transform;
                if (!string.IsNullOrEmpty(record.Node)) foreach (string part in record.Node.Split('/'))
                {
                    int index;
                    if (!int.TryParse(part, out index) || index < 0 || index >= node.childCount)
                        throw new InvalidOperationException("Captured material hierarchy does not match the local visual.");
                    node = node.GetChild(index);
                }
                var renderer = node.GetComponent<Renderer>();
                if (renderer == null) throw new InvalidOperationException("Captured material target has no renderer.");
                string shaderName = string.IsNullOrEmpty(rule.ShaderOverride) ? record.Shader : rule.ShaderOverride;
                Shader shader = ResolveMaterialShader(profile, shaderName);
                if (shader == null || !shader.isSupported) throw new InvalidOperationException("Captured material shader is unavailable: " + shaderName);
                Texture2D texture = null;
                if (!string.IsNullOrEmpty(record.MainTexture))
                {
                    string asset = FindTextureAsset(profile, record.MainTexture);
                    texture = asset != null ? mod.GetAsset<Texture2D>(asset) : null;
                    if (texture == null) throw new InvalidOperationException("Captured texture asset cannot be loaded: " + record.MainTexture);
                }
                var materials = renderer.sharedMaterials;
                if (materials.Length <= record.Slot) Array.Resize(ref materials, record.Slot + 1);
                Material material = materials[record.Slot];
                if (owner == null) owner = presentation.AddComponent<SyncedObjectMaterials>();
                if (material == null || !owner.Owned.Contains(material))
                { material = new Material(shader); owner.Owned.Add(material); created++; }
                material.shader = shader; material.shaderKeywords = new string[0];
                if (material.HasProperty("_MainTex"))
                {
                    material.SetTexture("_MainTex", texture);
                    material.SetTextureScale("_MainTex", record.Tiling); material.SetTextureOffset("_MainTex", record.Offset);
                }
                if (material.HasProperty("_Color")) material.SetColor("_Color", record.Tint);
                materials[record.Slot] = material; renderer.sharedMaterials = materials;
            }
            if (created > 0) Debug.Log("[ModObjectSync] Applied captured materials: slots=" + records.Length + ", newMaterials=" + created + ", firstShader='" + (string.IsNullOrEmpty(rule.ShaderOverride) ? records[0].Shader : rule.ShaderOverride) + "'.");
        }

        static Shader ResolveMaterialShader(ProfileRuntime profile, string shaderName)
        {
            return ResolveInstalledModShader(ActiveMod(profile.Config), shaderName);
        }
        public static Shader ResolveInstalledModShader(Mod mod, string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName) || shaderName.Length > 128) return null;
            Shader shader = Shader.Find(shaderName);
            if (shader != null && shader.isSupported) return shader;
            // Runtime source materials may use a shader loaded from the mod's bundle.
            // Shader.Find alone is insufficient before the receiving client has used it.
            if (mod == null) return null;
            string basename = Path.GetFileName(shaderName);
            foreach (string asset in mod.AssetNames ?? new string[0])
                if (string.Equals(Path.GetExtension(asset), ".shader", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileNameWithoutExtension(asset), basename, StringComparison.OrdinalIgnoreCase))
                {
                    shader = mod.GetAsset<Shader>(asset);
                    if (shader != null && shader.isSupported) return shader;
                }
            return null;
        }

        static GameObject WrapBillboardPresentation(GameObject visual)
        {
            if (visual == null || visual.GetComponent<DaggerfallBillboard>() == null) return visual;
            // Network pose belongs to this neutral parent. The actual billboard keeps
            // its own Update/animation/collider and faces each receiving client's camera.
            // A 3D flat replacement without a root DaggerfallBillboard is left intact.
            var anchor = new GameObject("BillboardAnchor");
            visual.transform.SetParent(anchor.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;
            return anchor;
        }

        static void ApplyShaderOverride(GameObject go, string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return;
            Shader shader = Shader.Find(shaderName);
            if (shader == null || !shader.isSupported)
            {
                Debug.LogWarning("[ModObjectSync] Shader override unavailable: " + shaderName + "; original replica materials retained.");
                return;
            }
            var copies = new Dictionary<Material, Material>();
            SyncedObjectMaterials owner = null;
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material original = materials[i]; if (original == null) continue;
                    Material copy;
                    if (!copies.TryGetValue(original, out copy))
                    {
                        copy = new Material(original); copy.shader = shader;
                        // Custom shader keywords are not necessarily meaningful to the replacement.
                        copy.shaderKeywords = new string[0];
                        copies.Add(original, copy);
                        if (owner == null) owner = go.AddComponent<SyncedObjectMaterials>();
                        owner.Owned.Add(copy);
                    }
                    materials[i] = copy;
                }
                renderer.sharedMaterials = materials;
            }
            Debug.Log("[ModObjectSync] Applied replica shader override '" + shaderName + "' to " + copies.Count + " material(s).");
        }
        static bool FiniteVector(Vector3 value) { return Finite(value.x) && Finite(value.y) && Finite(value.z) && Math.Abs(value.x)<=100000 && Math.Abs(value.y)<=100000 && Math.Abs(value.z)<=100000; }
        static bool FiniteRotation(Quaternion value) { float length=value.x*value.x+value.y*value.y+value.z*value.z+value.w*value.w; return Finite(length) && length>.5f && length<1.5f; }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        GameObject CloneVisualPrefab(GameObject prefab)
        {
            // Inspect the asset before cloning: arbitrary mod Awake/OnEnable methods
            // must not run merely because another peer needs a visual copy.
            if(prefab.GetComponentsInChildren<Mirror.NetworkIdentity>(true).Length>0 || prefab.GetComponentsInChildren<DaggerfallEntityBehaviour>(true).Length>0 ||
                prefab.GetComponentsInChildren<DaggerfallLoot>(true).Length>0) throw new InvalidOperationException("An actor/container prefab is not a visual prop.");
            foreach(var script in prefab.GetComponentsInChildren<MonoBehaviour>(true)) {
                string name=script.GetType().Name;
                bool visual=script is DaggerfallBillboard || script is DaggerfallMesh || script is DaggerfallAudioSource ||
                    name=="FaceCamera" || name=="AnimatedMaterial" || name=="RuntimeMaterials" || name=="DaggerfallLight";
                if(!visual) throw new InvalidOperationException("Prefab has non-presentation scripts; register a custom factory instead.");
            }
            var dormant=new GameObject("MP_VisualStaging"); dormant.SetActive(false);
            GameObject clone=null;
            try {
                clone=Instantiate(prefab,dormant.transform,false);
                foreach(var body in clone.GetComponentsInChildren<Rigidbody>(true)) body.isKinematic=true;
                clone.transform.SetParent(objectRoot,false);
                return clone;
            }
            catch { if(clone!=null) Destroy(clone); throw; }
            finally { Destroy(dormant); }
        }
        static void ApplyPresentationExtras(GameObject go,ObjectSyncVisual visual,Replica replica)
        {
            int lightCount=visual.Lights==null?0:visual.Lights.Length, soundCount=visual.Audio==null?0:visual.Audio.Length;
            if(replica.Extras==null || replica.Lights.Length!=lightCount || replica.Sounds.Length!=soundCount) {
                if(replica.Extras!=null) { replica.Extras.gameObject.SetActive(false); Destroy(replica.Extras.gameObject); }
                replica.Extras=new GameObject("MP_Synced_Extras").transform; replica.Extras.SetParent(go.transform,false);
                replica.Lights=new Light[lightCount]; replica.Sounds=new DaggerfallAudioSource[soundCount];
                for(int i=0;i<lightCount;i++) {
                    var node=new GameObject("MP_Synced_Light"); node.transform.SetParent(replica.Extras,false); replica.Lights[i]=node.AddComponent<Light>();
                }
                for(int i=0;i<soundCount;i++) {
                    var node=new GameObject("MP_Synced_Audio"); node.transform.SetParent(replica.Extras,false); replica.Sounds[i]=node.AddComponent<DaggerfallAudioSource>();
                    replica.Sounds[i].SoundIndex=-1;
                }
            }
            for(int i=0;i<lightCount;i++) {
                var data=visual.Lights[i]; var light=replica.Lights[i]; var node=light.gameObject;
                node.transform.localPosition=data.Position; node.transform.localRotation=data.Rotation;
                light.type=(LightType)data.Type; light.color=data.Color;
                light.intensity=data.Intensity; light.range=data.Range; light.spotAngle=data.SpotAngle;
                light.shadows=(LightShadows)data.Shadows; light.shadowStrength=data.ShadowStrength; light.enabled=data.Enabled;
            }
            for(int i=0;i<soundCount;i++) {
                var data=visual.Audio[i]; var sound=replica.Sounds[i]; sound.transform.localPosition=data.Position;
                if(sound.SoundIndex!=data.Sound || sound.Preset!=(AudioPresets)data.Preset)
                    sound.SetSound(data.Sound,(AudioPresets)data.Preset,data.SpatialBlend);
                var audio=sound.AudioSource; audio.volume=data.Volume; audio.minDistance=data.MinDistance; audio.maxDistance=data.MaxDistance;
                audio.spatialBlend=data.SpatialBlend; audio.mute=data.Mute; sound.enabled=data.Enabled;
            }
        }
    }
}
