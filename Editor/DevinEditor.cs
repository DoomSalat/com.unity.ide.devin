using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UCE = Unity.CodeEditor.CodeEditor;
using IExternalCodeEditor = Unity.CodeEditor.IExternalCodeEditor;

namespace Unity.Devin.Editor
{
	[InitializeOnLoad]
	public class DevinEditor : IExternalCodeEditor
	{
		// Reflection targets for SyncAll delegation
		private const string SyncVsType = "UnityEditor.SyncVS,UnityEditor";
		private const string SyncSolutionMethod = "SyncSolution";
		private const string VsEditorType = "Microsoft.Unity.VisualStudio.Editor.VisualStudioEditor,Unity.VisualStudio.Editor";
		private const string WindsurfEditorType = "Unity.VisualStudio.Editor.VisualStudioEditor,Unity.Windsurf.Editor";
		private const string SyncAllMethod = "SyncAll";
		private const string InstanceProperty = "Instance";

		// EditorPrefs keys — which package types get .csproj files
		private const string PrefEmbedded = "Devin_Generate_Embedded";
		private const string PrefLocal = "Devin_Generate_Local";
		private const string PrefRegistry = "Devin_Generate_Registry";
		private const string PrefGit = "Devin_Generate_Git";
		private const string PrefBuiltIn = "Devin_Generate_BuiltIn";
		private const string PrefPlayerAssemblies = "Devin_Generate_Player";

		private const float ButtonWidth = 252f;

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
			DrawToggle(PrefRegistry, "Registry packages", defaultValue: true);
			DrawToggle(PrefGit, "Git packages", defaultValue: true);
			DrawToggle(PrefBuiltIn, "Built-in packages", defaultValue: false);
			DrawToggle(PrefPlayerAssemblies, "Player projects", defaultValue: false);
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
			// Unity 2019–2022
			if (TryInvokeStatic(SyncVsType, SyncSolutionMethod))
				return;

			// Unity 6+: delegate to com.unity.ide.visualstudio
			if (TryInvokeInstanceMethod(VsEditorType, SyncAllMethod))
				return;

			// Windsurf fallback
			TryInvokeInstanceMethod(WindsurfEditorType, SyncAllMethod);
		}

		private static bool TryInvokeStatic(string typeAssemblyName, string methodName)
		{
			try
			{
				var method = Type.GetType(typeAssemblyName)
					?.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

				if (method == null)
					return false;

				method.Invoke(null, null);

				return true;
			}
			catch
			{
				return false;
			}
		}

		private static bool TryInvokeInstanceMethod(string typeAssemblyName, string methodName)
		{
			try
			{
				var type = Type.GetType(typeAssemblyName);

				if (type == null)
					return false;

				var instance = type
					.GetProperty(InstanceProperty, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
					?.GetValue(null);

				if (instance == null)
					return false;

				var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

				if (method == null)
					return false;

				method.Invoke(instance, null);

				return true;
			}
			catch
			{
				return false;
			}
		}
	}
}
