using System;
using System.Collections.Generic;
using System.Linq;
using CompilePalX.Compiling;

namespace CompilePalX.Preview
{
    /// <summary>The props of a map as drawable geometry, in the world's vertex layout.</summary>
    public sealed class PropGeometry
    {
        public float[] Vertices { get; init; } = [];
        public uint[] Indices { get; init; } = [];
        /// <summary>Prop batches: light in the colour slot.</summary>
        public IReadOnlyList<DrawBatch> Batches { get; init; } = [];
        public int PropsPlaced { get; init; }
        public int EntityPropsPlaced { get; init; }
        public int PropsMissing { get; init; }
        public int PropsSkipped { get; init; }
        public int ModelsLoaded { get; init; }
        public int PropsWithBakedLight { get; init; }
        public int PropsInSun { get; init; }
        public int Triangles => Indices.Length / 3;
    }

    /// <summary>
    /// Places every prop - static props from the lump, and the doors and dynamic props entities
    /// put down: loads each model once, transforms its LOD 0 triangles by the prop's origin,
    /// angles and scale, and lights every vertex - from VRAD's baked vertex colours when the map
    /// has them, otherwise the way the engine lights a model at runtime: the ambient cube at its
    /// position plus the sun, when a trace toward it reaches the sky - so the viewer needs no
    /// per-prop state at all and can draw all props of one material in one call.
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
            int placed = 0, entityPlaced = 0, missing = 0, skipped = 0, baked = 0, inSun = 0;
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

                // one colour list per mesh, in mesh order, with a colour per vertex in the order the
                // VTX strips list them - the order VRAD lit them in; anything else is a file for a
                // different build of the model, and lighting it at runtime beats garbling it
                bool bakedOk = lighting is not null && lighting.Count == model.Meshes.Count
                               && lighting.Zip(model.Meshes).All(pair => pair.First.Length == pair.Second.Positions.Length);
                if (bakedOk)
                    baked++;

                var placement = new BspGeometry.Placement(prop.Origin, prop.Angles);
                var runtime = bakedOk ? null : RuntimeLight.For(scene, prop, model, placement);
                if (runtime is { SeesSun: true })
                    inSun++;

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
                            light = [meshLight[v * 3], meshLight[v * 3 + 1], meshLight[v * 3 + 2]];
                        }
                        else
                        {
                            light = runtime is null ? [0.5f, 0.5f, 0.5f] : runtime.Light(worldNormal);
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

            var indices = new List<uint>();
            var batches = new List<DrawBatch>();
            foreach (var (key, list) in byMaterial.OrderBy(kv => kv.Key.Skybox ? 0 : 1).ThenBy(kv => kv.Key.Material))
            {
                batches.Add(new DrawBatch(key.Material, indices.Count, list.Count, key.Skybox));
                indices.AddRange(list);
            }

            return new PropGeometry
            {
                Vertices = vertices.ToArray(),
                Indices = indices.ToArray(),
                Batches = batches,
                PropsPlaced = placed,
                EntityPropsPlaced = entityPlaced,
                PropsMissing = missing,
                PropsSkipped = skipped,
                ModelsLoaded = models.Values.Count(m => m is not null),
                PropsWithBakedLight = baked,
                PropsInSun = inSun,
            };
        }

        /// <summary>
        /// How the engine lights a model that has no baked light: the ambient cube where its body
        /// is, plus the sun when the body can see the sky. An entity's origin is often its hinge or
        /// its base, inside a wall or a floor where the leaf is dark or solid, so the sample is
        /// taken at the model's centre and falls back to the origin only when that is black too.
        /// </summary>
        public sealed class RuntimeLight
        {
            public required float[] Cube { get; init; }
            public bool SeesSun { get; init; }
            public Sun? Sun { get; init; }

            public float[] Light(float[] normal)
            {
                var light = LeafAmbient.Light(Cube, normal);
                if (SeesSun && Sun is not null)
                {
                    float facing = Math.Max(-(normal[0] * Sun.Direction[0] + normal[1] * Sun.Direction[1] + normal[2] * Sun.Direction[2]), 0f);
                    for (int k = 0; k < 3; k++)
                        light[k] += Sun.Color[k] * facing;
                }
                return light;
            }

            public static RuntimeLight? For(PreviewScene scene, StaticProp prop, StudioModel model, BspGeometry.Placement placement)
            {
                if (scene.Ambient is null)
                    return null;

                var c = model.Centre;
                var body = BspGeometry.Place([c[0] * prop.Scale, c[1] * prop.Scale, c[2] * prop.Scale], placement);
                var samplePoint = prop.Index >= 0 ? prop.LightingOrigin : body;

                var cube = scene.Ambient.CubeAt(samplePoint);
                if (prop.Index < 0 && cube.All(v => v <= 0.002f))
                {
                    cube = scene.Ambient.CubeAt(prop.Origin);
                    if (cube.All(v => v <= 0.002f))
                        cube = scene.Ambient.CubeAt([body[0], body[1], body[2] + 32]);
                }

                // the sun: traced from a little above the body, so a prop standing on the ground is not its own shadow
                bool seesSun = scene.Sun is not null && scene.SunVisibleAt is not null
                               && (scene.SunVisibleAt([body[0], body[1], body[2] + 8]) || scene.SunVisibleAt([prop.Origin[0], prop.Origin[1], prop.Origin[2] + 8]));

                return new RuntimeLight { Cube = cube, SeesSun = seesSun, Sun = scene.Sun };
            }
        }

        private static float Distance2(float[] a, float[] b)
        {
            float dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
