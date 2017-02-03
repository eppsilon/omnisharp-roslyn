﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.DotNet.ProjectModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OmniSharp.DotNet.Cache;
using OmniSharp.DotNet.Extensions;
using OmniSharp.DotNet.Models;
using OmniSharp.DotNet.Tools;
using OmniSharp.Models;
using OmniSharp.Models.v1;
using OmniSharp.Services;
using OmniSharp.Services.FileWatching;

namespace OmniSharp.DotNet
{
    [Export(typeof(IProjectSystem)), Shared]
    public class DotNetProjectSystem : IProjectSystem
    {
        private const string CompilationConfiguration = "Debug";

        private readonly IOmniSharpEnvironment _environment;
        private readonly OmniSharpWorkspace _workspace;
        private readonly DotNetCliService _dotNetCliService;
        private readonly IMetadataFileReferenceCache _metadataFileReferenceCache;
        private readonly IEventEmitter _eventEmitter;
        private readonly IFileSystemWatcher _fileSystemWatcher;
        private readonly ILogger _logger;
        private readonly ProjectStatesCache _projectStates;

        private DotNetWorkspace _workspaceContext;
        private bool _enableRestorePackages;

        [ImportingConstructor]
        public DotNetProjectSystem(
            IOmniSharpEnvironment environment,
            OmniSharpWorkspace workspace,
            DotNetCliService dotNetCliService,
            IMetadataFileReferenceCache metadataFileReferenceCache,
            IEventEmitter eventEmitter,
            IFileSystemWatcher fileSystemWatcher,
            ILoggerFactory loggerFactory)
        {
            _environment = environment;
            _workspace = workspace;
            _dotNetCliService = dotNetCliService;
            _metadataFileReferenceCache = metadataFileReferenceCache;
            _eventEmitter = eventEmitter;
            _fileSystemWatcher = fileSystemWatcher;
            _logger = loggerFactory.CreateLogger<DotNetProjectSystem>();

            _projectStates = new ProjectStatesCache(loggerFactory, _eventEmitter);
        }

        public IEnumerable<string> Extensions { get; } = new string[] { ".cs" };

        public string Key => "DotNet";

        public string Language => LanguageNames.CSharp;

        Task<object> IProjectSystem.GetWorkspaceModelAsync(WorkspaceInformationRequest request)
        {
            return Task.FromResult<object>(
                new DotNetWorkspaceInformation(
                    entries: _projectStates.GetStates,
                    includeSourceFiles: !request.ExcludeSourceFiles));
        }

        Task<object> IProjectSystem.GetProjectModelAsync(string filePath)
        {
            _logger.LogDebug($"GetProjectModel: {filePath}");

            var document = _workspace.GetDocument(filePath);

            var projectFilePath = document != null
                ? document.Project.FilePath
                : filePath;

            var projectEntry = _projectStates.GetEntry(projectFilePath);
            if (projectEntry == null)
            {
                return Task.FromResult<object>(null);
            }

            return Task.FromResult<object>(
                new DotNetProjectInformation(projectEntry));
        }

        public void Initalize(IConfiguration configuration)
        {
            bool enabled;
            if (!bool.TryParse(configuration["enabled"], out enabled))
            {
                enabled = true;
            }

            if (!enabled)
            {
                _logger.LogInformation("DotNetProjectSystem is disabled");
                return;
            }

            _logger.LogInformation($"Initializing in {_environment.Path}");

            if (!bool.TryParse(configuration["enablePackageRestore"], out _enableRestorePackages))
            {
                _enableRestorePackages = false;
            }

            _logger.LogInformation($"Auto package restore: {_enableRestorePackages}");

            _workspaceContext = new DotNetWorkspace(_environment.Path);

            Update(allowRestore: true);
        }

        public void Update(bool allowRestore)
        {
            _logger.LogInformation("Update workspace context");
            _workspaceContext.Refresh();

            var projectPaths = _workspaceContext.GetAllProjects();

            _projectStates.RemoveExcept(projectPaths, entry =>
            {
                foreach (var state in entry.ProjectStates)
                {
                    _workspace.RemoveProject(state.Id);
                    _logger.LogInformation($"Removing project {state.Id}.");
                }
            });

            foreach (var projectPath in projectPaths)
            {
                UpdateProject(projectPath);
            }

            _logger.LogInformation("Resolving projects references");
            foreach (var state in _projectStates.GetValues())
            {
                _logger.LogInformation($"  Processing {state}");

                var lens = new ProjectContextLens(state.ProjectContext, CompilationConfiguration);
                UpdateFileReferences(state, lens.FileReferences);
                UpdateProjectReferences(state, lens.ProjectReferences);
                UpdateUnresolvedDependencies(state, allowRestore);
                UpdateCompilationOption(state);
                UpdateSourceFiles(state, lens.SourceFiles);
            }
        }

