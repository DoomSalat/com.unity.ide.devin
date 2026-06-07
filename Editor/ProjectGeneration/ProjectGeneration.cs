using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using PackageSource = UnityEditor.PackageManager.PackageSource;
using UnityEngine;
using IOPath = System.IO.Path;
using CompAssembly = UnityEditor.Compilation.Assembly;

namespace Unity.Devin.Editor
{
	internal static class ProjectGeneration
	{
		private const string Newline = "\r\n";

		private const string CsharpProjectTypeGuid = "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";

		public static void Sync(ProjectGenerationFlag flag)
		{
			var projectDirectory = IOPath.GetDirectoryName(Application.dataPath);
			var projectName = IOPath.GetFileName(projectDirectory);

			var assemblies = GatherAssemblies(flag, projectDirectory);
			var includedNames = new HashSet<string>(assemblies.Select(assembly => assembly.name));

			var generatedProjectFiles = new HashSet<string>(assemblies.Select(a => a.name + ".csproj"), StringComparer.OrdinalIgnoreCase);

			foreach (var stale in Directory.GetFiles(projectDirectory, "*.csproj"))
			{
				if (!generatedProjectFiles.Contains(IOPath.GetFileName(stale)))
				{
					File.Delete(stale);
				}
			}

			var writtenCount = 0;

			foreach (var assembly in assemblies)
			{
				var projectFilePath = IOPath.Combine(projectDirectory, assembly.name + ".csproj");
				var content = BuildProjectText(assembly, includedNames, projectDirectory);

				if (WriteIfChanged(projectFilePath, content))
					writtenCount++;
			}

			var solutionPath = IOPath.Combine(projectDirectory, projectName + ".sln");
			var directoryBuildPropsPath = IOPath.Combine(projectDirectory, "Directory.Build.props");

			foreach (var staleSolution in Directory.GetFiles(projectDirectory, "*.sln"))
			{
				if (!IOPath.GetFileName(staleSolution).Equals(projectName + ".sln", StringComparison.OrdinalIgnoreCase))
				{
					File.Delete(staleSolution);
				}
			}

			foreach (var staleSolutionX in Directory.GetFiles(projectDirectory, "*.slnx"))
			{
				File.Delete(staleSolutionX);
			}

			WriteIfChanged(solutionPath, BuildSolutionText(assemblies));
			WriteIfChanged(directoryBuildPropsPath, BuildDirectoryBuildProps());
			WriteOmniSharpJson(projectDirectory);

			Debug.Log($"[{nameof(ProjectGeneration)}] Synced {assemblies.Length} projects ({writtenCount} updated).");
		}

		private static CompAssembly[] GatherAssemblies(ProjectGenerationFlag flag, string projectDirectory)
		{
			var allAssemblies = new List<CompAssembly>(CompilationPipeline.GetAssemblies(AssembliesType.Editor));

			if ((flag & ProjectGenerationFlag.PlayerAssemblies) != 0)
			{
				allAssemblies.AddRange(CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies));
			}

			return allAssemblies
				.Where(assembly => assembly.sourceFiles.Length > 0)
				.Where(assembly => ShouldInclude(assembly, flag, projectDirectory))
				.ToArray();
		}

		private static bool ShouldInclude(CompAssembly assembly, ProjectGenerationFlag flag, string projectDirectory)
		{
			var absolutePath = assembly.sourceFiles[0].Replace('\\', '/');
			var packageInfo = PackageInfo.FindForAssetPath(absolutePath);

			if (packageInfo == null)
			{
				return true;
			}

			return packageInfo.source switch
			{
				PackageSource.Embedded => (flag & ProjectGenerationFlag.Embedded) != 0,
				PackageSource.Local => (flag & ProjectGenerationFlag.Local) != 0,
				PackageSource.Registry => (flag & ProjectGenerationFlag.Registry) != 0,
				PackageSource.Git => (flag & ProjectGenerationFlag.Git) != 0,
				PackageSource.BuiltIn => (flag & ProjectGenerationFlag.BuiltIn) != 0,
				PackageSource.Unknown => (flag & ProjectGenerationFlag.Unknown) != 0,
				PackageSource.LocalTarball => (flag & ProjectGenerationFlag.LocalTarball) != 0,
				_ => true,
			};
		}

