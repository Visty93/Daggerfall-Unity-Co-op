// Only instantiated by RDBLayout while building a server-only dungeon representation.
// Keeps the normal Billboard/DaggerfallBillboard API and RDB metadata on the same object.
using UnityEngine;
using UnityEngine.Rendering;
using DaggerfallConnect;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Utility.AssetInjection;

namespace DaggerfallWorkshop
{
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class ServerDungeonBillboard : DaggerfallBillboard
    {
        private bool deferred;
        private int deferredFrame;

        public override Material SetMaterial(int archive, int record, int frame = 0)
        {
            deferred = false;
            ServerDungeonPresentation owner = ServerDungeonPresentation.Current;
            if (owner == null) return base.SetMaterial(archive, record, frame);

            DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
            if (!dfUnity.IsReady) return null;
            Vector2 size;
            Vector2 scale;
            // Preserve mod texture replacement dimensions/scaling through the existing API.
            Material material = TextureReplacement.GetStaticBillboardMaterial(gameObject, archive, record, ref summary, out scale);
            Mesh mesh;
            if (material != null)
            {
                mesh = dfUnity.MeshReader.GetBillboardMesh(summary.Rect, archive, record, out size);
                size *= scale;
                summary.AtlasedMaterial = false;
                summary.AnimatedMaterial = summary.ImportedTextures.FrameCount > 1;
            }
            else
            {
                if (!owner.TryGetBillboardSize(archive, record, out size)) return base.SetMaterial(archive, record, frame);
                mesh = dfUnity.MeshReader.GetSimpleBillboardMesh(size);
                summary.Rect = new Rect(0, 0, 1, 1);
                summary.AtlasedMaterial = false;
                summary.AnimatedMaterial = false;
                deferred = true;
                deferredFrame = frame;
            }
            summary.FlatType = MaterialReader.GetFlatType(archive);
            summary.Archive = archive;
            summary.Record = record;
            summary.Size = size;
            if (summary.FlatType == FlatTypes.Editor)
                summary.EditorFlatType = MaterialReader.GetEditorFlatType(record);
            if (RDBLayout.IsNPCFlat(archive)) summary.FlatType = FlatTypes.NPC;

            MeshFilter filter = GetComponent<MeshFilter>();
            Mesh oldMesh = filter.sharedMesh;
            filter.sharedMesh = mesh;
            var renderer = GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = DaggerfallUnity.Settings.GeneralBillboardShadows && archive != TextureReader.LightsTextureArchive
                ? ShadowCastingMode.TwoSided : ShadowCastingMode.Off;
            if (oldMesh != null) Destroy(oldMesh);
            if (summary.FlatType == FlatTypes.NPC)
            {
                var collider = GetComponent<BoxCollider>();
                if (collider == null) collider = gameObject.AddComponent<BoxCollider>();
                collider.isTrigger = true;
            }
            return material;
        }

        public override Material SetMaterial(Texture2D texture, Vector2 size, bool isLightArchive = false)
        {
            deferred = false;
            return base.SetMaterial(texture, size, isLightArchive);
        }

        public void LoadPresentation()
        {
            if (!deferred) return;
            deferred = false;
            if (GetComponent<MeshRenderer>().sharedMaterial != null) return;
            // Base method restores normal textures/animations without replacing the object or RDB data.
            base.SetMaterial(summary.Archive, summary.Record, deferredFrame);
        }
    }
}
