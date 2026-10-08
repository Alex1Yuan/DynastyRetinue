using System;
using System.Collections.Generic;
using Kingmaker.ResourceManagement;
using Kingmaker.View;
using Owlcat.Runtime.Visual.FogOfWar;
using Owlcat.Runtime.Visual.Waaagh;
using UnityEngine;
using UnityEngine.Rendering;

namespace DynastyRetinue.UI
{
    /// <summary>
    /// Window-scoped still images of the original hulls. Called only from recruitment UI events.
    /// Copies geometry, never a unit prefab or its behaviours. No Update, coroutine or live camera.
    /// </summary>
    internal static class FleetHullPreview
    {
        private const int Width = 1000, Height = 600;
        private const int PreviewLayer = 15; // Vanilla DollRoom layer.
        private static readonly Dictionary<string, Texture2D> Images
            = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static readonly HashSet<string> Misses = new HashSet<string>(StringComparer.Ordinal);

        internal static Texture2D Get(SpaceFleetHullDef hull)
        {
            if (hull == null || string.IsNullOrEmpty(hull.PrefabAssetId)) return null;
            string key = hull.PrefabAssetId;
            Texture2D cached;
            if (Images.TryGetValue(key, out cached) && cached != null) return cached;
            if (Misses.Contains(key)) return null;
            try
            {
                cached = Render(key);
                if (cached != null) Images[key] = cached;
                else Misses.Add(key);
                return cached;
            }
            catch (Exception e)
            {
                Misses.Add(key);
                Main.LogError("[舰型预览] " + hull.Name + ": " + e.Message);
                return null;
            }
        }

        internal static void Clear()
        {
            foreach (var texture in Images.Values)
                if (texture != null) UnityEngine.Object.Destroy(texture);
            Images.Clear();
            Misses.Clear();
        }

        private static Texture2D Render(string prefabGuid)
        {
            BundledResourceHandle<UnitEntityView> handle = null;
            GameObject stage = null;
            Camera camera = null;
            RenderTexture target = null;
            Texture2D result = null;
            RenderTexture previous = RenderTexture.active;
            bool completed = false;
            try
            {
                handle = BundledResourceHandle<UnitEntityView>.Request(prefabGuid, true);
                var prefab = handle != null ? handle.Object : null;
                var view = prefab != null ? prefab.GetComponentInChildren<StarshipView>(true) : null;
                if (view == null) return null;
                Transform visualRoot = view.BaseRenderer != null ? view.BaseRenderer.transform : null;
                if (visualRoot == null)
                {
                    // Native NPC cruisers leave the player doll-room field unset. Their hull
                    // MeshFilter/MeshRenderer live on StarshipView itself (verified for all four).
                    var hullFilter = view.GetComponent<MeshFilter>();
                    var hullRenderer = view.GetComponent<MeshRenderer>();
                    if (hullFilter == null || hullFilter.sharedMesh == null || hullRenderer == null)
                        return null;
                    visualRoot = view.transform;
                }

                stage = new GameObject("DynastyRetinue_HullPreview");
                stage.hideFlags = HideFlags.HideAndDontSave;
                stage.SetActive(false);
                // Separate from world geometry and the native doll room; no scene state is changed.
                stage.transform.position = new Vector3(0f, -10000f, 0f);
                var model = new GameObject("HullGeometry");
                model.transform.SetParent(stage.transform, false);
                var renderers = new List<MeshRenderer>();
                CopyGeometry(visualRoot, model.transform, renderers, true);
                if (renderers.Count == 0) return null;

                Bounds bounds = CombinedBounds(renderers);
                float diameter = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                if (diameter < 0.001f) return null;
                model.transform.localScale = Vector3.one * (10f / diameter);
                bounds = CombinedBounds(renderers);
                model.transform.position += stage.transform.position - bounds.center;
                bounds = CombinedBounds(renderers);

                var cameraObject = new GameObject("StillCamera");
                cameraObject.transform.SetParent(stage.transform, false);
                camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.aspect = (float)Width / Height;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.035f, 0.045f, 0.045f, 1f);
                camera.cullingMask = 1 << PreviewLayer;
                camera.useOcclusionCulling = false;
                camera.allowHDR = false;
                camera.allowMSAA = false;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 80f;
                camera.transform.rotation = Quaternion.Euler(50f, -35f, 45f);
                camera.transform.position = bounds.center - camera.transform.forward * 30f;
                FitCamera(camera, bounds);

                // Camera.Render is also used by vanilla CameraStackScreenshoter. This camera owns
                // an empty stack and opts out of world post effects, volumes and indirect batches.
                var data = cameraObject.AddComponent<WaaaghAdditionalCameraData>();
                data.RenderType = CameraRenderType.Base;
                data.RenderShadows = false;
                data.RenderPostProcessing = false;
                data.AllowIndirectRendering = false;
                data.AllowRenderScaling = false;
                data.RequiresColorTexture = false;
                data.RequiresDepthTexture = false;
                data.VolumeLayerMask = 0;
                AddLight(stage.transform, new Vector3(45f, -30f, 0f), 1.7f,
                    new Color(1f, 0.94f, 0.84f));
                AddLight(stage.transform, new Vector3(25f, 150f, 0f), 1.1f,
                    new Color(0.65f, 0.79f, 1f));

                target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
                target.name = "DynastyRetinue_HullStill";
                target.Create();
                camera.targetTexture = target;
                stage.SetActive(true);
                var fog = FogOfWarArea.Active;
                bool savedFog = fog != null && fog.ApplyShaderManually;
                try
                {
                    // FogOfWarFeature is separate from postprocessing; mirror the vanilla doll room.
                    if (fog != null) fog.ApplyShaderManually = true;
                    camera.Render();
                }
                finally
                {
                    if (fog != null) fog.ApplyShaderManually = savedFog;
                }
                RenderTexture.active = target;
                result = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                result.name = "DynastyRetinue_Hull_" + prefabGuid;
                result.hideFlags = HideFlags.HideAndDontSave;
                result.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                result.Apply(false, true);
                completed = true;
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                if (camera != null) camera.targetTexture = null;
                // All these objects were created above and contain only transforms, renderers,
                // lights and our disabled camera. Destroy synchronously before releasing the bundle.
                if (stage != null)
                {
                    stage.SetActive(false);
                    UnityEngine.Object.DestroyImmediate(stage);
                }
                if (target != null)
                {
                    target.Release();
                    UnityEngine.Object.Destroy(target);
                }
                if (!completed && result != null) UnityEngine.Object.Destroy(result);
                if (handle != null) handle.Dispose();
            }
        }