		private static string BuildProjectText(CompAssembly assembly, HashSet<string> includedNames, string projectDirectory)
		{
			var defines = string.Join(";", assembly.defines);
			var allowUnsafe = assembly.compilerOptions.AllowUnsafeCode ? "true" : "false";
			var outputDirectory = ToRelativePath(IOPath.GetDirectoryName(assembly.outputPath), projectDirectory);

			var projectReferenceNames = new HashSet<string>(
				assembly.assemblyReferences
					.Where(reference => includedNames.Contains(reference.name))
					.Select(reference => reference.name));

			var stringBuilder = new StringBuilder();

			stringBuilder.Append(@"<Project Sdk=""Microsoft.NET.Sdk"">").Append(Newline);
			stringBuilder.Append(@"  <PropertyGroup>").Append(Newline);
			stringBuilder.Append(@"    <LangVersion>latest</LangVersion>").Append(Newline);
			stringBuilder.Append(@"    <TargetFramework>net471</TargetFramework>").Append(Newline);
			stringBuilder.Append($@"    <RootNamespace>{assembly.name}</RootNamespace>").Append(Newline);
			stringBuilder.Append($@"    <AssemblyName>{assembly.name}</AssemblyName>").Append(Newline);
			stringBuilder.Append(@"    <OutputType>Library</OutputType>").Append(Newline);
			stringBuilder.Append($@"    <OutputPath>{outputDirectory}</OutputPath>").Append(Newline);
			stringBuilder.Append($@"    <DefineConstants>{defines}</DefineConstants>").Append(Newline);
			stringBuilder.Append(@"    <NoWarn>0169;USG0001</NoWarn>").Append(Newline);
			stringBuilder.Append($@"    <AllowUnsafeBlocks>{allowUnsafe}</AllowUnsafeBlocks>").Append(Newline);
			stringBuilder.Append(@"    <NoConfig>true</NoConfig>").Append(Newline);
			stringBuilder.Append(@"    <NoStdLib>true</NoStdLib>").Append(Newline);
			stringBuilder.Append(@"    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>").Append(Newline);
			stringBuilder.Append(@"    <Nullable>disable</Nullable>").Append(Newline);
			stringBuilder.Append(@"  </PropertyGroup>").Append(Newline);

			AppendSourceFiles(stringBuilder, assembly, projectDirectory);
			AppendDllReferences(stringBuilder, assembly, projectReferenceNames);
			AppendProjectReferences(stringBuilder, assembly, includedNames, projectDirectory);

			stringBuilder.Append(@"</Project>").Append(Newline);

			return stringBuilder.ToString();
		}

		private static void AppendSourceFiles(StringBuilder stringBuilder, CompAssembly assembly, string projectDirectory)
		{
			stringBuilder.Append(@"  <ItemGroup>").Append(Newline);

			foreach (var file in assembly.sourceFiles)
			{
				var relativePath = ToRelativePath(file, projectDirectory);
				stringBuilder.Append($@"    <Compile Include=""{relativePath}"" />").Append(Newline);
			}

			stringBuilder.Append(@"  </ItemGroup>").Append(Newline);
		}

		private static void AppendDllReferences(StringBuilder stringBuilder, CompAssembly assembly, HashSet<string> projectReferenceNames)
		{
			var dllReferences = assembly.compiledAssemblyReferences
				.Where(path => !string.IsNullOrEmpty(path))
				.Where(path => !projectReferenceNames.Contains(IOPath.GetFileNameWithoutExtension(path)))
				.ToArray();

			if (dllReferences.Length == 0)
			{
				return;
			}

			stringBuilder.Append(@"  <ItemGroup>").Append(Newline);

			foreach (var dllReferencePath in dllReferences)
			{
				var assemblyName = IOPath.GetFileNameWithoutExtension(dllReferencePath);
				stringBuilder.Append($@"    <Reference Include=""{assemblyName}"">").Append(Newline);
				stringBuilder.Append($@"      <HintPath>{dllReferencePath}</HintPath>").Append(Newline);
				stringBuilder.Append(@"    </Reference>").Append(Newline);
			}

			stringBuilder.Append(@"  </ItemGroup>").Append(Newline);
		}

