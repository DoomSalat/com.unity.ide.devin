using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using UCE = Unity.CodeEditor.CodeEditor;
using IExternalCodeEditor = Unity.CodeEditor.IExternalCodeEditor;

namespace Unity.Devin.Editor
{
	[InitializeOnLoad]
	public class DevinEditor : IExternalCodeEditor
	{
		private const float ButtonWidth = 252f;

		// Extensions that trigger SyncIfNeeded (narrow: only project-structure-relevant files)
		private static readonly string[] RelevantExtensions = { ".cs", ".asmdef", ".asmref", ".dll", ".rsp" };

		// Extensions Devin should open; everything else (prefabs, materials, scenes…) stays in Unity
		private static readonly HashSet<string> OpenableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			".cs", ".uxml", ".uss", ".shader", ".compute", ".cginc", ".hlsl", ".glslinc",
			".template", ".raytrace", ".json", ".rsp", ".asmdef", ".asmref",
			".xaml", ".tt", ".t4", ".ttinclude", ".dll", ".txt", ".md", ".xml",
			".yaml", ".yml"
		};

		private static bool _isSyncing;

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
			if (!string.IsNullOrEmpty(path))
			{
				var ext = System.IO.Path.GetExtension(path);
				if (!string.IsNullOrEmpty(ext) && !OpenableExtensions.Contains(ext))
					return false;
			}

			if (!DevinInstallation.TryDiscover(UCE.CurrentEditorInstallation, out var installation))
				return false;

