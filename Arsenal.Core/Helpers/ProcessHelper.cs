using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Arsenal.Helpers
{
    public static class ProcessHelper
    {
        private const string ExitEventName = "Global\\ArsenalApp-Exit";
        private const string ShowEventName = "Global\\ArsenalApp-Show";

        /// <summary>Marks a launch that intends to replace the running instance.</summary>
        public const string ReplaceArgument = "--replace";
        private static EventWaitHandle? exitEvent;
        private static EventWaitHandle? showEvent;
        private static long lastAdmin;
        public static event Action? ExitRequested;

        /// <summary>Raised when another launch asked this instance to come to the front.</summary>
        public static event Action? ShowRequested;

        private static EventWaitHandleSecurity AuthenticatedUserAccess()
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify,
                AccessControlType.Allow));
            return security;
        }

        /// <summary>
        /// Asks an already-running instance to show itself.
        /// </summary>
        /// <returns>
        /// True when one was found and signalled, meaning this process has nothing to do
        /// and should exit. False when this is the only instance.
        /// </returns>
        public static bool TryActivateExistingInstance()
        {
            try
            {
                using var handle = EventWaitHandleAcl.OpenExisting(ShowEventName,
                    EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify);
                handle.Set();
                Logger.WriteLine("Another instance is already running, bringing it to the front");
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (Exception ex)
            {
                // Better to start a second window than to exit and leave the user with
                // nothing when the handle exists but cannot be opened.
                Logger.WriteLine("Can't signal existing instance: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Publishes the handle <see cref="TryActivateExistingInstance"/> looks for, so
        /// later launches raise <see cref="ShowRequested"/> here instead of restarting.
        /// </summary>
        public static void ListenForActivation()
        {
            try
            {
                showEvent = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset,
                    ShowEventName, out _, AuthenticatedUserAccess());

                ThreadPool.RegisterWaitForSingleObject(showEvent,
                    (_, _) => ShowRequested?.Invoke(), null, Timeout.Infinite, false);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Can't publish activation handle: " + ex.Message);
            }
        }

        private static readonly Lazy<bool> _isSystem = new Lazy<bool>(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        public static bool IsRunningAsSystem() => _isSystem.Value;

        /// <summary>
        /// Takes over from any other running instance, stopping it. Only for launches
        /// that genuinely mean to replace it, such as relaunching elevated or finishing
        /// an update - an ordinary second launch should call
        /// <see cref="TryActivateExistingInstance"/> instead.
        /// </summary>
        /// <param name="takeOver">
        /// When false this only subscribes to the exit broadcast, so a later replacing
        /// launch can shut this instance down cleanly. When true it also broadcasts and
        /// then kills anything that ignored it.
        /// </param>
        public static void CheckAlreadyRunning(bool takeOver = true)
        {
            var sec = AuthenticatedUserAccess();

            bool created = false;
            try
            {
                exitEvent = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, ExitEventName, out created, sec);
            }
            catch
            {
                try { exitEvent = EventWaitHandle.OpenExisting(ExitEventName); }
                catch { }
            }

            if (!takeOver)
            {
                RegisterExitListener();
                return;
            }

            if (!created && exitEvent != null)
            {
                try
                {
                    exitEvent.Set();
                    exitEvent.Reset();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Broadcast exit failed: " + ex.Message);
                    exitEvent = null;
                }
            }

            using Process currentProcess = Process.GetCurrentProcess();
            Process[] processes = Process.GetProcessesByName(currentProcess.ProcessName);
            try
            {
                if (processes.Length > 1)
                {
                    var failed = new List<Process>();
                    foreach (Process process in processes)
                        if (process.Id != currentProcess.Id)
                        {
                            try
                            {
                                process.Kill();
                            }
                            catch (Exception ex)
                            {
                                Logger.WriteLine($"Can't kill PID {process.Id}: {ex.Message}");
                                failed.Add(process);
                            }
                        }

                    if (failed.Count > 0)
                    {
                        Thread.Sleep(2000);

                        foreach (var p in failed)
                        {
                            bool stillAlive;
                            try { stillAlive = !p.HasExited; }
                            catch { stillAlive = true; }

                            if (stillAlive)
                            {
                                MessageBox.Show("Arsenal is already running. Check system tray for an icon.", "App already running", MessageBoxButtons.OK);
                                Application.Exit();
                                return;
                            }
                        }
                    }
                }
            }
            finally
            {
                foreach (Process p in processes) p.Dispose();
            }

            RegisterExitListener();
        }

        private static void RegisterExitListener()
        {
            if (exitEvent is null) return;

            ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) =>
            {
                ExitRequested?.Invoke();
                Application.Exit();
            }, null, Timeout.Infinite, true);
        }

        public static bool IsUserAdministrator()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static bool RunAsAdmin(string? param = null, bool force = false)
        {

            if (Math.Abs(DateTimeOffset.Now.ToUnixTimeMilliseconds() - lastAdmin) < 2000) return false;
            lastAdmin = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            // Check if the current user is an administrator
            if (!IsUserAdministrator() || force)
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.UseShellExecute = true;
                startInfo.WorkingDirectory = Environment.CurrentDirectory;
                startInfo.FileName = Application.ExecutablePath;
                // --replace, because this instance is still alive as the new one starts.
                // Without it the new process would see us running, ask us to show
                // ourselves and exit, and the elevation would never happen.
                startInfo.Arguments = string.IsNullOrEmpty(param) ? ReplaceArgument : param + " " + ReplaceArgument;
                startInfo.Verb = "runas";
                try
                {
                    Process.Start(startInfo);
                    Application.Exit();
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.Message);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Runs one privileged helper action without replacing or closing the normal
        /// desktop instance. Used for focused jobs such as adding companion firewall
        /// rules, where the rest of Arsenal does not need an administrator token.
        /// </summary>
        public static Process? StartElevatedAction(string arguments)
        {
            try
            {
                return Process.Start(new ProcessStartInfo
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                    FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                    Arguments = arguments,
                    Verb = "runas"
                });
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
                return null;
            }
        }


        public static void KillByName(string name)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        process.Kill();
                        Logger.WriteLine($"Stopped: {process.ProcessName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteLine($"Failed to stop: {process.ProcessName} {ex.Message}");
                    }
                }
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }

        public static void KillSmartDisplayControl()
        {
            KillByName("ASUSSmartDisplayControl");
        }

        public static void KillByProcess(Process process)
        {
            try
            {
                process.Kill();
                Logger.WriteLine($"Stopped: {process.ProcessName}");
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Failed to stop: {process.ProcessName} {ex.Message}");
            }
        }

        public static void StopDisableService(string serviceName, string disable = "Disabled")
        {
            try
            {
                string script = $"Get-Service -Name \"{serviceName}\" | Stop-Service -Force -PassThru | Set-Service -StartupType {disable}";
                Logger.WriteLine(script);
                RunCMD("powershell", script);
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.ToString());
            }
        }

        public static void StartEnableService(string serviceName, bool automatic = true)
        {
            try
            {
                string script = $"Set-Service -Name \"{serviceName}\" -Status running" + (automatic? " -StartupType Automatic":"");
                Logger.WriteLine(script);
                RunCMD("powershell", script);
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.ToString());
            }
        }

        public static string RunCMD(string name, string args, string? directory = null, int timeoutMs = 0)
        {
            using var cmd = new Process();
            cmd.StartInfo.UseShellExecute = false;
            cmd.StartInfo.CreateNoWindow = true;
            cmd.StartInfo.RedirectStandardOutput = true;
            cmd.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            cmd.StartInfo.FileName = name;
            cmd.StartInfo.Arguments = args;
            if (directory != null) cmd.StartInfo.WorkingDirectory = directory;
            cmd.Start();

            var watch = Stopwatch.StartNew();
            string result;

            if (timeoutMs > 0)
            {
                var readTask = cmd.StandardOutput.ReadToEndAsync();
                if (!readTask.Wait(timeoutMs))
                {
                    try { cmd.Kill(entireProcessTree: true); } catch { }
                    watch.Stop();
                    Logger.WriteLine(name + " " + args);
                    Logger.WriteLine($"{watch.ElapsedMilliseconds} ms: TIMEOUT after {timeoutMs} ms");
                    return string.Empty;
                }
                result = readTask.Result.Replace(Environment.NewLine, " ").Trim(' ');
            }
            else
            {
                result = cmd.StandardOutput.ReadToEnd().Replace(Environment.NewLine, " ").Trim(' ');
            }

            watch.Stop();
            Logger.WriteLine(name + " " + args);
            Logger.WriteLine(watch.ElapsedMilliseconds + " ms: " + result);
            cmd.WaitForExit();

            return result;
        }

        public static void SetPriority(ProcessPriorityClass priorityClass = ProcessPriorityClass.Normal)
        {
            try
            {
                using (Process p = Process.GetCurrentProcess())
                    p.PriorityClass = priorityClass;
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.ToString());
            }
        }


    }
}
