﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using NuGet.Common;
using Task = System.Threading.Tasks.Task;

namespace NuGet.PackageManagement.VisualStudio
{
    /// <summary>
    /// Aggregates logging and UI services consumed by the <see cref="SolutionRestoreJob"/>.
    /// </summary>
    internal sealed class RestoreOperationLogger : ILogger, IDisposable
    {
        private static readonly string BuildWindowPaneGuid = VSConstants.BuildOutput.ToString("B");

        private readonly IServiceProvider _serviceProvider;
        private readonly ErrorListProvider _errorListProvider;
        private readonly EnvDTE.OutputWindowPane _outputWindowPane;
        private readonly Func<CancellationToken, Task<RestoreOperationProgressUI>> _progressFactory;
        private readonly CancellationTokenSource _externalCts;

        private bool _cancelled;

        // The value of the "MSBuild project build output verbosity" setting
        // of VS. From 0 (quiet) to 4 (Diagnostic).
        public int OutputVerbosity { get; }

        private RestoreOperationLogger(
            IServiceProvider serviceProvider,
            ErrorListProvider errorListProvider,
            EnvDTE.OutputWindowPane outputWindowPane,
            Func<CancellationToken, Task<RestoreOperationProgressUI>> progressFactory,
            int outputVerbosity,
            CancellationTokenSource cts)
        {
            _serviceProvider = serviceProvider;
            _errorListProvider = errorListProvider;
            _progressFactory = progressFactory;
            _outputWindowPane = outputWindowPane;

            _externalCts = cts;
            _externalCts.Token.Register(() => _cancelled = true);

            OutputVerbosity = outputVerbosity;
        }

        public static async Task<RestoreOperationLogger> StartAsync(
            IServiceProvider serviceProvider,
            ErrorListProvider errorListProvider,
            bool blockingUi,
            CancellationTokenSource cts)
        {
            if (serviceProvider == null)
            {
                throw new ArgumentNullException(nameof(serviceProvider));
            }

            if (errorListProvider == null)
            {
                throw new ArgumentNullException(nameof(errorListProvider));
            }

            if (cts == null)
            {
                throw new ArgumentNullException(nameof(cts));
            }

            var msbuildOutputVerbosity = await GetMSBuildOutputVerbositySettingAsync(serviceProvider);

            var buildOutputPane = await GetBuildOutputPaneAsync(serviceProvider);

            Func<CancellationToken, Task<RestoreOperationProgressUI>> progressFactory;
            if (blockingUi)
            {
                progressFactory = t => WaitDialogProgress.StartAsync(serviceProvider, t);
            }
            else
            {
                progressFactory = t => StatusBarProgress.StartAsync(serviceProvider, t);
            }

            await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                errorListProvider.Tasks.Clear();
            });