        private void UpdateProject(string projectDirectory)
        {
            _logger.LogInformation($"Update project {projectDirectory}");
            var contexts = _workspaceContext.GetProjectContexts(projectDirectory);

            if (!contexts.Any())
            {
                _logger.LogWarning($"Cannot create any {nameof(ProjectContext)} from project {projectDirectory}");
                return;
            }

            _projectStates.Update(projectDirectory, contexts, AddProject, RemoveProject);

            var projectFilePath = contexts.First().ProjectFile.ProjectFilePath;
            _fileSystemWatcher.Watch(projectFilePath, file =>
            {
                _logger.LogInformation($"Watcher: {file} updated.");
                Update(allowRestore: true);
            });

            _fileSystemWatcher.Watch(Path.ChangeExtension(projectFilePath, "lock.json"), file =>
            {
                _logger.LogInformation($"Watcher: {file} updated.");
                Update(allowRestore: false);
            });
        }

        private void AddProject(ProjectId id, ProjectContext context)
        {
            var info = ProjectInfo.Create(
                id: id,
                version: VersionStamp.Create(),
                name: $"{context.ProjectFile.Name}+{context.TargetFramework.GetShortFolderName()}",
                assemblyName: context.ProjectFile.Name,
                language: LanguageNames.CSharp,
                filePath: context.ProjectFile.ProjectFilePath);

            _workspace.AddProject(info);

            _logger.LogInformation($"Add project {context.ProjectFile.ProjectFilePath} => {id.Id}");
        }

        private void RemoveProject(ProjectId projectId)
        {
            _workspace.RemoveProject(projectId);
        }

        private void UpdateFileReferences(ProjectState state, IEnumerable<string> fileReferences)
        {
            var metadataReferences = new List<MetadataReference>();
            var fileReferencesToRemove = state.FileMetadataReferences.Keys.ToHashSet();

            foreach (var fileReference in fileReferences)
            {
                if (!File.Exists(fileReference))
                {
                    continue;
                }

                if (fileReferencesToRemove.Remove(fileReference))
                {
                    continue;
                }

                var metadataReference = _metadataFileReferenceCache.GetMetadataReference(fileReference);
                state.FileMetadataReferences[fileReference] = metadataReference;
                metadataReferences.Add(metadataReference);
                _logger.LogDebug($"    Add file reference {fileReference}");
            }

            foreach (var reference in metadataReferences)
            {
                _workspace.AddMetadataReference(state.Id, reference);
            }

            foreach (var reference in fileReferencesToRemove)
            {
                var toRemove = state.FileMetadataReferences[reference];
                state.FileMetadataReferences.Remove(reference);
                _workspace.RemoveMetadataReference(state.Id, toRemove);
                _logger.LogDebug($"    Remove file reference {reference}");
            }

            if (metadataReferences.Count != 0 || fileReferencesToRemove.Count != 0)
            {
                _logger.LogInformation($"    Added {metadataReferences.Count} and removed {fileReferencesToRemove.Count} file references");
            }
        }

        private void UpdateProjectReferences(ProjectState state, IEnumerable<ProjectDescription> projectReferencesLatest)
        {
            var projectReferences = new List<ProjectReference>();
            var projectReferencesToRemove = state.ProjectReferences.Keys.ToHashSet();

            foreach (var description in projectReferencesLatest)
            {
                var projectPath = Path.GetDirectoryName(description.Path);
                if (projectReferencesToRemove.Remove(projectPath))
                {
                    continue;
                }

                var referencedProjectState = _projectStates.Find(projectPath, description.Framework);
                projectReferences.Add(new ProjectReference(referencedProjectState.Id));
                state.ProjectReferences[projectPath] = referencedProjectState.Id;

                _logger.LogDebug($"    Add project reference {description.Path}");
            }

            foreach (var reference in projectReferences)
            {
                _workspace.AddProjectReference(state.Id, reference);
            }

            foreach (var reference in projectReferencesToRemove)
            {
                var toRemove = state.ProjectReferences[reference];
                state.ProjectReferences.Remove(reference);
                _workspace.RemoveProjectReference(state.Id, new ProjectReference(toRemove));

                _logger.LogDebug($"    Remove project reference {reference}");
            }

            if (projectReferences.Count != 0 || projectReferencesToRemove.Count != 0)
            {
                _logger.LogInformation($"    Added {projectReferences.Count} and removed {projectReferencesToRemove.Count} project references");
            }
        }

        private void UpdateUnresolvedDependencies(ProjectState state, bool allowRestore)
        {
            var libraryManager = state.ProjectContext.LibraryManager;
            var allDiagnostics = libraryManager.GetAllDiagnostics();
            var unresolved = libraryManager.GetLibraries().Where(dep => !dep.Resolved);
            var needRestore = allDiagnostics.Any(diag => diag.ErrorCode == ErrorCodes.NU1006) || unresolved.Any();

            if (needRestore)
            {
                if (allowRestore && _enableRestorePackages)
                {
                    _dotNetCliService.Restore(state.ProjectContext.ProjectDirectory, onFailure: () =>
                    {
                        _eventEmitter.Emit(EventTypes.UnresolvedDependencies, new UnresolvedDependenciesMessage()
                        {
                            FileName = state.ProjectContext.ProjectFile.ProjectFilePath,
                            UnresolvedDependencies = unresolved.Select(d => new PackageDependency { Name = d.Identity.Name, Version = d.Identity.Version?.ToString() })
                        });
                    });
                }
                else
                {
                    _eventEmitter.Emit(EventTypes.UnresolvedDependencies, new UnresolvedDependenciesMessage()
                    {
                        FileName = state.ProjectContext.ProjectFile.ProjectFilePath,
                        UnresolvedDependencies = unresolved.Select(d => new PackageDependency { Name = d.Identity.Name, Version = d.Identity.Version?.ToString() })
                    });
                }
            }
        }

