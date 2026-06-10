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

		private const string VisualStudio = "VisualStudio";
		private const string DiscoverInstallationsField = "_discoverInstallations";
		private const string VsWhereRelativePath = "Editor\\VSWhere\\vswhere.exe";
		private const string VsForWinTypeName = "Microsoft.Unity.VisualStudio.Editor.VisualStudioForWindowsInstallation";
		private const string TryDiscoverInstallationMethod = "TryDiscoverInstallation";
		private const string VsWhereArguments = "-latest -property installationPath";
		private const string DevenvRelativePath = "Common7\\IDE\\devenv.exe";
		private const string SyncIfNeededMethod = "SyncIfNeeded";
		private const string CsprojPattern = "*.csproj";
		private const string SolutionExtension = ".sln";
		private const string OmniSharpJsonFileName = "omnisharp.json";
		private const string DirectoryBuildPropsFileName = "Directory.Build.props";
		private const string EditorPropertyName = "Editor";
		private const string SolutionJsonKey = "solution";
		private const string LoadProjectsOnDemandJsonKey = "loadProjectsOnDemand";
		private const string ResultPropertyName = "Result";
		private const string ProjectGeneratorPropertyName = "ProjectGenerator";

		// Auto-select priority when no preference is saved
		private static readonly string[] PreferredEditorNames = { "Rider", "VisualStudioCode", "VisualStudio" };

		private static IExternalCodeEditor _cachedDelegate;
		private static bool? _vsFoundViaFallback;

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
			_vsFoundViaFallback = null;
		}

		// Cached check: did the vswhere fallback find VS on the last attempt?
		// null = not yet checked (run on first GUI repaint for VS delegate).
		// Runs vswhere once, then caches; reset on InvalidateCache().
		public static bool? VsFoundViaFallback(Assembly vsAssembly)
		{
			if (_vsFoundViaFallback.HasValue)
				return _vsFoundViaFallback;

			_vsFoundViaFallback = GetGeneratorViaVsWhere(vsAssembly) != null;
			return _vsFoundViaFallback;
		}

		public static bool TrySync(IExternalCodeEditor delegateEditor)
		{
			if (delegateEditor == null)
			{
				Debug.LogWarning("[Devin] No delegate editor selected for project generation.");
				return false;
			}

			bool success;
			var typeName = delegateEditor.GetType().FullName ?? "";

			// VS Tools' SyncAll() bails early when Devin is the selected editor
			// (it checks CodeEditor.CurrentEditorInstallation, gets Devin's path, finds no VS installation).
			// Bypass by reflecting _discoverInstallations → first installation → ProjectGenerator.Sync().
			if (typeName.IndexOf(VisualStudio, StringComparison.OrdinalIgnoreCase) >= 0)
				success = TryInternalSyncVSTools(delegateEditor);
			else
				success = TrySyncAllDirect(delegateEditor);

			if (success)
			{
				WriteOmniSharpSupport();
				Debug.Log($"[Devin] Project files synced via {delegateEditor.GetType().Name}.");
			}

			return success;
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
			try
			{
				var generator = GetVSToolsGenerator(editor);
				if (generator == null)
					return TrySyncAllDirect(editor);

				var syncMethod = generator.GetType().GetMethod("Sync",
					BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
				if (syncMethod == null)
					return TrySyncAllDirect(editor);

				syncMethod.Invoke(generator, null);
				return true;
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[Devin] VS Tools internal sync failed: {ex.Message}. Falling back to SyncAll.");
				return TrySyncAllDirect(editor);
			}
		}

		// Returns the IGenerator from the first discovered VS installation, or null on failure.
		// Fast path: uses VS plugin's own async discovery result.
		// Fallback: VS plugin's GetPackageAssetFullPath is broken on Unity < 6000.5 (uses
		// Path.GetFullPath instead of FileUtil.PathToAbsolutePath, resolves vswhere.exe to a
		// non-existent CWD-relative path). We bypass it by finding vswhere.exe via
		// PackageInfo.resolvedPath, which is always correct regardless of Unity version.
		private static object GetVSToolsGenerator(IExternalCodeEditor editor)
		{
			return GetGeneratorFromDiscovery(editor)
				?? GetGeneratorViaVsWhere(editor.GetType().Assembly);
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

			return ExtractGenerator(firstInstallation);
		}

		private static object GetGeneratorViaVsWhere(Assembly vsAssembly)
		{
			var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(vsAssembly);
			if (packageInfo == null)
				return null;

			var vswhereExe = IOPath.Combine(packageInfo.resolvedPath, VsWhereRelativePath);
			if (!File.Exists(vswhereExe))
				return null;

			var devenvPath = RunVsWhere(vswhereExe);
			if (string.IsNullOrEmpty(devenvPath))
			{
				Debug.LogWarning("[Devin] vswhere found no Visual Studio installations.");
				return null;
			}

			var vsForWinType = vsAssembly.GetType(VsForWinTypeName);
			var discoverMethod = vsForWinType?.GetMethod(TryDiscoverInstallationMethod,
				BindingFlags.Public | BindingFlags.Static);
			if (discoverMethod == null)
				return null;

			var args = new object[] { devenvPath, null };
			if (!(bool)discoverMethod.Invoke(null, args) || args[1] == null)
				return null;

			var generator = ExtractGenerator(args[1]);
			if (generator != null)
				Debug.Log("[Devin] VS discovery fallback succeeded via vswhere.");

			return generator;
		}

		private static object ExtractGenerator(object installation)
		{
			if (installation == null)
				return null;

			return installation.GetType()
				.GetProperty(ProjectGeneratorPropertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
				?.GetValue(installation);
		}

		private static string RunVsWhere(string vswhereExe)
		{
			try
			{
				var psi = new System.Diagnostics.ProcessStartInfo
				{
					FileName = vswhereExe,
					Arguments = "-latest -property installationPath",
					UseShellExecute = false,
					RedirectStandardOutput = true,
					CreateNoWindow = true
				};

				using var proc = System.Diagnostics.Process.Start(psi);
				var installPath = proc?.StandardOutput.ReadToEnd().Trim();
				proc?.WaitForExit();

				if (string.IsNullOrEmpty(installPath))
					return null;

				var devenv = IOPath.Combine(installPath, DevenvRelativePath);
				return File.Exists(devenv) ? devenv : null;
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[Devin] vswhere execution failed: {ex.Message}");
				return null;
			}
		}

		public static bool TrySyncIfNeeded(IExternalCodeEditor delegateEditor,
			string[] added, string[] deleted, string[] moved, string[] movedFrom, string[] imported)
		{
			if (delegateEditor == null)
				return false;

			bool synced = false;
			var typeName = delegateEditor.GetType().FullName ?? "";

			// VS Tools' SyncIfNeeded has the same current-editor guard as SyncAll.
			// IGenerator.SyncIfNeeded(affectedFiles, reimportedFiles) bypasses it directly.
			if (typeName.IndexOf(VisualStudio, StringComparison.OrdinalIgnoreCase) >= 0)
			{
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
					Debug.LogWarning($"[Devin] VS Tools internal SyncIfNeeded failed: {ex.Message}. Falling back.");
				}
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

		private static void WriteOmniSharpSupport()
		{
			var projectDirectory = IOPath.GetDirectoryName(Application.dataPath);
			var projectName = IOPath.GetFileName(projectDirectory);

			WriteDirectoryBuildProps(projectDirectory);
			WriteOmniSharpJson(projectDirectory, projectName);
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

		private static void WriteOmniSharpJson(string projectDirectory, string projectName)
		{
			var solutionPath = IOPath.Combine(projectDirectory, projectName + SolutionExtension).Replace("\\", "\\\\");
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
			if (File.Exists(filePath))
			{
				var existing = File.ReadAllText(filePath);
				if (existing.Contains("\"" + SolutionJsonKey + "\"") && existing.Contains(LoadProjectsOnDemandJsonKey))
					return;
			}

			File.WriteAllText(filePath, content, Encoding.UTF8);
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