            return new RestoreOperationLogger(
                serviceProvider, 
                errorListProvider,
                buildOutputPane,
                progressFactory, 
                msbuildOutputVerbosity,
                cts);
        }

        public void Dispose()
        {
        }

        public void LogDebug(string data)
        {
            LogToVS(VerbosityLevel.Diagnostic, data);
        }

        public void LogVerbose(string data)
        {
            LogToVS(VerbosityLevel.Detailed, data);
        }

        public void LogInformation(string data)
        {
            LogToVS(VerbosityLevel.Normal, data);
        }

        public void LogMinimal(string data)
        {
            LogInformation(data);
        }

        public void LogWarning(string data)
        {
            LogToVS(VerbosityLevel.Minimal, data);
        }

        public void LogError(string data)
        {
            LogToVS(VerbosityLevel.Quiet, data);
        }

        public void LogInformationSummary(string data)
        {
            // Treat Summary as Debug
            LogDebug(data);
        }

        public void LogErrorSummary(string data)
        {
            // Treat Summary as Debug
            LogDebug(data);
        }

        private void LogToVS(VerbosityLevel verbosityLevel, string message)
        {
            if (_cancelled)
            {
                // If an operation is canceled, don't log anything, simply return
                // And, show a single message gets shown in the summary that package restore has been canceled
                // Do not report it as separate errors
                return;
            }

            // If the verbosity level of message is worse than VerbosityLevel.Normal, that is,
            // VerbosityLevel.Detailed or VerbosityLevel.Diagnostic, AND,
            // _msBuildOutputVerbosity is lesser than verbosityLevel; do nothing
            if (verbosityLevel > VerbosityLevel.Normal && OutputVerbosity < (int)verbosityLevel)
            {
                return;
            }

            Do((_, progress) =>
            {
                // Only show messages with VerbosityLevel.Normal. That is, info messages only.
                // Do not show errors, warnings, verbose or debug messages on the progress dialog
                // Avoid showing indented messages, these are typically not useful for the progress dialog since
                // they are missing the context of the parent text above it
                if (verbosityLevel == VerbosityLevel.Normal &&
                    message.Length == message.TrimStart().Length)
                {
                    progress?.ReportProgress(message);
                }

                // Write to the output window. Based on _msBuildOutputVerbosity, the message may or may not
                // get shown on the output window. Default is VerbosityLevel.Minimal
                WriteLine(verbosityLevel, message);

                // VerbosityLevel.Quiet corresponds to ILogger.LogError, and,
                // VerbosityLevel.Minimal corresponds to ILogger.LogWarning
                // In these 2 cases, we add an error or warning to the error list window
                if (verbosityLevel == VerbosityLevel.Quiet ||
                    verbosityLevel == VerbosityLevel.Minimal)
                {
                    MessageHelper.ShowError(
                        _errorListProvider,
                        verbosityLevel == VerbosityLevel.Quiet ? TaskErrorCategory.Error : TaskErrorCategory.Warning,
                        TaskPriority.High,
                        message,
                        hierarchyItem: null);
                }
            });
        }

        /// <summary>
        /// Outputs a message to the debug output pane, if the VS MSBuildOutputVerbosity
        /// setting value is greater than or equal to the given verbosity. So if verbosity is 0,
        /// it means the message is always written to the output pane.
        /// </summary>
        /// <param name="verbosity">The verbosity level.</param>
        /// <param name="format">The format string.</param>
        /// <param name="args">An array of objects to write using format. </param>
        public void WriteLine(VerbosityLevel verbosity, string format, params object[] args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (OutputVerbosity >= (int)verbosity && _outputWindowPane != null)
            {
                _outputWindowPane.OutputString(string.Format(CultureInfo.CurrentCulture, format, args));
                _outputWindowPane.OutputString(Environment.NewLine);
            }
        }

        private static async Task<EnvDTE.OutputWindowPane> GetBuildOutputPaneAsync(IServiceProvider serviceProvider)
        {
            return await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                // Switch to main thread to use DTE
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte2 = (DTE2)serviceProvider.GetDTE();
                var pane = dte2.ToolWindows.OutputWindow
                    .OutputWindowPanes
                    .Cast<EnvDTE.OutputWindowPane>()
                    .FirstOrDefault(p => StringComparer.OrdinalIgnoreCase.Equals(p.Guid, BuildWindowPaneGuid));
                return pane;
            });
        }

        public Task LogExceptionAsync(Exception ex, bool logError)
        {
            return DoAsync((_, __) =>
            {
                string message;
                if (OutputVerbosity < 3)
                {
                    message = string.Format(CultureInfo.CurrentCulture,
                        Strings.ErrorOccurredRestoringPackages,
                        ex.Message);
                }
                else
                {
                    // output exception detail when _msBuildOutputVerbosity is >= Detailed.
                    message = string.Format(CultureInfo.CurrentCulture, Strings.ErrorOccurredRestoringPackages, ex);
                }

                if (logError)
                {
                    // Write to the error window and console
                    LogError(message);
                }
                else
                {
                    // Write to console
                    WriteLine(VerbosityLevel.Quiet, message);
                }

                ExceptionHelper.WriteToActivityLog(ex);
            });
        }

        public void ShowError(string errorText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            MessageHelper.ShowError(
                _errorListProvider,
                TaskErrorCategory.Error,
                TaskPriority.High,
                errorText,
                hierarchyItem: null);
        }

        /// <summary>
        /// Helper async method to run batch of logging call on the main UI thread.
        /// </summary>
        /// <param name="action">Sync callback invoking logger.</param>
        /// <returns>An awaitable task.</returns>
        public async Task DoAsync(Action<RestoreOperationLogger, RestoreOperationProgressUI> action)
        {
            // capture current progress from the current execution context
            var progress = RestoreOperationProgressUI.Current;

            await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                action(this, progress);
            });
        }

        /// <summary>
        /// Helper synchronous method to run batch of logging call on the main UI thread.
        /// </summary>
        /// <param name="action">Sync callback invoking logger.</param>
        public void Do(Action<RestoreOperationLogger, RestoreOperationProgressUI> action)
        {
            // capture current progress from the current execution context
            var progress = RestoreOperationProgressUI.Current;

            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                action(this, progress);
            });
        }

        public async Task RunWithProgressAsync(
            Func<RestoreOperationLogger, RestoreOperationProgressUI, CancellationToken, Task> asyncRunMethod,
            CancellationToken token)
        {
            using (var progress = await _progressFactory(token))
            using (var ctr = progress.RegisterUserCancellationAction(() => _externalCts.Cancel()))
            {
                // Save the progress instance in the current execution context.
                // The value won't be available outside of this async method.
                RestoreOperationProgressUI.Current = progress;
                await asyncRunMethod(this, progress, token);
            }
        }

        public async Task RunWithProgressAsync(
            Action<RestoreOperationLogger, RestoreOperationProgressUI, CancellationToken> runAction,
            CancellationToken token)
        {
            using (var progress = await _progressFactory(token))
            using (var ctr = progress.RegisterUserCancellationAction(() => _externalCts.Cancel()))
            {
                // Save the progress instance in the current execution context.
                // The value won't be available outside of this async method.
                RestoreOperationProgressUI.Current = progress;
                runAction(this, progress, token);
            }
        }

        /// <summary>
        /// Returns the value of the VisualStudio MSBuildOutputVerbosity setting.
        /// </summary>
        /// <param name="dte">The VisualStudio instance.</param>
        /// <remarks>
        /// 0 is Quiet, while 4 is diagnostic.
        /// </remarks>
        private static async Task<int> GetMSBuildOutputVerbositySettingAsync(IServiceProvider serviceProvider)
        {
            return await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                // Switch to main thread to use DTE
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = serviceProvider.GetDTE();

                var properties = dte.get_Properties("Environment", "ProjectsAndSolution");
                var value = properties.Item("MSBuildOutputVerbosity").Value;
                if (value is int)
                {
                    return (int)value;
                }
                return 0;
            });
        }

        private class WaitDialogProgress : RestoreOperationProgressUI
        {
            private readonly ThreadedWaitDialogHelper.Session _session;

            private WaitDialogProgress(ThreadedWaitDialogHelper.Session session)
            {
                _session = session;
                UserCancellationToken = _session.UserCancellationToken;
            }

            public static async Task<RestoreOperationProgressUI> StartAsync(IServiceProvider serviceProvider, CancellationToken token)
            {
                return await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    var waitDialogFactory = serviceProvider.GetService<
                        SVsThreadedWaitDialogFactory, IVsThreadedWaitDialogFactory>();

                    var session = waitDialogFactory.StartWaitDialog(
                        waitCaption: Strings.DialogTitle,
                        initialProgress: new ThreadedWaitDialogProgressData(
                            Strings.RestoringPackages,
                            progressText: string.Empty,
                            statusBarText: string.Empty,
                            isCancelable: true,
                            currentStep: 0,
                            totalSteps: 0));

                    return new WaitDialogProgress(session);
                });
            }

            public override void Dispose()
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _session.Dispose();
                });
            }

            public override void ReportProgress(
                string progressMessage,
                uint currentStep,
                uint totalSteps)
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                // When both currentStep and totalSteps are 0, we get a marquee on the dialog
                var progressData = new ThreadedWaitDialogProgressData(
                        progressMessage,
                        progressText: string.Empty,
                        statusBarText: string.Empty,
                        isCancelable: true,
                        currentStep: (int)currentStep,
                        totalSteps: (int)totalSteps);

                _session.Progress.Report(progressData);
            }
        }

        private class StatusBarProgress : RestoreOperationProgressUI
        {
            private static object icon = (short)Constants.SBAI_General;
            private readonly IVsStatusbar StatusBar;
            private uint cookie = 0;

            private StatusBarProgress(IVsStatusbar statusBar)
            {
                StatusBar = statusBar;
            }

            public static async Task<RestoreOperationProgressUI> StartAsync(
                IServiceProvider serviceProvider,
                CancellationToken token)
            {
                return await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    var statusBar = serviceProvider.GetService<SVsStatusbar, IVsStatusbar>();

                    // Make sure the status bar is not frozen
                    int frozen;
                    statusBar.IsFrozen(out frozen);

                    if (frozen != 0)
                    {
                        statusBar.FreezeOutput(0);
                    }

                    statusBar.Animation(1, ref icon);

                    RestoreOperationProgressUI progress = new StatusBarProgress(statusBar);
                    progress.ReportProgress(Strings.RestoringPackages);

                    return progress;
                });
            }

            public override void Dispose()
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    StatusBar.Animation(0, ref icon);
                    StatusBar.Progress(ref cookie, 0, "", 0, 0);
                    StatusBar.FreezeOutput(0);
                    StatusBar.Clear();
                });
            }

            public override void ReportProgress(
                string progressMessage,
                uint currentStep,
                uint totalSteps)
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                // Make sure the status bar is not frozen
                int frozen;
                StatusBar.IsFrozen(out frozen);

                if (frozen != 0)
                {
                    StatusBar.FreezeOutput(0);
                }

                StatusBar.SetText(progressMessage);

                if (totalSteps != 0)
                {
                    StatusBar.Progress(ref cookie, 1, "", currentStep, totalSteps);
                }
            }
        }
    }
}