        private void UpdateCompilationOption(ProjectState state)
        {
            var context = state.ProjectContext;
            var project = context.ProjectFile;
            var option = project.GetCompilerOptions(context.TargetFramework, CompilationConfiguration);
            var outputKind = option.EmitEntryPoint.GetValueOrDefault() ? OutputKind.ConsoleApplication :
                                                                         OutputKind.DynamicallyLinkedLibrary;

            var generalDiagnosticOpt = (option.WarningsAsErrors ?? false) ? ReportDiagnostic.Error :
                                                                            ReportDiagnostic.Default;

            var optimize = (option.Optimize ?? false) ? OptimizationLevel.Release : OptimizationLevel.Debug;

            var suppressedDiagnostics = new Dictionary<string, ReportDiagnostic>
            {
                {"CS1701", ReportDiagnostic.Suppress},
                {"CS1702", ReportDiagnostic.Suppress},
                {"CS1705", ReportDiagnostic.Suppress},
            };

            if (option.SuppressWarnings != null && option.SuppressWarnings.Any())
            {
                foreach (var nowarn in option.SuppressWarnings)
                {
                    if (!suppressedDiagnostics.ContainsKey(nowarn))
                    {
                        suppressedDiagnostics.Add(nowarn, ReportDiagnostic.Suppress);
                    }
                }
            }

            var csharpOptions = new CSharpCompilationOptions(outputKind)
                .WithAllowUnsafe(option.AllowUnsafe ?? false)
                .WithPlatform(ParsePlatfrom(option.Platform))
                .WithGeneralDiagnosticOption(generalDiagnosticOpt)
                .WithOptimizationLevel(optimize)
                .WithSpecificDiagnosticOptions(suppressedDiagnostics)
                .WithConcurrentBuild(false); // TODO: actually just need to disable on mono

            if (!string.IsNullOrEmpty(option.KeyFile))
            {
                var cryptoKeyFile = Path.GetFullPath(Path.Combine(project.ProjectDirectory, option.KeyFile));
                if (File.Exists(cryptoKeyFile))
                {
                    var strongNameProvider = new DesktopStrongNameProvider(ImmutableArray.Create(project.ProjectDirectory));
                    csharpOptions = csharpOptions
                        .WithStrongNameProvider(strongNameProvider)
                        .WithCryptoPublicKey(SnkUtils.ExtractPublicKey(File.ReadAllBytes(cryptoKeyFile)));
                }
            }

            var parseOptions = new CSharpParseOptions(languageVersion: ParseLanguageVersion(option.LanguageVersion),
                                                      preprocessorSymbols: option.Defines);

            if (option.GenerateXmlDocumentation ?? false)
            {
                csharpOptions = csharpOptions.WithXmlReferenceResolver(XmlFileResolver.Default);
                parseOptions = parseOptions.WithDocumentationMode(DocumentationMode.Diagnose);
            }

            _workspace.SetCompilationOptions(state.Id, csharpOptions);
            _workspace.SetParseOptions(state.Id, parseOptions);
        }

        private void UpdateSourceFiles(ProjectState state, IEnumerable<string> sourceFiles)
        {
            sourceFiles = sourceFiles.Where(filename => Path.GetExtension(filename) == ".cs");

            var existingFiles = new HashSet<string>(state.DocumentReferences.Keys);

            var added = 0;
            var removed = 0;

            foreach (var sourceFile in sourceFiles)
            {
                if (existingFiles.Remove(sourceFile))
                {
                    continue;
                }

                var documentId = _workspace.AddDocument(state.Id, sourceFile);
                state.DocumentReferences[sourceFile] = documentId;

                _logger.LogDebug($"    Added document {sourceFile}.");
                added++;
            }

            foreach (var file in existingFiles)
            {
                _workspace.RemoveDocument(state.DocumentReferences[file]);
                state.DocumentReferences.Remove(file);
                _logger.LogDebug($"    Removed document {file}.");
                removed++;
            }

            if (added != 0 || removed != 0)
            {
                _logger.LogInformation($"    Added {added} and removed {removed} documents.");
            }
        }

        private static Platform ParsePlatfrom(string value)
        {
            Platform platform;
            if (!Enum.TryParse(value, ignoreCase: true, result: out platform))
            {
                platform = Platform.AnyCpu;
            }

            return platform;
        }

        private static LanguageVersion ParseLanguageVersion(string value)
        {
            LanguageVersion languageVersion;
            if (!Enum.TryParse(value, ignoreCase: true, result: out languageVersion))
            {
                languageVersion = LanguageVersion.Default;
            }

            return languageVersion;
        }
    }
}