			return installation.Open(path, line, column);
		}

		public void SyncAll()
		{
			if (!IsDevinSelected())
				return;

			TrySyncSolution(full: true);
		}

		public void SyncIfNeeded(string[] addedFiles, string[] deletedFiles, string[] movedFiles,
			string[] movedFromFiles, string[] importedFiles)
		{
			if (!IsDevinSelected())
				return;

			if (!HasRelevantChanges(addedFiles, deletedFiles, movedFiles, importedFiles))
				return;

			TrySyncSolution(full: false, addedFiles, deletedFiles, movedFiles, movedFromFiles, importedFiles);
		}

		public void Initialize(string editorInstallationPath)
		{
			ReflectedProjectSync.InvalidateCache();
		}

		public void CreateIfDoesntExist() { }

		[DidReloadScripts]
		private static void OnScriptsReloaded()
		{
			if (!IsDevinSelected())
				return;

			// Full sync only when solution is missing — first launch or deleted files.
			// Otherwise SyncIfNeeded handles incremental updates, so we don't disturb
			// an in-progress OmniSharp load by rewriting files that haven't changed.
			var dir = System.IO.Path.GetDirectoryName(Application.dataPath);
			var projectName = System.IO.Path.GetFileName(dir);
			var slnPath = System.IO.Path.Combine(dir, projectName + ".sln");

			if (!System.IO.File.Exists(slnPath))
				TrySyncSolution(full: true);
		}

		public void OnGUI()
		{
			if (!IsDevinSelected())
				return;

			var allEditors = ReflectedProjectSync.GetAllEditors();

			// — Delegate selector —
			EditorGUILayout.LabelField("Project generation delegate:", EditorStyles.boldLabel);
			EditorGUI.indentLevel++;

			if (allEditors.Length == 0)
			{
				EditorGUILayout.HelpBox(
					"No editor plugins found in the project.\n" +
					"Add com.unity.ide.rider or com.unity.ide.visualstudio to Packages/manifest.json.",
					MessageType.Error);
			}
			else
			{
				var typeNames = allEditors.Select(e => e.GetType().FullName).ToArray();
				var displayNames = allEditors.Select(e => e.GetType().Name).ToArray();
				var savedType = EditorPrefs.GetString(ReflectedProjectSync.PrefDelegateEditorType, "");
				var currentIndex = Math.Max(0, Array.IndexOf(typeNames, savedType));

				var newIndex = EditorGUILayout.Popup(currentIndex, displayNames);
				if (newIndex != currentIndex)
				{
					EditorPrefs.SetString(ReflectedProjectSync.PrefDelegateEditorType, typeNames[newIndex]);
					ReflectedProjectSync.InvalidateCache();
				}

				var selected = allEditors[newIndex];
				var installs = selected.Installations;
				var hasInstall = installs != null && installs.Length > 0;

				if (!hasInstall)
				{
					bool isVsEditor = selected.GetType().FullName
						?.IndexOf("VisualStudio", StringComparison.OrdinalIgnoreCase) >= 0;

					if (isVsEditor)
					{
						EditorGUILayout.HelpBox(
							"Visual Studio installations were not auto-discovered by the VS plugin.\n" +
							"Project file generation uses GeneratorFactory directly — click \"Regenerate project files\".",
							MessageType.Info);
					}
					else
						EditorGUILayout.HelpBox(
							$"{selected.GetType().Name} is registered but not installed on this machine.\n" +
							"Project files will not be generated until a valid installation is found.",
							MessageType.Warning);
				}
				else
					EditorGUILayout.HelpBox(
						"Generation settings are controlled by the selected delegate editor's preferences.",
						MessageType.Info);
			}

			EditorGUI.indentLevel--;
			GUILayout.Space(6);

			// — File state —
			EditorGUILayout.LabelField("Project file state:", EditorStyles.boldLabel);
			EditorGUI.indentLevel++;
			DrawFileStatus();
			EditorGUI.indentLevel--;
			GUILayout.Space(6);

			if (GUILayout.Button("Regenerate project files", GUILayout.Width(ButtonWidth)))
				SyncAll();
		}

		private static void DrawFileStatus()
		{
			var dir = System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath);
			var projectName = System.IO.Path.GetFileName(dir);

			var slnPath = System.IO.Path.Combine(dir, projectName + ".sln");
			var omniPath = System.IO.Path.Combine(dir, "omnisharp.json");
			var propsPath = System.IO.Path.Combine(dir, "Directory.Build.props");

			DrawRow(".sln",               slnPath,   required: true);
			DrawRow("omnisharp.json",     omniPath,  required: true,  contentCheck: c => c.Contains("\"solution\""));
			DrawRow("Directory.Build.props", propsPath, required: false, contentCheck: c => c.Contains("SkipGetTargetFrameworkProperties"));
		}

		private static void DrawRow(string label, string path, bool required,
			System.Func<string, bool> contentCheck = null)
		{
			bool exists = System.IO.File.Exists(path);
			bool contentOk = true;

			if (exists && contentCheck != null)
				contentOk = contentCheck(System.IO.File.ReadAllText(path));

			string status;
			MessageType msgType;

			if (!exists)
			{
				status = required ? $"{label} — missing" : $"{label} — not generated yet";
				msgType = required ? MessageType.Warning : MessageType.Info;
			}
			else if (!contentOk)
			{
				status = $"{label} — exists but may be misconfigured";
				msgType = MessageType.Warning;
			}
			else
			{
				status = $"{label} — OK";
				msgType = MessageType.None;
			}

			if (msgType == MessageType.None)
				EditorGUILayout.LabelField(status);
			else
				EditorGUILayout.HelpBox(status, msgType);
		}

		private static bool IsDevinSelected() =>
			DevinInstallation.TryDiscover(UCE.CurrentEditorInstallation, out _);

		private static void TrySyncSolution(bool full,
			string[] added = null, string[] deleted = null,
			string[] moved = null, string[] movedFrom = null, string[] imported = null)
		{
			if (_isSyncing)
				return;

			_isSyncing = true;

			try
			{
				var delegateEditor = ReflectedProjectSync.FindDelegate();

				if (full)
					ReflectedProjectSync.TrySync(delegateEditor);
				else
					ReflectedProjectSync.TrySyncIfNeeded(delegateEditor, added, deleted, moved, movedFrom, imported);
			}
			finally
			{
				_isSyncing = false;
			}
		}

		private static bool HasRelevantChanges(params string[][] fileSets)
		{
			foreach (var files in fileSets)
			{
				if (files == null)
					continue;

				foreach (var file in files)
				{
					var ext = System.IO.Path.GetExtension(file);

					foreach (var relevant in RelevantExtensions)
					{
						if (string.Equals(ext, relevant, StringComparison.OrdinalIgnoreCase))
							return true;
					}
				}
			}

			return false;
		}
	}
}
