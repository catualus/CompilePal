using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CompilePalX.Compiling
{
    /// <summary>
    /// Writes a step's rewritten copy of the map somewhere VBSP can compile it from without the
    /// queued VMF ever being touched.
    ///
    /// The copy keeps the map's file name, so the BSP it produces reaches the game under the name the
    /// map has always had, and lives in a folder of its own under the temp directory, one per step and
    /// map. Moving a VMF breaks one thing: VBSP looks for a func_instance's file relative to the VMF
    /// that names it, so every instance path in the copy is rewritten to reach the same file from the
    /// copy's folder.
    /// </summary>
    static class VmfHandoff
    {
        /// <summary>Where <paramref name="step"/> keeps its copy of <paramref name="sourceMapFile"/>.</summary>
        public static string CopyPath(string sourceMapFile, string step)
        {
            string name = Path.GetFileNameWithoutExtension(sourceMapFile);
            // Two maps of the same name in different folders must not share a copy.
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourceMapFile).ToLowerInvariant()));
            string folder = $"{name}-{Convert.ToHexString(hash, 0, 4).ToLowerInvariant()}";
            return Path.Combine(Path.GetTempPath(), "CompilePal", "handoff", step, folder, name + ".vmf");
        }

        /// <summary>
        /// Saves <paramref name="vmf"/>, loaded from <paramref name="loadedFrom"/>, as the copy for
        /// <paramref name="step"/>, and returns its path. <paramref name="sourceMapFile"/> is the map as
        /// queued, which names the copy; <paramref name="loadedFrom"/> may be an earlier step's copy.
        /// </summary>
        public static string Write(VmfDocument vmf, string loadedFrom, string sourceMapFile, string step)
        {
            string copy = CopyPath(sourceMapFile, step);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            RetargetInstances(vmf, loadedFrom, copy);
            vmf.Save(copy);
            return copy;
        }

        /// <summary>
        /// Rewrites each func_instance's file so it reaches, from <paramref name="newLocation"/>, the
        /// file VBSP would have found from <paramref name="loadedFrom"/>. An instance VBSP could not
        /// have found either way is left as written, so it fails with the same message it always did.
        /// </summary>
        public static int RetargetInstances(VmfDocument vmf, string loadedFrom, string newLocation)
        {
            int changed = 0;
            string newFolder = Path.GetDirectoryName(Path.GetFullPath(newLocation))!;

            foreach (var entity in vmf.Entities)
            {
                if (!string.Equals(vmf.Classname(entity), "func_instance", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? file = vmf.GetValue(entity, "file");
                if (string.IsNullOrWhiteSpace(file))
                    continue;

                string? found = ResolveInstance(loadedFrom, file);
                if (found is null)
                    continue;

                // A relative path cannot cross drives, and VBSP joins the instance path onto the VMF's
                // folder, so an absolute one does not work either.
                if (!string.Equals(Path.GetPathRoot(found), Path.GetPathRoot(newFolder), StringComparison.OrdinalIgnoreCase))
                    throw new IOException(
                        $"The instance {file} is on a different drive from {newFolder}, so a copy of the map compiled there cannot reach it.");

                string relative = Path.GetRelativePath(newFolder, found);
                if (vmf.SetValue(entity, "file", relative))
                    changed++;
            }

            return changed;
        }

        /// <summary>
        /// Finds an instance file the way VBSP's DeterminePath does: beside the map that names it, then
        /// under the nearest folder called "maps" above it. The third place VBSP looks, gameinfo's
        /// InstancePath, is searched through the game's file system and is the same wherever the map
        /// is, so it needs no rewriting and is not looked for here.
        /// </summary>
        public static string? ResolveInstance(string mapFile, string instanceFile)
        {
            string relative = Path.ChangeExtension(instanceFile.Replace('/', '\\'), ".vmf");
            string folder = Path.GetDirectoryName(Path.GetFullPath(mapFile))!;

            string beside = Path.GetFullPath(Path.Combine(folder, relative));
            if (File.Exists(beside))
                return beside;

            string withSlash = folder.TrimEnd('\\') + "\\";
            int maps = withSlash.IndexOf("\\maps\\", StringComparison.OrdinalIgnoreCase);
            if (maps >= 0)
            {
                string underMaps = Path.GetFullPath(Path.Combine(withSlash[..(maps + "\\maps\\".Length)], relative));
                if (File.Exists(underMaps))
                    return underMaps;
            }

            return null;
        }
    }
}