        private static void CopyGeometry(Transform source, Transform parent,
            List<MeshRenderer> renderers, bool root)
        {
            if (!root && !source.gameObject.activeSelf) return;
            var copy = new GameObject(source.name);
            copy.layer = PreviewLayer;
            copy.transform.SetParent(parent, false);
            // BaseRenderer is the native doll room's visual root. Its parent world offset is irrelevant.
            copy.transform.localPosition = root ? Vector3.zero : source.localPosition;
            copy.transform.localRotation = root ? Quaternion.identity : source.localRotation;
            copy.transform.localScale = source.localScale;
            var filter = source.GetComponent<MeshFilter>();
            var renderer = source.GetComponent<MeshRenderer>();
            if (filter != null && filter.sharedMesh != null && renderer != null
                && (root || renderer.enabled))
            {
                copy.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var visual = copy.AddComponent<MeshRenderer>();
                visual.sharedMaterials = renderer.sharedMaterials;
                visual.shadowCastingMode = ShadowCastingMode.Off;
                visual.receiveShadows = false;
                visual.lightProbeUsage = LightProbeUsage.Off;
                visual.reflectionProbeUsage = ReflectionProbeUsage.Off;
                renderers.Add(visual);
            }
            for (int i = 0; i < source.childCount; i++)
                CopyGeometry(source.GetChild(i), copy.transform, renderers, false);
        }

        private static Bounds CombinedBounds(List<MeshRenderer> renderers)
        {
            // Renderer.bounds can be stale before an inactive object has entered culling.
            // Compute directly from mesh bounds and transforms while the stage stays inactive.
            var bounds = new Bounds();
            bool first = true;
            foreach (var renderer in renderers)
            {
                Bounds local = renderer.GetComponent<MeshFilter>().sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = local.center + Vector3.Scale(local.extents,
                        new Vector3((i & 1) == 0 ? -1f : 1f,
                            (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 point = renderer.transform.TransformPoint(corner);
                    if (first) { bounds = new Bounds(point, Vector3.zero); first = false; }
                    else bounds.Encapsulate(point);
                }
            }
            return bounds;
        }

        private static void FitCamera(Camera camera, Bounds bounds)
        {
            float halfWidth = 0f, halfHeight = 0f;
            for (int i = 0; i < 8; i++)
            {
                var corner = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f,
                        (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                Vector3 point = camera.transform.InverseTransformPoint(corner);
                halfWidth = Mathf.Max(halfWidth, Mathf.Abs(point.x));
                halfHeight = Mathf.Max(halfHeight, Mathf.Abs(point.y));
            }
            camera.orthographicSize = Mathf.Max(halfHeight, halfWidth / camera.aspect) * 1.12f;
        }

        private static void AddLight(Transform parent, Vector3 angles, float intensity, Color color)
        {
            var go = new GameObject("PreviewLight");
            go.transform.SetParent(parent, false);
            go.transform.rotation = Quaternion.Euler(angles);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.cullingMask = 1 << PreviewLayer;
            light.shadows = LightShadows.None;
            light.intensity = intensity;
            light.color = color;
        }
    }
}
