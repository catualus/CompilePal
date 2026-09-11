using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CompilePalX.Compiling;

namespace CompilePalX.Preview
{
    /// <summary>The props and overlays of a map as drawable geometry, in the world's vertex layout.</summary>
    public sealed class PropGeometry
    {
        public float[] Vertices { get; init; } = [];
        public uint[] Indices { get; init; } = [];
        /// <summary>Prop batches: light in the colour slot. Overlay batches are flagged and drawn on top of the world.</summary>
        public IReadOnlyList<DrawBatch> Batches { get; init; } = [];
        public IReadOnlyList<DrawBatch> OverlayBatches { get; init; } = [];
        public int PropsPlaced { get; init; }
        public int EntityPropsPlaced { get; init; }
        public int PropsMissing { get; init; }
        public int PropsSkipped { get; init; }
        public int ModelsLoaded { get; init; }
        public int PropsWithBakedLight { get; init; }
        public int OverlaysPlaced { get; init; }
        public int Triangles => Indices.Length / 3;
    }

    /// <summary>
    /// Places every prop - static props from the lump, and the doors and dynamic props entities
    /// put down - and every overlay: loads each model once, transforms its LOD 0 triangles by the
    /// prop's origin, angles and scale, and lights every vertex - from VRAD's baked vertex colours
    /// when the map has them, otherwise from the ambient cube at the prop's position - so the
    /// viewer needs no per-prop state at all and can draw all props of one material in one call.
    ///
    /// Vertices use the world layout with the lightmap coordinate set to "none" and the colour slot
    /// carrying the light, which the viewer multiplies the texture by. Props inside the 3D skybox
    /// are scaled up around the sky camera like the skybox's own faces.
    /// </summary>
    public static class PropBuilder
    {
        /// <summary>Past this many triangles props are skipped, nearest to the spawn first kept.</summary>
        public const int TriangleBudget = 4_000_000;

