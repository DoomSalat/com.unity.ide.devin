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
		private const float ButtonWidth = 252f;

		private static readonly string[] RelevantExtensions = { ".cs", ".asmdef", ".asmref", ".dll", ".rsp" };

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

		public void OnGUI()
		{
			if (!IsDevinSelected())
				return;

			var delegateEditor = ReflectedProjectSync.FindDelegate();

			if (delegateEditor == null)
			{
				EditorGUILayout.HelpBox(
					"No compatible editor found for project generation.\n" +
					"Install Rider or VS Tools for Unity to enable .csproj generation.",
					MessageType.Warning);
			}
			else
			{
				EditorGUILayout.LabelField("Project generation delegated to:", EditorStyles.boldLabel);
				EditorGUI.indentLevel++;
				EditorGUILayout.LabelField(delegateEditor.GetType().Name, EditorStyles.label);
				EditorGUI.indentLevel--;
				GUILayout.Space(4);
				EditorGUILayout.HelpBox(
					"Generation settings (package types, etc.) are controlled by the delegate editor's preferences.",
					MessageType.Info);
			}

			GUILayout.Space(4);

			if (GUILayout.Button("Regenerate project files", GUILayout.Width(ButtonWidth)))
				SyncAll();
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
