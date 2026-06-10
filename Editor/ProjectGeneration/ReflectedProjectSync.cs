using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using IExternalCodeEditor = Unity.CodeEditor.IExternalCodeEditor;
using UCE = Unity.CodeEditor.CodeEditor;
using IOPath = System.IO.Path;

namespace Unity.Devin.Editor
{
	internal static class ReflectedProjectSync
	{
		internal const string PrefDelegateEditorType = "Devin_Delegate_Editor_Type";

		private const string DiscoverInstallationsField = "_discoverInstallations";
		private const string SyncIfNeededMethod = "SyncIfNeeded";
		private const string CsprojPattern = "*.csproj";
		private const string SolutionExtension = ".sln";
		private const string SolutionXExtension = ".slnx";
		private const string OmniSharpJsonFileName = "omnisharp.json";
		private const string DirectoryBuildPropsFileName = "Directory.Build.props";
		private const string EditorPropertyName = "Editor";
		private const string ResultPropertyName = "Result";
		private const string ProjectGeneratorPropertyName = "ProjectGenerator";
		private const string GeneratorFactoryTypeName = "Microsoft.Unity.VisualStudio.Editor.GeneratorFactory";
		private const string GeneratorStyleTypeName = "Microsoft.Unity.VisualStudio.Editor.GeneratorStyle";
		private const int GeneratorStyleLegacy = 2; // GeneratorStyle.Legacy enum value

		// Auto-select priority when no preference is saved
		private static readonly string[] PreferredEditorNames = { "Rider", "VisualStudioCode", "VisualStudio" };

		private static IExternalCodeEditor _cachedDelegate;

		public static IExternalCodeEditor[] GetAllEditors() =>
			GetRegisteredEditors().Where(e => !(e is DevinEditor)).ToArray();

		public static IExternalCodeEditor FindDelegate()
		{
			if (_cachedDelegate != null)
				return _cachedDelegate;

			var editors = GetAllEditors();
			if (editors.Length == 0)
				return null;

			var savedType = UnityEditor.EditorPrefs.GetString(PrefDelegateEditorType, "");

			if (!string.IsNullOrEmpty(savedType))
			{
				var saved = editors.FirstOrDefault(e => e.GetType().FullName == savedType);
				if (saved != null)
					return _cachedDelegate = saved;
			}

			foreach (var preferred in PreferredEditorNames)
			{
				var found = editors.FirstOrDefault(e =>
					e.GetType().FullName?.IndexOf(preferred, StringComparison.OrdinalIgnoreCase) >= 0);

				if (found != null)
					return _cachedDelegate = found;
			}

			return _cachedDelegate = editors[0];
		}

		public static void InvalidateCache()
		{
			_cachedDelegate = null;
		}

		public static bool TrySync(IExternalCodeEditor delegateEditor)
		{
			if (delegateEditor == null)
			{
				Debug.LogWarning("[Devin] No delegate editor selected for project generation.");
				return false;
			}

			bool success;

			// Always try generator path first — bypasses the current-editor guard in SyncAll()
			// that all VS-family plugins (VS Tools, Cursor, Windsurf, VSCode) use.
			// Falls back to SyncAll() only when no generator is accessible (e.g. Rider).
			success = TryInternalSyncVSTools(delegateEditor);
			if (!success)
				success = TrySyncAllDirect(delegateEditor);

			if (success)
			{
				var solutionPath = FindGeneratedSolutionPath();
				if (solutionPath == null)
				{
					Debug.LogWarning("[Devin] Sync reported success but no .sln/.slnx found. Project generation may have silently failed.");
					return false;
				}

				WriteOmniSharpSupport(solutionPath);
				Debug.Log($"[Devin] Project files synced via {delegateEditor.GetType().Name}. Solution: {solutionPath}");
			}

			return success;
		}

		private static string FindGeneratedSolutionPath()
		{
			var dir = IOPath.GetDirectoryName(Application.dataPath);
			var projectName = IOPath.GetFileName(dir);

			var slnPath = IOPath.Combine(dir, projectName + SolutionExtension);
			if (File.Exists(slnPath))
				return slnPath;

			var slnxPath = IOPath.Combine(dir, projectName + SolutionXExtension);
			if (File.Exists(slnxPath))
				return slnxPath;

			return null;
		}

		private static bool TrySyncAllDirect(IExternalCodeEditor editor)
		{
			try
			{
				editor.SyncAll();
				return true;
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[Devin] SyncAll via {editor.GetType().Name} failed: {ex.Message}");
				return false;
			}
		}