		private static void AppendProjectReferences(StringBuilder stringBuilder, CompAssembly assembly, HashSet<string> includedNames, string projectDirectory)
		{
			var projectReferences = assembly.assemblyReferences
				.Where(reference => includedNames.Contains(reference.name))
				.ToArray();

			var missingDllReferences = assembly.assemblyReferences
				.Where(reference => !includedNames.Contains(reference.name))
				.Select(reference => IOPath.Combine(projectDirectory, "Library", "ScriptAssemblies", reference.name + ".dll"))
				.Where(File.Exists)
				.ToArray();

			if (projectReferences.Length == 0 && missingDllReferences.Length == 0)
			{
				return;
			}

			stringBuilder.Append(@"  <ItemGroup>").Append(Newline);

			foreach (var reference in projectReferences)
			{
				var referenceGuid = StableGuid(reference.name).ToString("B").ToUpperInvariant();
				stringBuilder.Append($@"    <ProjectReference Include=""{reference.name}.csproj"">").Append(Newline);
				stringBuilder.Append($@"      <Project>{referenceGuid}</Project>").Append(Newline);
				stringBuilder.Append($@"      <Name>{reference.name}</Name>").Append(Newline);
				stringBuilder.Append(@"    </ProjectReference>").Append(Newline);
			}

			foreach (var dllPath in missingDllReferences)
			{
				var assemblyName = IOPath.GetFileNameWithoutExtension(dllPath);
				stringBuilder.Append($@"    <Reference Include=""{assemblyName}"">").Append(Newline);
				stringBuilder.Append($@"      <HintPath>{dllPath}</HintPath>").Append(Newline);
				stringBuilder.Append(@"    </Reference>").Append(Newline);
			}

			stringBuilder.Append(@"  </ItemGroup>").Append(Newline);
		}

		private static bool WriteIfChanged(string filePath, string content)
		{
			if (File.Exists(filePath) && File.ReadAllText(filePath, Encoding.UTF8) == content)
				return false;

			File.WriteAllText(filePath, content, Encoding.UTF8);
			return true;
		}

		private static void WriteOmniSharpJson(string projectDirectory)
		{
			const string fileName = "omnisharp.json";

			var projectName = IOPath.GetFileName(projectDirectory);
			var solutionPath = IOPath.Combine(projectDirectory, projectName + ".sln").Replace("\\", "\\\\");

			var content =
				"{" + "\r\n" +
				"  \"msbuild\": {" + "\r\n" +
				$"    \"solution\": \"{solutionPath}\"," + "\r\n" +
				"    \"loadProjectsOnDemand\": false," + "\r\n" +
				"    \"enablePackageAutoRestore\": false," + "\r\n" +
				"    \"Properties\": {" + "\r\n" +
				"      \"DesignTimeBuild\": \"true\"" + "\r\n" +
				"    }" + "\r\n" +
				"  }," + "\r\n" +
				"  \"RoslynExtensionsOptions\": {" + "\r\n" +
				"    \"EnableAnalyzersSupport\": false," + "\r\n" +
				"    \"AnalyzeOpenDocumentsOnly\": true," + "\r\n" +
				"    \"EnableImportCompletion\": true," + "\r\n" +
				"    \"DiagnosticWorkersThreadCount\": 4," + "\r\n" +
				"    \"DocumentAnalysisTimeoutMs\": 15000" + "\r\n" +
				"  }" + "\r\n" +
				"}";

			var filePath = IOPath.Combine(projectDirectory, fileName);

			if (File.Exists(filePath))
			{
				var existingContent = File.ReadAllText(filePath);

				if (existingContent.Contains("\"solution\"") && existingContent.Contains("loadProjectsOnDemand\": false"))
				{
					return;
				}
			}

			File.WriteAllText(filePath, content, Encoding.UTF8);
		}