        public static PropGeometry Build(PreviewScene scene, ContentLocator content, PreviewMaterials materials, bool hdr)
        {
            var models = new Dictionary<string, StudioModel?>(StringComparer.OrdinalIgnoreCase);
            var vertices = new List<float>();
            var byMaterial = new Dictionary<(int Material, bool Skybox), List<uint>>();
            int placed = 0, entityPlaced = 0, missing = 0, skipped = 0, baked = 0;
            long triangles = 0;

            var centre = scene.Spawn ?? [(scene.Mins[0] + scene.Maxs[0]) / 2, (scene.Mins[1] + scene.Maxs[1]) / 2, (scene.Mins[2] + scene.Maxs[2]) / 2];
            int skyArea = scene.SkyboxArea;

            bool InSkybox(StaticProp prop)
            {
                if (scene.Sky3D is null || scene.Ambient is null || skyArea < 0)
                    return false;
                int leaf = prop.FirstLeaf >= 0 ? prop.FirstLeaf : scene.Ambient.LeafAt(prop.Origin);
                return scene.Ambient.AreaOf(leaf) == skyArea;
            }

            float[] ToWorld(float[] p, bool skybox) =>
                skybox && scene.Sky3D is { } sky
                    ? [(p[0] - sky.Origin[0]) * sky.Scale, (p[1] - sky.Origin[1]) * sky.Scale, (p[2] - sky.Origin[2]) * sky.Scale]
                    : p;

            void Emit(float[] p, float[] normal, float tu, float tv, float[] light)
            {
                vertices.Add(p[0]); vertices.Add(p[1]); vertices.Add(p[2]);
                vertices.Add(normal[0]); vertices.Add(normal[1]); vertices.Add(normal[2]);
                vertices.Add(-1); vertices.Add(-1);
                vertices.Add(tu); vertices.Add(tv);
                vertices.Add(0);
                vertices.Add(light[0]); vertices.Add(light[1]); vertices.Add(light[2]);
            }

            var ordered = scene.StaticProps.Concat(scene.EntityProps)
                .OrderBy(p => Distance2(p.Origin, centre))
                .ToList();

            foreach (var prop in ordered)
            {
                if (!models.TryGetValue(prop.Model, out var model))
                {
                    model = Mdl.Load(prop.Model, content.Read);
                    models[prop.Model] = model;
                    if (model is null)
                        CompilePalLogger.LogLineDebug($"Prop model \"{prop.Model}\" could not be loaded.");
                }

                if (model is null)
                {
                    missing++;
                    continue;
                }

                if (triangles + model.TriangleCount > TriangleBudget)
                {
                    skipped++;
                    continue;
                }

                bool skybox = InSkybox(prop);

                // baked vertex lighting, when VRAD wrote it; only static props have a file
                List<float[]>? lighting = null;
                if (prop.Index >= 0)
                {
                    var vhv = content.Read(hdr ? $"sp_hdr_{prop.Index}.vhv" : $"sp_{prop.Index}.vhv")
                              ?? content.Read($"sp_{prop.Index}.vhv");
                    if (vhv is not null)
                        lighting = VertexLighting.Read(vhv);
                }

                // one colour list per mesh, in mesh order; anything else is a file for a different model
                bool bakedOk = lighting is not null && lighting.Count == model.Meshes.Count;
                if (bakedOk)
                    baked++;

                var placement = new BspGeometry.Placement(prop.Origin, prop.Angles);
                float[]? cube = bakedOk ? null : AmbientFor(scene, prop, model, placement);

                for (int m = 0; m < model.Meshes.Count; m++)
                {
                    var mesh = model.Meshes[m];
                    var key = (materials.IndexOf(model.MaterialFor(mesh, prop.Skin)), skybox);
                    if (!byMaterial.TryGetValue(key, out var list))
                        byMaterial[key] = list = [];

                    uint baseIndex = (uint)(vertices.Count / PreviewScene.VertexStride);
                    int count = mesh.Positions.Length / 3;
                    float[]? meshLight = bakedOk ? lighting![m] : null;

                    for (int v = 0; v < count; v++)
                    {
                        float[] local = [mesh.Positions[v * 3] * prop.Scale, mesh.Positions[v * 3 + 1] * prop.Scale, mesh.Positions[v * 3 + 2] * prop.Scale];
                        float[] normal = [mesh.Normals[v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]];

                        var world = ToWorld(BspGeometry.Place(local, placement), skybox);
                        var worldNormal = BspGeometry.Rotate(normal, prop.Angles);

                        float[] light;
                        if (meshLight is not null)
                        {
                            // the file's vertices follow the mesh's own order in the VVD
                            int li = Math.Clamp(mesh.VvdIndices[v] - mesh.VertexStart, 0, meshLight.Length / 3 - 1);
                            light = [meshLight[li * 3], meshLight[li * 3 + 1], meshLight[li * 3 + 2]];
                        }
                        else
                        {
                            light = cube is null ? [0.5f, 0.5f, 0.5f] : LeafAmbient.Light(cube, worldNormal);
                        }

                        Emit(world, worldNormal, mesh.TexCoords[v * 2], mesh.TexCoords[v * 2 + 1], light);
                    }

                    foreach (uint i in mesh.Indices)
                        list.Add(baseIndex + i);
                }

                triangles += model.TriangleCount;
                placed++;
                if (prop.Index < 0)
                    entityPlaced++;
            }

            // overlays: a quad each, lit like a prop from the ambient cube where it sits
            var overlaysByMaterial = new Dictionary<(int Material, bool Skybox), List<uint>>();
            int overlaysPlaced = 0;
            foreach (var overlay in scene.Overlays)
            {
                if (overlay.Material < 0 || overlay.Material >= scene.MaterialNames.Count)
                    continue;

                var key = (materials.IndexOf(scene.MaterialNames[overlay.Material]), overlay.Skybox);
                if (!overlaysByMaterial.TryGetValue(key, out var list))
                    overlaysByMaterial[key] = list = [];

                var centreOfQuad = new float[3];
                foreach (var c in overlay.Corners)
                    for (int k = 0; k < 3; k++)
                        centreOfQuad[k] += c[k] / 4;
                var cube = scene.Ambient?.CubeAt(centreOfQuad);
                var light = cube is null ? [0.6f, 0.6f, 0.6f] : LeafAmbient.Light(cube, overlay.Normal);

                uint baseIndex = (uint)(vertices.Count / PreviewScene.VertexStride);
                for (int k = 0; k < 4; k++)
                    Emit(overlay.Corners[k], overlay.Normal, overlay.TexCoords[k][0], overlay.TexCoords[k][1], light);

                list.AddRange([baseIndex, baseIndex + 1, baseIndex + 2, baseIndex, baseIndex + 2, baseIndex + 3]);
                overlaysPlaced++;
            }

            var indices = new List<uint>();
            var batches = new List<DrawBatch>();
            foreach (var (key, list) in byMaterial.OrderBy(kv => kv.Key.Skybox ? 0 : 1).ThenBy(kv => kv.Key.Material))
            {
                batches.Add(new DrawBatch(key.Material, indices.Count, list.Count, key.Skybox));
                indices.AddRange(list);
            }

            var overlayBatches = new List<DrawBatch>();
            foreach (var (key, list) in overlaysByMaterial.OrderBy(kv => kv.Key.Skybox ? 0 : 1).ThenBy(kv => kv.Key.Material))
            {
                overlayBatches.Add(new DrawBatch(key.Material, indices.Count, list.Count, key.Skybox));
                indices.AddRange(list);
            }

            return new PropGeometry
            {
                Vertices = vertices.ToArray(),
                Indices = indices.ToArray(),
                Batches = batches,
                OverlayBatches = overlayBatches,
                PropsPlaced = placed,
                EntityPropsPlaced = entityPlaced,
                PropsMissing = missing,
                PropsSkipped = skipped,
                ModelsLoaded = models.Values.Count(m => m is not null),
                PropsWithBakedLight = baked,
                OverlaysPlaced = overlaysPlaced,
            };
        }

        /// <summary>
        /// The ambient cube to light a prop by. A static prop names its lighting origin. An entity's
        /// origin is often its hinge or its base, inside a wall or a floor where the leaf is dark or
        /// solid, so those are lit from where their body is and fall back to the origin only when
        /// that sample is black too.
        /// </summary>
        private static float[]? AmbientFor(PreviewScene scene, StaticProp prop, StudioModel model, BspGeometry.Placement placement)
        {
            if (scene.Ambient is null)
                return null;
            if (prop.Index >= 0)
                return scene.Ambient.CubeAt(prop.LightingOrigin);

            var c = model.Centre;
            var body = BspGeometry.Place([c[0] * prop.Scale, c[1] * prop.Scale, c[2] * prop.Scale], placement);
            var cube = scene.Ambient.CubeAt(body);
            if (cube.Any(v => v > 0.002f))
                return cube;
            cube = scene.Ambient.CubeAt(prop.Origin);
            if (cube.Any(v => v > 0.002f))
                return cube;
            return scene.Ambient.CubeAt([body[0], body[1], body[2] + 32]);
        }

        private static float Distance2(float[] a, float[] b)
        {
            float dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
