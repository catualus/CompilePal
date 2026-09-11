using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CompilePalX.Compiling;

namespace CompilePalX.Preview
{
    /// <summary>The static props of a map as drawable geometry, in the world's vertex layout.</summary>
    public sealed class PropGeometry
    {
        public float[] Vertices { get; init; } = [];
        public uint[] Indices { get; init; } = [];
        public IReadOnlyList<DrawBatch> Batches { get; init; } = [];
        public int PropsPlaced { get; init; }
        public int PropsMissing { get; init; }
        public int PropsSkipped { get; init; }
        public int ModelsLoaded { get; init; }
        public int PropsWithBakedLight { get; init; }
        public int Triangles => Indices.Length / 3;
    }

    /// <summary>
    /// Places every static prop: loads each model once, transforms its LOD 0 triangles by the
    /// prop's origin, angles and scale, and lights every vertex - from VRAD's baked vertex colours
    /// when the map has them, otherwise from the ambient cube at the prop's position - so the
    /// viewer needs no per-prop state at all and can draw all props of one material in one call.
    ///
    /// Vertices use the world layout with the lightmap coordinate set to "none" and the colour slot
    /// carrying the light, which the viewer multiplies the texture by.
    /// </summary>
    public static class PropBuilder
    {
        /// <summary>Past this many triangles props are skipped, nearest to the spawn first kept.</summary>
        public const int TriangleBudget = 4_000_000;

        public static PropGeometry Build(PreviewScene scene, ContentLocator content, PreviewMaterials materials, bool hdr)
        {
            var models = new Dictionary<string, StudioModel?>(StringComparer.OrdinalIgnoreCase);
            var vertices = new List<float>();
            var indices = new List<uint>();
            var byMaterial = new Dictionary<int, List<uint>>();
            int placed = 0, missing = 0, skipped = 0, baked = 0;
            long triangles = 0;

            var centre = scene.Spawn ?? [(scene.Mins[0] + scene.Maxs[0]) / 2, (scene.Mins[1] + scene.Maxs[1]) / 2, (scene.Mins[2] + scene.Maxs[2]) / 2];

            var ordered = scene.StaticProps
                .OrderBy(p => Distance2(p.Origin, centre))
                .ToList();

            foreach (var prop in ordered)
            {
                if (!models.TryGetValue(prop.Model, out var model))
                {
                    model = Mdl.Load(prop.Model, content.Read);
                    models[prop.Model] = model;
                    if (model is null)
                        CompilePalLogger.LogLineDebug($"Static prop model \"{prop.Model}\" could not be loaded.");
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

                // baked vertex lighting, when VRAD wrote it
                List<float[]>? lighting = null;
                var vhv = content.Read(hdr ? $"sp_hdr_{prop.Index}.vhv" : $"sp_{prop.Index}.vhv")
                          ?? content.Read($"sp_{prop.Index}.vhv");
                if (vhv is not null)
                    lighting = VertexLighting.Read(vhv);

                // one colour list per mesh, in mesh order; anything else is a file for a different model
                bool bakedOk = lighting is not null && lighting.Count == model.Meshes.Count;
                if (bakedOk)
                    baked++;

                float[]? cube = bakedOk ? null : scene.Ambient?.CubeAt(prop.LightingOrigin);

                for (int m = 0; m < model.Meshes.Count; m++)
                {
                    var mesh = model.Meshes[m];
                    int material = materials.IndexOf(model.MaterialFor(mesh, prop.Skin));
                    if (!byMaterial.TryGetValue(material, out var list))
                        byMaterial[material] = list = [];

                    uint baseIndex = (uint)(vertices.Count / PreviewScene.VertexStride);
                    int count = mesh.Positions.Length / 3;
                    float[]? meshLight = bakedOk ? lighting![m] : null;

                    for (int v = 0; v < count; v++)
                    {
                        float[] local = [mesh.Positions[v * 3] * prop.Scale, mesh.Positions[v * 3 + 1] * prop.Scale, mesh.Positions[v * 3 + 2] * prop.Scale];
                        float[] normal = [mesh.Normals[v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]];

                        var world = BspGeometry.Place(local, new BspGeometry.Placement(prop.Origin, prop.Angles));
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

                        vertices.Add(world[0]); vertices.Add(world[1]); vertices.Add(world[2]);
                        vertices.Add(worldNormal[0]); vertices.Add(worldNormal[1]); vertices.Add(worldNormal[2]);
                        vertices.Add(-1); vertices.Add(-1);
                        vertices.Add(mesh.TexCoords[v * 2]); vertices.Add(mesh.TexCoords[v * 2 + 1]);
                        vertices.Add(0);
                        vertices.Add(light[0]); vertices.Add(light[1]); vertices.Add(light[2]);
                    }

                    foreach (uint i in mesh.Indices)
                        list.Add(baseIndex + i);
                }

                triangles += model.TriangleCount;
                placed++;
            }

            var batches = new List<DrawBatch>();
            foreach (var (material, list) in byMaterial.OrderBy(kv => kv.Key))
            {
                batches.Add(new DrawBatch(material, indices.Count, list.Count));
                indices.AddRange(list);
            }

            return new PropGeometry
            {
                Vertices = vertices.ToArray(),
                Indices = indices.ToArray(),
                Batches = batches,
                PropsPlaced = placed,
                PropsMissing = missing,
                PropsSkipped = skipped,
                ModelsLoaded = models.Values.Count(m => m is not null),
                PropsWithBakedLight = baked,
            };
        }

        private static float Distance2(float[] a, float[] b)
        {
            float dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