		// Bypasses VS Tools' current-editor check by calling ProjectGenerator.Sync() directly.
		// VS Tools stores installations in a static AsyncOperation<Dictionary<string, IVisualStudioInstallation>>.
		private static bool TryInternalSyncVSTools(IExternalCodeEditor editor)
		{
			var generator = GetVSToolsGenerator(editor);
			if (generator == null)
				return false;

			var syncMethod = generator.GetType().GetMethod("Sync",
				BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
			if (syncMethod == null)
			{
				Debug.LogWarning($"[Devin] VS Tools generator type {generator.GetType().FullName} has no Sync() method.");
				return false;
			}

			try
			{
				syncMethod.Invoke(generator, null);
				return true;
			}
			catch (Exception ex)
			{
				var inner = (ex as System.Reflection.TargetInvocationException)?.InnerException ?? ex;
				Debug.LogWarning($"[Devin] VS Tools Sync() threw: {inner.GetType().Name}: {inner.Message}");
				return false;
			}
		}

		private static bool IsVSToolsAssembly(Assembly assembly) =>
			assembly.GetType(GeneratorFactoryTypeName) != null;

		// Returns an IGenerator from the editor's assembly, bypassing the current-editor guard in SyncAll().
		// All VS-family plugins (VS Tools, Cursor, Windsurf, VS Code) store generators in static fields
		// on their installation classes. We try three paths in order:
		//   1. _discoverInstallations  — works when the IDE is installed and discovered
		//   2. GeneratorFactory        — VS Tools only (Legacy style → .sln)
		//   3. Static IGenerator scan  — catches Cursor/VSCode pattern (_generator field)
		private static object GetVSToolsGenerator(IExternalCodeEditor editor)
		{
			var vsAssembly = editor.GetType().Assembly;

			var fromDiscovery = GetGeneratorFromDiscovery(editor);
			if (fromDiscovery != null)
				return fromDiscovery;

			if (IsVSToolsAssembly(vsAssembly))
				return GetGeneratorViaFactory(vsAssembly);

			return GetGeneratorFromStaticField(vsAssembly);
		}

		private static object GetGeneratorFromDiscovery(IExternalCodeEditor editor)
		{
			var discoverField = editor.GetType().GetField(DiscoverInstallationsField,
				BindingFlags.NonPublic | BindingFlags.Static);
			if (discoverField == null)
				return null;

			var asyncOp = discoverField.GetValue(null);
			if (asyncOp == null)
				return null;

			var resultProp = asyncOp.GetType().GetProperty(ResultPropertyName, BindingFlags.Public | BindingFlags.Instance);
			if (resultProp == null)
				return null;

			var dict = resultProp.GetValue(asyncOp) as IDictionary;
			if (dict == null || dict.Count == 0)
				return null;

			object firstInstallation = null;
			foreach (var value in dict.Values) { firstInstallation = value; break; }

			return firstInstallation?.GetType()
				.GetProperty(ProjectGeneratorPropertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
				?.GetValue(firstInstallation);
		}

		private static object GetGeneratorViaFactory(Assembly vsAssembly)
		{
			var factoryType = vsAssembly.GetType(GeneratorFactoryTypeName);
			if (factoryType == null)
			{
				Debug.LogWarning($"[Devin] {GeneratorFactoryTypeName} not found in VS assembly.");
				return null;
			}

			// Primary: read the cached legacy instance directly — avoids enum-type matching issues.
			var legacyField = factoryType.GetField("_legacyStyleProjectGeneration",
				BindingFlags.NonPublic | BindingFlags.Static);
			if (legacyField != null)
			{
				var generator = legacyField.GetValue(null);
				if (generator != null)
				{
					Debug.Log($"[Devin] Got VS generator via _legacyStyleProjectGeneration: {generator.GetType().Name}");
					return generator;
				}
			}

			// Secondary: call GetInstance(Legacy=2) via reflection.
			var styleType = vsAssembly.GetType(GeneratorStyleTypeName);
			if (styleType == null)
			{
				Debug.LogWarning($"[Devin] {GeneratorStyleTypeName} not found in VS assembly.");
				return null;
			}

			var getInstanceMethod = factoryType.GetMethods(BindingFlags.Public | BindingFlags.Static)
				.FirstOrDefault(m => m.Name == "GetInstance" && m.GetParameters().Length == 1);
			if (getInstanceMethod == null)
			{
				Debug.LogWarning($"[Devin] GeneratorFactory.GetInstance not found.");
				return null;
			}

			try
			{
				var legacyStyle = Enum.ToObject(styleType, GeneratorStyleLegacy);
				var generator = getInstanceMethod.Invoke(null, new[] { legacyStyle });
				Debug.Log($"[Devin] Got VS generator via GeneratorFactory.GetInstance: {generator?.GetType().Name}");
				return generator;
			}
			catch (Exception ex)
			{
				var inner = (ex as System.Reflection.TargetInvocationException)?.InnerException ?? ex;
				Debug.LogWarning($"[Devin] GeneratorFactory.GetInstance threw: {inner.GetType().Name}: {inner.Message}");
				return null;
			}
		}

		// Scans all static fields of IGenerator type across all types in the assembly.
		// Handles VS Code-family plugins (Cursor, Windsurf) that store a static _generator
		// field directly on their installation class without a GeneratorFactory.
		private static object GetGeneratorFromStaticField(Assembly assembly)
		{
			Type[] types;
			try
			{
				types = assembly.GetTypes();
			}
			catch (ReflectionTypeLoadException ex)
			{
				types = ex.Types.Where(t => t != null).ToArray();
			}

			const string generatorInterface = "IGenerator";

			foreach (var type in types)
			{
				if (!type.IsClass)
					continue;

				foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Static))
				{
					if (!field.FieldType.GetInterfaces().Any(i => i.Name == generatorInterface)
						&& field.FieldType.Name != generatorInterface)
						continue;

					try
					{
						var value = field.GetValue(null);
						if (value == null)
							continue;

						var syncMethod = value.GetType().GetMethod("Sync",
							BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
						if (syncMethod == null)
							continue;

						Debug.Log($"[Devin] Got generator via static field {type.Name}.{field.Name}: {value.GetType().Name}");
						return value;
					}
					catch { }
				}
			}

			Debug.LogWarning($"[Devin] No static IGenerator field found in {assembly.GetName().Name}.");
			return null;
		}

		public static bool TrySyncIfNeeded(IExternalCodeEditor delegateEditor,
			string[] added, string[] deleted, string[] moved, string[] movedFrom, string[] imported)
		{
			if (delegateEditor == null)
				return false;

			bool synced = false;

			// VS-family editors (VS Tools, Cursor, etc.) have the same current-editor guard in SyncIfNeeded.
			// IGenerator.SyncIfNeeded(affectedFiles, reimportedFiles) bypasses it directly.
			try
			{
				var generator = GetVSToolsGenerator(delegateEditor);
				if (generator != null)
				{
					// IGenerator.SyncIfNeeded(IEnumerable<string> affectedFiles, IEnumerable<string> reimportedFiles)
					var method = generator.GetType().GetMethod(SyncIfNeededMethod,
						BindingFlags.Public | BindingFlags.Instance, null,
						new[] { typeof(IEnumerable<string>), typeof(IEnumerable<string>) }, null);

					if (method != null)
					{
						var affected = ConcatArrays(added, deleted, moved, movedFrom);
						method.Invoke(generator, new object[] { affected, (IEnumerable<string>)(imported ?? Array.Empty<string>()) });
						synced = true;
					}
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[Devin] Internal SyncIfNeeded failed: {ex.Message}. Falling back.");
			}

			if (!synced)
			{
				try
				{
					delegateEditor.SyncIfNeeded(added, deleted, moved, movedFrom, imported);
					synced = true;
				}
				catch (Exception ex)
				{
					Debug.LogWarning($"[Devin] SyncIfNeeded via {delegateEditor.GetType().Name} failed: {ex.Message}");
				}
			}

			// SDK-style .csproj includes files via glob — the delegate may not modify the .csproj
			// at all when a new .cs file is added. Touch all .csproj files so OmniSharp re-evaluates
			// the glob and discovers the new file without requiring a manual restart.
			if (synced && HasAddedOrDeleted(added, deleted))
				TouchCsprojFiles(IOPath.GetDirectoryName(Application.dataPath));

			return synced;
		}

		private static bool HasAddedOrDeleted(string[] added, string[] deleted)
		{
			return (added != null && added.Length > 0) || (deleted != null && deleted.Length > 0);
		}

		private static void TouchCsprojFiles(string projectDirectory)
		{
			var now = DateTime.UtcNow;
			try
			{
				foreach (var csproj in Directory.GetFiles(projectDirectory, CsprojPattern))
					File.SetLastWriteTimeUtc(csproj, now);
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[Devin] Failed to touch .csproj files: {ex.Message}");
			}
		}

		private static IEnumerable<string> ConcatArrays(params string[][] arrays)
		{
			foreach (var arr in arrays)
			{
				if (arr == null) continue;
				foreach (var s in arr) yield return s;
			}
		}

		private static void WriteOmniSharpSupport(string solutionPath)
		{
			var projectDirectory = IOPath.GetDirectoryName(Application.dataPath);

			WriteDirectoryBuildProps(projectDirectory);
			WriteOmniSharpJson(projectDirectory, solutionPath);
		}

		private static void WriteDirectoryBuildProps(string projectDirectory)
		{
			const string nl = "\r\n";
			var content =
				"<!-- Generated by Unity.Devin — do not edit manually -->" + nl +
				"<Project>" + nl +
				"  <PropertyGroup>" + nl +
				"    <SkipGetTargetFrameworkProperties>true</SkipGetTargetFrameworkProperties>" + nl +
				"    <NoRestore>true</NoRestore>" + nl +
				"  </PropertyGroup>" + nl +
				"</Project>" + nl;

			WriteIfChanged(IOPath.Combine(projectDirectory, DirectoryBuildPropsFileName), content);
		}

		private static void WriteOmniSharpJson(string projectDirectory, string solutionPath)
		{
			solutionPath = solutionPath.Replace("\\", "\\\\");
			var content =
				"{\r\n" +
				"  \"msbuild\": {\r\n" +
				$"    \"solution\": \"{solutionPath}\",\r\n" +
				"    \"loadProjectsOnDemand\": false,\r\n" +
				"    \"enablePackageAutoRestore\": false,\r\n" +
				"    \"Properties\": {\r\n" +
				"      \"DesignTimeBuild\": \"true\"\r\n" +
				"    }\r\n" +
				"  },\r\n" +
				"  \"RoslynExtensionsOptions\": {\r\n" +
				"    \"EnableAnalyzersSupport\": false,\r\n" +
				"    \"AnalyzeOpenDocumentsOnly\": true,\r\n" +
				"    \"EnableImportCompletion\": true,\r\n" +
				"    \"DiagnosticWorkersThreadCount\": 4,\r\n" +
				"    \"DocumentAnalysisTimeoutMs\": 15000\r\n" +
				"  }\r\n" +
				"}";

			var filePath = IOPath.Combine(projectDirectory, OmniSharpJsonFileName);
			WriteIfChanged(filePath, content);
		}

		private static void WriteIfChanged(string filePath, string content)
		{
			if (File.Exists(filePath) && File.ReadAllText(filePath, Encoding.UTF8) == content)
				return;

			File.WriteAllText(filePath, content, Encoding.UTF8);
		}

		private static IExternalCodeEditor[] GetRegisteredEditors()
		{
			var codeEditorType = typeof(UCE);

			// Unity stores editors in an instance field on a static CodeEditor instance.
			// Strategy: find the static CodeEditor instance first, then look for the list on it.
			// Fallback: also scan static fields directly (older Unity versions).

			var instances = new List<object> { null }; // null = search static fields

			// Try public static property "Editor"
			var editorProp = codeEditorType.GetProperty(EditorPropertyName,
				BindingFlags.Public | BindingFlags.Static);
			if (editorProp != null)
				instances.Insert(0, editorProp.GetValue(null));

			// Try any static field of type CodeEditor
			foreach (var f in codeEditorType.GetFields(BindingFlags.NonPublic | BindingFlags.Static))
			{
				if (f.FieldType == codeEditorType)
					instances.Insert(0, f.GetValue(null));
			}

			foreach (var instance in instances)
			{
				var flags = instance != null
					? BindingFlags.NonPublic | BindingFlags.Instance
					: BindingFlags.NonPublic | BindingFlags.Static;

				foreach (var field in codeEditorType.GetFields(flags))
				{
					if (!typeof(IEnumerable).IsAssignableFrom(field.FieldType))
						continue;

					var value = field.GetValue(instance);
					if (value is not IEnumerable enumerable)
						continue;

					var result = new List<IExternalCodeEditor>();
					foreach (var item in enumerable)
					{
						if (item is IExternalCodeEditor editor)
							result.Add(editor);
					}

					if (result.Count > 0)
						return result.ToArray();
				}
			}

			return Array.Empty<IExternalCodeEditor>();
		}
	}
}
