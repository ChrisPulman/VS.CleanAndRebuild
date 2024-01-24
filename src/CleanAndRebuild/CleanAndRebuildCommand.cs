// Copyright (c) Chris Pulman. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CPCleanAndRebuild
{
    /// <summary>
    /// Command handler.
    /// </summary>
    internal sealed class CleanAndRebuildCommand
    {
        /// <summary>
        /// Command ID for CleanAndRebuild.
        /// </summary>
        public const int CleanAndRebuildCommandId = 0x0100;

        /// <summary>
        /// Command ID for CleanOnly.
        /// </summary>
        public const int CleanOnlyCommandCommandId = 0x0101;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CleanAndRebuildCommandGuid = new("58cab930-ec55-4b8b-876a-c6208cc246c4");

        /// <summary>
        /// The clean only command unique identifier.
        /// </summary>
        public static readonly Guid CleanOnlyCommandGuid = new("58cab930-ec55-4b8b-876a-c6208cc246c5");

        private static IVsOutputWindowPane _vsOutputWindowPane;
        private readonly DTE2 _dte;
        private readonly Options _options;
        private readonly Package _package;
        private IVsStatusbar _bar;

        /// <summary>
        /// Initializes a new instance of the <see cref="CleanAndRebuildCommand" /> class.
        /// Adds our command handlers for menu (commands must exist in the command table file).
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="dte2">The dte2.</param>
        /// <param name="options">The options.</param>
        /// <exception cref="System.ArgumentNullException">package.</exception>
        private CleanAndRebuildCommand(Package package, DTE2 dte2, Options options)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _dte = dte2;
            _options = options;

            if (ServiceProvider.GetService(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                commandService.AddCommand(CreateCommand(CleanAndRebuildCommandGuid, CleanAndRebuildCommandId, CleanAndRebuild));
                commandService.AddCommand(CreateCommand(CleanOnlyCommandGuid, CleanOnlyCommandCommandId, CleanOnly));

                var outWindow = (IVsOutputWindow)Package.GetGlobalService(typeof(SVsOutputWindow));
                var generalPaneGuid = VSConstants.GUID_BuildOutputWindowPane;
                outWindow.GetPane(ref generalPaneGuid, out _vsOutputWindowPane);
                _vsOutputWindowPane.Activate(); // Brings this pane into view
            }
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        /// <value>
        /// The instance.
        /// </value>
        public static CleanAndRebuildCommand Instance { get; private set; }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        /// <value>
        /// The service provider.
        /// </value>
        private IServiceProvider ServiceProvider => _package;

        /// <summary>
        /// Gets the status bar.
        /// </summary>
        /// <value>
        /// The status bar.
        /// </value>
        private IVsStatusbar StatusBar
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _bar ??= ServiceProvider.GetService(typeof(SVsStatusbar)) as IVsStatusbar;
            }
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await package.GetServiceAsync<DTE, DTE2>();
            var options = (Options)package.GetDialogPage(typeof(Options));
            Instance = new CleanAndRebuildCommand(package, dte, options);
        }

        private static Project[] GetActiveProjects(DTE2 dte)
        {
            try
            {
                if (dte.ActiveSolutionProjects is Array activeSolutionProjects && activeSolutionProjects.Length > 0)
                {
                    return activeSolutionProjects.Cast<Project>().ToArray();
                }
            }
            catch (Exception ex)
            {
                Debug.Write(ex.Message);
            }

            return null;
        }

        private static bool ProjectFullNameNotEmpty(Project p)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return !string.IsNullOrEmpty(p.FullName);
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<Project> GetChildProjects(Project parent)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // Unloaded
                if (parent.Kind != ProjectKinds.vsProjectKindSolutionFolder && parent.Collection == null)
                {
                    return Enumerable.Empty<Project>();
                }

                if (!string.IsNullOrEmpty(parent.FullName))
                {
                    return Enumerable.Repeat(parent, 1);
                }
            }
            catch (COMException)
            {
                return Enumerable.Empty<Project>();
            }

            return parent.ProjectItems
                .Cast<ProjectItem>()
                .Where(p =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    return p.SubProject != null;
                })
                .SelectMany(p =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    return GetChildProjects(p.SubProject);
                });
        }

        private static string GetProjectRootFolder(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(project.FullName))
            {
                return null;
            }

            string fullPath;

            try
            {
                fullPath = project.Properties.Item("FullPath").Value as string;
            }
            catch (ArgumentException)
            {
                try
                {
                    // MFC projects don't have FullPath, and there seems to be no way to query existence
                    fullPath = project.Properties.Item("ProjectDirectory").Value as string;
                }
                catch (ArgumentException)
                {
                    // Installer projects have a ProjectPath.
                    fullPath = project.Properties.Item("ProjectPath").Value as string;
                }
            }

            if (string.IsNullOrEmpty(fullPath))
            {
                return File.Exists(project.FullName) ? Path.GetDirectoryName(project.FullName) : null;
            }

            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }

            return File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : null;
        }

        private Project[] GetProjects()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var projects = GetActiveProjects(_dte) ?? _dte.Solution.Projects.Cast<Project>().ToArray();
            return
            [
                .. projects
                .SelectMany(GetChildProjects)
                .Union(projects)
                .Where(ProjectFullNameNotEmpty)
                .OrderBy(x =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    return x.UniqueName;
                }),
            ];
        }

        private OleMenuCommand CreateCommand(Guid menuGroup, int commandId, EventHandler invokeHandler) =>
            new(invokeHandler, new CommandID(menuGroup, commandId));

        /// <summary>
        /// Cleans and rebuilds.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        private void CleanAndRebuild(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var sw = new Stopwatch();
            var solutionProjects = GetProjects();
            _vsOutputWindowPane.Clear();
            WriteToOutput($"Starting... Projects to clean: {solutionProjects.Length}");
            uint cookie = 0;
            StatusBar.Progress(ref cookie, 1, string.Empty, 0, (uint)solutionProjects.Length);
            sw.Start();
            for (uint index = 0; index < solutionProjects.Length; index++)
            {
                var project = solutionProjects[index];
                var projectRootPath = GetProjectRootFolder(project);
                var message = $"Cleaning {project.UniqueName}";
                WriteToOutput(message);
                StatusBar.Progress(ref cookie, 1, string.Empty, index, (uint)solutionProjects.Length);
                StatusBar.SetText(message);
                CleanDirectory(projectRootPath);
            }

            var success = true;

            // Rebuild the solution
            if (ServiceProvider.GetService(typeof(SVsSolutionBuildManager)) is IVsSolutionBuildManager2 buildManager)
            {
                StatusBar.SetText("Cleaned bin and obj folders, Rebuilding Solution");

                if (ErrorHandler.Failed(buildManager.StartSimpleUpdateSolutionConfiguration(
                    (uint)(VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_FORCE_UPDATE | VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_BUILD),
                    (uint)VSSOLNBUILDQUERYRESULTS.VSSBQR_OUTOFDATE_QUERY_YES,
                    0/*false*/)))
                {
                    // handle the error
                    success = false;
                }
            }
            else
            {
                success = false;
            }

            sw.Stop();
            WriteToOutput($@"Finished. Elapsed: {sw.Elapsed:mm\:ss\.ffff}");

            // Clear the progress bar.
            StatusBar.Progress(ref cookie, 0, string.Empty, 0, 0);
            StatusBar.FreezeOutput(0);
            if (success)
            {
                StatusBar.SetText("Cleaned bin and obj folders and Rebuilt Solution");
                WriteToOutput("Cleaned bin and obj folders and Rebuilt Solution");
            }
            else
            {
                StatusBar.SetText("Cleaned bin and obj folders. Unable to Rebuild the Solution.");
                WriteToOutput("Cleaned bin and obj folders. Unable to Rebuild the Solution.");
            }
        }

        /// <summary>
        /// Cleans and rebuilds.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        private void CleanOnly(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var sw = new Stopwatch();
            var solutionProjects = GetProjects();
            _vsOutputWindowPane.Clear();
            WriteToOutput($"Starting... Projects to clean: {solutionProjects.Length}");
            uint cookie = 0;
            StatusBar.Progress(ref cookie, 1, string.Empty, 0, (uint)solutionProjects.Length);
            sw.Start();
            for (uint index = 0; index < solutionProjects.Length; index++)
            {
                var project = solutionProjects[index];
                var projectRootPath = GetProjectRootFolder(project);
                var message = $"Cleaning {project.UniqueName}";
                WriteToOutput(message);
                StatusBar.Progress(ref cookie, 1, string.Empty, index, (uint)solutionProjects.Length);
                StatusBar.SetText(message);
                CleanDirectory(projectRootPath);
            }

            sw.Stop();
            WriteToOutput($@"Finished. Elapsed: {sw.Elapsed:mm\:ss\.ffff}");

            // Clear the progress bar.
            StatusBar.Progress(ref cookie, 0, string.Empty, 0, 0);
            StatusBar.FreezeOutput(0);
            StatusBar.SetText("Cleaned bin and obj folders.");
            WriteToOutput("Cleaned bin and obj folders.");
        }

        private void CleanDirectory(string directoryPath)
        {
            if (directoryPath == null || _options.TargetSubdirectories == null || _options.TargetSubdirectories.Length == 0)
            {
                return;
            }

            try
            {
                foreach (var di in _options.TargetSubdirectories
                    .Select(x => Path.Combine(directoryPath, x))
                    .Where(Directory.Exists)
                    .Select(x => new DirectoryInfo(x)))
                {
                    foreach (var file in di.EnumerateFiles())
                    {
                        file.Delete();
                    }

                    foreach (var dir in di.EnumerateDirectories())
                    {
                        dir.Delete(true);
                    }
                }
            }
            catch (Exception e)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                WriteToOutput($"Error while cleaning directory {directoryPath}: {e.Message}");
                Debug.WriteLine(e);
            }
        }

        private void WriteToOutput(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _vsOutputWindowPane.OutputStringThreadSafe($"{DateTime.Now:HH:mm:ss.ffff}: {message}{Environment.NewLine}");
        }
    }
}
