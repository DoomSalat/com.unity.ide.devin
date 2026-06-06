using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UCE = Unity.CodeEditor.CodeEditor;
using IExternalCodeEditor = Unity.CodeEditor.IExternalCodeEditor;

namespace Unity.Devin.Editor
{
	[InitializeOnLoad]
	public class DevinEditor : IExternalCodeEditor
	{
		private const string PrefEmbedded = "Devin_Generate_Embedded";
		private const string PrefLocal = "Devin_Generate_Local";
		private const string PrefRegistry = "Devin_Generate_Registry";
		private const string PrefGit = "Devin_Generate_Git";
		private const string PrefBuiltIn = "Devin_Generate_BuiltIn";
		private const string PrefPlayerAssemblies = "Devin_Generate_Player";
		private const string PrefUseCustomSolution = "Devin_Use_Custom_Solution";
		private const string PrefCustomSolutionPath = "Devin_Custom_Solution_Path";

		private const float ButtonWidth = 252f;

		private static readonly string[] RelevantExtensions = { ".cs", ".asmdef", ".asmref", ".dll", ".rsp" }; // Static: immutable lookup table shared across calls

		private static bool _isSyncing; // Static: re-entrancy guard

		static DevinEditor()
		{
			UCE.Register(new DevinEditor());
		}

		public UCE.Installation[] Installations =>
			DevinInstallation.GetInstallations()
				.Select(i => new UCE.Installation { Name = i.Name, Path = i.Path })
				.ToArray();

		public bool TryGetInstallationForPath(string editorPath, out UCE.Installation installation)
		{
			if (DevinInstallation.TryDiscover(editorPath, out var inst))
			{
				installation = new UCE.Installation { Name = inst.Name, Path = inst.Path };

				return true;
			}

			installation = default;

			return false;
		}

		public bool OpenProject(string path, int line, int column)
		{
			if (!DevinInstallation.TryDiscover(UCE.CurrentEditorInstallation, out var installation))
				return false;

			return installation.Open(path, line, column);
		}

		public void SyncAll()
		{
			if (!IsDevinSelected())
				return;

			TrySyncSolution();
		}

		public void SyncIfNeeded(string[] addedFiles, string[] deletedFiles, string[] movedFiles,
			string[] movedFromFiles, string[] importedFiles)
		{
			if (!IsDevinSelected())
				return;

			if (!HasRelevantChanges(addedFiles, deletedFiles, movedFiles, importedFiles))
				return;

			TrySyncSolution();
		}

		public void Initialize(string editorInstallationPath) { }

		public void CreateIfDoesntExist() { }

		public void OnGUI()
		{
			if (!IsDevinSelected())
				return;

			EditorGUILayout.LabelField("Generate .csproj files for:", EditorStyles.boldLabel);

			EditorGUI.indentLevel++;
			DrawToggle(PrefEmbedded, "Embedded packages", defaultValue: true);
			DrawToggle(PrefLocal, "Local packages", defaultValue: true);
			DrawToggle(PrefRegistry, "Registry packages", defaultValue: false);
			DrawToggle(PrefGit, "Git packages", defaultValue: true);
			DrawToggle(PrefBuiltIn, "Built-in packages", defaultValue: false);
			DrawToggle(PrefPlayerAssemblies, "Player projects", defaultValue: false);
			EditorGUI.indentLevel--;

			GUILayout.Space(4);

			EditorGUILayout.LabelField("OmniSharp Solution:", EditorStyles.boldLabel);

			EditorGUI.indentLevel++;
			var useCustomSolution = EditorPrefs.GetBool(PrefUseCustomSolution, defaultValue: false);
			var nextUseCustomSolution = EditorGUILayout.Toggle("Use custom solution path", useCustomSolution);

			if (nextUseCustomSolution != useCustomSolution)
				EditorPrefs.SetBool(PrefUseCustomSolution, nextUseCustomSolution);

			if (nextUseCustomSolution)
			{
				var customPath = EditorPrefs.GetString(PrefCustomSolutionPath, "");
				var nextCustomPath = EditorGUILayout.TextField("Solution path:", customPath);

				if (nextCustomPath != customPath)
					EditorPrefs.SetString(PrefCustomSolutionPath, nextCustomPath);

				if (GUILayout.Button("Auto-detect solution path", GUILayout.Width(ButtonWidth)))
				{
					var detectedPath = AutoDetectSolutionPath();
					if (!string.IsNullOrEmpty(detectedPath))
						EditorPrefs.SetString(PrefCustomSolutionPath, detectedPath);
				}
			}
			EditorGUI.indentLevel--;

			GUILayout.Space(4);

			if (GUILayout.Button("Regenerate project files", GUILayout.Width(ButtonWidth)))
				SyncAll();
		}

		private static void DrawToggle(string prefKey, string label, bool defaultValue)
		{
			var current = EditorPrefs.GetBool(prefKey, defaultValue);
			var next = EditorGUILayout.Toggle(label, current);

			if (next != current)
				EditorPrefs.SetBool(prefKey, next);
		}

		private static bool IsDevinSelected() =>
			DevinInstallation.TryDiscover(UCE.CurrentEditorInstallation, out _);

		private static void TrySyncSolution()
		{
			if (_isSyncing)
				return;

			_isSyncing = true;

			try
			{
				ProjectGeneration.Sync(ReadFlag());
			}
			finally
			{
				_isSyncing = false;
			}
		}

		private static ProjectGenerationFlag ReadFlag()
		{
			var flag = ProjectGenerationFlag.None;

			if (EditorPrefs.GetBool(PrefEmbedded, defaultValue: true))
				flag |= ProjectGenerationFlag.Embedded;

			if (EditorPrefs.GetBool(PrefLocal, defaultValue: true))
				flag |= ProjectGenerationFlag.Local;

			if (EditorPrefs.GetBool(PrefRegistry, defaultValue: false))
				flag |= ProjectGenerationFlag.Registry;

			if (EditorPrefs.GetBool(PrefGit, defaultValue: true))
				flag |= ProjectGenerationFlag.Git;

			if (EditorPrefs.GetBool(PrefBuiltIn, defaultValue: false))
				flag |= ProjectGenerationFlag.BuiltIn;

			if (EditorPrefs.GetBool(PrefPlayerAssemblies, defaultValue: false))
				flag |= ProjectGenerationFlag.PlayerAssemblies;

			return flag;
		}

		private static bool HasRelevantChanges(params string[][] fileSets)
		{
			foreach (var files in fileSets)
			{
				if (files == null)
					continue;

				foreach (var file in files)
				{
					var extension = System.IO.Path.GetExtension(file);

					foreach (var relevant in RelevantExtensions)
					{
						if (string.Equals(extension, relevant, StringComparison.OrdinalIgnoreCase))
							return true;
					}
				}
			}

			return false;
		}

		private static string AutoDetectSolutionPath()
		{
			var projectPath = System.IO.Directory.GetCurrentDirectory();
			var slnFiles = System.IO.Directory.GetFiles(projectPath, "*.sln");

			if (slnFiles.Length > 0)
				return slnFiles[0];

			return "";
		}

		public static string GetSolutionPath()
		{
			if (EditorPrefs.GetBool(PrefUseCustomSolution, defaultValue: false))
			{
				var customPath = EditorPrefs.GetString(PrefCustomSolutionPath, "");
				if (!string.IsNullOrEmpty(customPath) && System.IO.File.Exists(customPath))
					return customPath;
			}

			return AutoDetectSolutionPath();
		}
	}
}
