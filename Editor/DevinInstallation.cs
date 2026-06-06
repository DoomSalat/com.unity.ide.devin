using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using IOPath = System.IO.Path;

namespace Unity.Devin.Editor
{
	[Serializable]
	internal class VSCodeManifest
	{
		public string version;
	}

	internal class DevinInstallation
	{
		private const string DisplayName = "Devin";
		private const string InsiderSuffix = " - Insider";
		private const string InsiderKeyword = "insider";
		private const string GotoFlag = "--goto";

#if UNITY_EDITOR_WIN
		private const string ExeName = "Devin.exe";
		private const string FolderName = "Devin";
		private const string ExePattern = @".*[Dd]evin.*\.exe$";
#elif UNITY_EDITOR_OSX
		private const string AppPattern = @".*[Dd]evin.*\.app$";
		private const string AppSearchPattern = "Devin*.app";
		private const string ApplicationsDir = "/Applications";
#else
		private const string LinuxBinary = "devin";
		private static readonly string[] LinuxPaths = { "/usr/bin/devin", "/usr/local/bin/devin", "/bin/devin" };
#endif

		private static readonly string[] ManifestRelativePath = { "resources", "app", "package.json" };

		public string Path { get; private set; }
		public string Name { get; private set; }
		public Version Version { get; private set; }

		private static bool IsCandidateForDiscovery(string path)
		{
#if UNITY_EDITOR_WIN
            return File.Exists(path) && Regex.IsMatch(path, ExePattern, RegexOptions.IgnoreCase);
#elif UNITY_EDITOR_OSX
            return Directory.Exists(path) && Regex.IsMatch(path, AppPattern, RegexOptions.IgnoreCase);
#else
			return File.Exists(path) && path.EndsWith(LinuxBinary, StringComparison.OrdinalIgnoreCase);
#endif
		}

		public static bool TryDiscover(string editorPath, out DevinInstallation installation)
		{
			installation = null;

			if (string.IsNullOrEmpty(editorPath))
				return false;

			editorPath = IOPath.GetFullPath(editorPath);

			if (!IsCandidateForDiscovery(editorPath))
				return false;

			string manifestBase;
#if UNITY_EDITOR_WIN
            manifestBase = IOPath.GetDirectoryName(editorPath);
#elif UNITY_EDITOR_OSX
            manifestBase = IOPath.Combine(editorPath, "Contents");
#else
			var parent = Directory.GetParent(editorPath);
			manifestBase = parent?.Name == "bin" ? parent.Parent?.FullName : parent?.FullName;
#endif

			Version version = null;
			bool isPrerelease = false;

			if (manifestBase != null)
			{
				var manifestPath = IOPath.Combine(manifestBase, IOPath.Combine(ManifestRelativePath));

				if (File.Exists(manifestPath))
				{
					try
					{
						var manifest = UnityEngine.JsonUtility.FromJson<VSCodeManifest>(File.ReadAllText(manifestPath));

						if (manifest?.version != null)
						{
							Version.TryParse(manifest.version.Split('-')[0], out version);
							isPrerelease = manifest.version.IndexOf(InsiderKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
						}
					}
					catch { }
				}
			}

			installation = new DevinInstallation
			{
				Path = editorPath,
				Name = BuildDisplayName(version, isPrerelease),
				Version = version ?? new Version(),
			};
			return true;
		}

		public static IEnumerable<DevinInstallation> GetInstallations()
		{
			var candidates = new List<string>();

#if UNITY_EDITOR_WIN
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates.Add(IOPath.Combine(localAppData, "Programs", FolderName, ExeName));
            candidates.Add(IOPath.Combine(programFiles, FolderName, ExeName));
#elif UNITY_EDITOR_OSX
            if (Directory.Exists(ApplicationsDir))
                candidates.AddRange(Directory.EnumerateDirectories(ApplicationsDir, AppSearchPattern));
#else
			candidates.AddRange(LinuxPaths);
#endif

			foreach (var candidate in candidates)
			{
				if (TryDiscover(candidate, out var inst))
					yield return inst;
			}
		}

		public bool Open(string filePath, int line, int column)
		{
			line = Math.Max(1, line);
			column = Math.Max(0, column);

			var projectRoot = IOPath.GetDirectoryName(UnityEngine.Application.dataPath);
			var args = string.IsNullOrEmpty(filePath)
				? $"\"{projectRoot}\""
				: $"\"{projectRoot}\" {GotoFlag} \"{filePath}\":{line}:{column}";

			try
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = Path,
					Arguments = args,
					UseShellExecute = false,
				});
			}
			catch (Exception e)
			{
				UnityEngine.Debug.LogError($"[Devin] Failed to open file: {e.Message}");
				return false;
			}

			return true;
		}

		private static string BuildDisplayName(Version version, bool isPrerelease)
		{
			var name = DisplayName;

			if (isPrerelease)
				name += InsiderSuffix;

			if (version != null)
				name += $" [{version.ToString(3)}]";

			return name;
		}
	}
}
