using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CompilePalX.Compiling;
using Microsoft.Win32;
using ValveKeyValue;

namespace CompilePalX
{
	/// <summary>
	/// Finds Source games installed through Steam so their Hammer configuration can be read without the
	/// user having launched Hammer first.
	///
	/// Game configurations were previously discovered from a single registry value -
	/// HKCU\Software\Valve\Hammer\General\Directory - which Hammer writes when it starts. That is why a
	/// fresh install greeted people with "launch Hammer for the game you want to compile for and click
	/// refresh": Compile Pal genuinely could not see a game until Hammer had been opened for it at least
	/// once, and only ever saw the most recent one.
	/// </summary>
	static class SteamGameScanner
	{
		private static readonly KVSerializer Serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);

		/// <summary>
		/// Bin folders of installed games that carry a Hammer game configuration.
		///
		/// Returns folders rather than parsed configurations so the existing parser stays the single
		/// place that understands GameConfig.txt.
		/// </summary>
		public static List<string> FindBinFolders()
		{
			var found = new List<string>();

			foreach (var library in FindLibraryFolders())
			{
				string common = Path.Combine(library, "steamapps", "common");
				if (!Directory.Exists(common))
					continue;

				IEnumerable<string> games;
				try
				{
					games = Directory.EnumerateDirectories(common);
				}
				catch (Exception e)
				{
					CompilePalLogger.LogLineDebug($"Could not list {common}: {e.Message}");
					continue;
				}

				foreach (var game in games)
				{
					foreach (var bin in CandidateBinFolders(game))
					{
						if (!HasGameConfig(bin))
							continue;

						// Normalised before comparing: the registry spells the Steam folder three
						// different ways between HKCU and HKLM ("c:/program files (x86)/steam",
						// "C:\\Program Files (x86)\\Steam"), so the same bin folder arrives more than
						// once and would otherwise be parsed once per spelling.
						string normalised = Normalise(bin);

						if (!found.Contains(normalised, StringComparer.OrdinalIgnoreCase))
							found.Add(normalised);
					}
				}
			}

			CompilePalLogger.LogLineDebug($"Steam scan found {found.Count} game configuration folder(s)");
			return found;
		}

		/// <summary>
		/// Where a Source game keeps its compile tools.
		///
		/// bin/ is the usual place; the 64-bit branches (Garry's Mod, CS:GO era engines) put them in
		/// bin/win64 instead, and some titles nest the whole engine one level down.
		/// </summary>
		private static IEnumerable<string> CandidateBinFolders(string gameFolder)
		{
			yield return Path.Combine(gameFolder, "bin");
			yield return Path.Combine(gameFolder, "bin", "win64");
		}

		/// <summary>
		/// Canonical form of a path, so two spellings of the same folder compare equal.
		/// Falls back to the original if the path is malformed enough that GetFullPath throws.
		/// </summary>
		private static string Normalise(string path)
		{
			try
			{
				return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
			}
			catch
			{
				return path;
			}
		}

		private static bool HasGameConfig(string binFolder)
		{
			if (!Directory.Exists(binFolder))
				return false;

			// Same two files, in the same order of preference, that GameConfigurationParser reads.
			return File.Exists(Path.Combine(binFolder, "hammerplusplus", "hammerplusplus_gameconfig.txt"))
			       || File.Exists(Path.Combine(binFolder, "GameConfig.txt"));
		}

		/// <summary>Every Steam library on this machine, including the install folder itself.</summary>
		private static List<string> FindLibraryFolders()
		{
			var libraries = new List<string>();

			string? steam = FindSteamPath();
			if (steam == null)
			{
				CompilePalLogger.LogLineDebug("Steam installation not found in the registry; skipping the game scan.");
				return libraries;
			}

			libraries.Add(Normalise(steam));

			string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
			if (!File.Exists(vdf))
				return libraries;

			try
			{
				using var stream = File.OpenRead(vdf);
				var data = Serializer.Deserialize(stream);

				foreach (var entry in data)
				{
					// The file has had two shapes. Older Steam wrote numbered keys straight to a path
					// string; current Steam writes a block per library with the path inside it. A
					// collection value enumerates as KVObjects, which is how the game config parser
					// tells the two apart too.
					string? path = entry.Value is IEnumerable<KVObject>
						? entry["path"]?.ToString()
						: entry.Value.ToString();

					if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
						continue;

					string normalised = Normalise(path);
					if (!libraries.Contains(normalised, StringComparer.OrdinalIgnoreCase))
						libraries.Add(normalised);
				}
			}
			catch (Exception e)
			{
				// A library file we cannot read costs us the extra libraries, not the scan.
				CompilePalLogger.LogLineDebug($"Could not read {vdf}: {e.Message}");
			}

			return libraries;
		}

		private static string? FindSteamPath()
		{
			// HKCU is where the current user's client records itself; the HKLM values are the fallback
			// for an install made by another account.
			foreach (var (hive, key, value) in new[]
			         {
				         (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
				         (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
				         (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
			         })
			{
				try
				{
					using var subKey = hive.OpenSubKey(key);
					if (subKey?.GetValue(value) is string path && Directory.Exists(path))
						return path;
				}
				catch (Exception e)
				{
					CompilePalLogger.LogLineDebug($"Could not read {key}\\{value}: {e.Message}");
				}
			}

			return null;
		}
	}
}