		private static string BuildDirectoryBuildProps()
		{
			var stringBuilder = new StringBuilder();

			stringBuilder.Append(@"<!-- Generated by Unity.Devin — do not edit manually -->").Append(Newline);
			stringBuilder.Append(@"<Project>").Append(Newline);
			stringBuilder.Append(@"  <PropertyGroup>").Append(Newline);
			stringBuilder.Append(@"    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>").Append(Newline);
			stringBuilder.Append(@"    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>").Append(Newline);
			stringBuilder.Append(@"    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>").Append(Newline);
			stringBuilder.Append(@"    <SkipGetTargetFrameworkProperties>true</SkipGetTargetFrameworkProperties>").Append(Newline);
			stringBuilder.Append(@"    <NoRestore>true</NoRestore>").Append(Newline);
			stringBuilder.Append(@"  </PropertyGroup>").Append(Newline);
			stringBuilder.Append(@"</Project>").Append(Newline);

			return stringBuilder.ToString();
		}

		private static string BuildSolutionText(CompAssembly[] assemblies)
		{
			var stringBuilder = new StringBuilder();

			stringBuilder.Append("Microsoft Visual Studio Solution File, Format Version 12.00").Append(Newline);
			stringBuilder.Append("# Visual Studio Version 16").Append(Newline);
			stringBuilder.Append("VisualStudioVersion = 16.0.0.0").Append(Newline);
			stringBuilder.Append("MinimumVisualStudioVersion = 10.0.40219.1").Append(Newline);

			foreach (var assembly in assemblies)
			{
				var projectGuid = StableGuid(assembly.name).ToString("B").ToUpperInvariant();
				stringBuilder.Append($@"Project(""{{{CsharpProjectTypeGuid}}}"") = ""{assembly.name}"", ""{assembly.name}.csproj"", ""{projectGuid}""").Append(Newline);
				stringBuilder.Append("EndProject").Append(Newline);
			}

			stringBuilder.Append("Global").Append(Newline);
			stringBuilder.Append("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution").Append(Newline);
			stringBuilder.Append("\t\tDebug|Any CPU = Debug|Any CPU").Append(Newline);
			stringBuilder.Append("\t\tRelease|Any CPU = Release|Any CPU").Append(Newline);
			stringBuilder.Append("\tEndGlobalSection").Append(Newline);
			stringBuilder.Append("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution").Append(Newline);

			foreach (var assembly in assemblies)
			{
				var projectGuid = StableGuid(assembly.name).ToString("B").ToUpperInvariant();
				stringBuilder.Append($"\t\t{projectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU").Append(Newline);
				stringBuilder.Append($"\t\t{projectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU").Append(Newline);
				stringBuilder.Append($"\t\t{projectGuid}.Release|Any CPU.ActiveCfg = Release|Any CPU").Append(Newline);
				stringBuilder.Append($"\t\t{projectGuid}.Release|Any CPU.Build.0 = Release|Any CPU").Append(Newline);
			}

			stringBuilder.Append("\tEndGlobalSection").Append(Newline);
			stringBuilder.Append("\tGlobalSection(SolutionProperties) = preSolution").Append(Newline);
			stringBuilder.Append("\t\tHideSolutionNode = FALSE").Append(Newline);
			stringBuilder.Append("\tEndGlobalSection").Append(Newline);
			stringBuilder.Append("EndGlobal").Append(Newline);

			return stringBuilder.ToString();
		}

		private static Guid StableGuid(string name)
		{
			using var md5HashAlgorithm = MD5.Create();
			var hashBytes = md5HashAlgorithm.ComputeHash(Encoding.UTF8.GetBytes(name));

			return new Guid(hashBytes);
		}

		private static string ToRelativePath(string path, string projectDirectory)
		{
			if (!IOPath.IsPathRooted(path))
			{
				return path.Replace('/', '\\');
			}

			if (path.StartsWith(projectDirectory, StringComparison.OrdinalIgnoreCase))
			{
				return path.Substring(projectDirectory.Length).TrimStart('\\', '/').Replace('/', '\\');
			}

			return path;
		}

		private static string ToAssetPath(string path, string projectDirectory)
		{
			if (!IOPath.IsPathRooted(path))
			{
				return path.Replace('\\', '/');
			}

			if (path.StartsWith(projectDirectory, StringComparison.OrdinalIgnoreCase))
			{
				return path.Substring(projectDirectory.Length).TrimStart('\\', '/').Replace('\\', '/');
			}

			return path.Replace('\\', '/');
		}
	}
}
