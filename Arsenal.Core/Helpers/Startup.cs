using Arsenal.Helpers;
using Microsoft.Win32.TaskScheduler;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;

public class Startup
{

    static string taskName = "Arsenal";
    static string chargeTaskName = taskName + "Charge";
    static string strExeFilePath = Application.ExecutablePath.Trim();

    /// <summary>
    /// The current user's SID, or an empty string where there is not one.
    /// </summary>
    /// <remarks>
    /// <c>WindowsIdentity.GetCurrent().User</c> is nullable, and these are static field
    /// initialisers: a null there does not throw a NullReferenceException somebody can
    /// catch, it throws TypeInitializationException and makes the whole Startup class
    /// permanently unusable for the life of the process. Every scheduled-task operation
    /// then fails, including the ones that only wanted the non-user task names.
    /// </remarks>
    static string CurrentUserSid
    {
        get
        {
            try { return WindowsIdentity.GetCurrent().User?.Value ?? ""; }
            catch (Exception e) { Logger.WriteLine("Could not read the current user SID: " + e.Message); return ""; }
        }
    }

    static string userTaskName = taskName + "_" + CurrentUserSid;
    static string legacyTaskName = string.Concat("G", "Helper");
    static string legacyChargeTaskName = legacyTaskName + "Charge";
    static string legacyUserTaskName = legacyTaskName + "_" + CurrentUserSid;

    static Microsoft.Win32.TaskScheduler.Task? GetUserTask(TaskService taskService)
    {
        try
        {
            foreach (string candidate in new[] { userTaskName, legacyUserTaskName, taskName, legacyTaskName })
            {
                var task = taskService.GetTask(candidate);
                if (task is null) continue;
                if (candidate == userTaskName || candidate == legacyUserTaskName) return task;

                string owner = task.Definition.Principal.UserId ?? "";
                string sid = CurrentUserSid;
                if ((sid.Length > 0 && owner == sid) ||
                    string.Equals(owner.Split('\\').Last(), Environment.UserName, StringComparison.OrdinalIgnoreCase))
                    return task;
            }
        }
        catch (Exception e)
        {
            Logger.WriteLine("Can't read startup task: " + e.Message);
        }

        return null;
    }

    public static bool IsScheduled()
    {
        try
        {
            using (TaskService taskService = new TaskService())
                return GetUserTask(taskService) != null;
        }
        catch (Exception e)
        {
            Logger.WriteLine("Can't check startup task status: " + e.Message);
            return false;
        }
    }

    public static void ReScheduleAdmin()
    {
        if (ProcessHelper.IsUserAdministrator() && IsScheduled())
        {
            UnSchedule();
            Schedule();
        }
    }

    /// <summary>
    /// Whether the process running this code is Arsenal itself.
    /// </summary>
    /// <remarks>
    /// Arsenal.Core is linked by the smoke harnesses in tools/ as well as by the
    /// application, and they run the same deferred startup path. Everything else here
    /// is harmless from a harness; rescheduling is not, because it points the user's
    /// Windows sign-in task at whatever executable happens to be running. A smoke run
    /// did exactly that: the task that starts Arsenal at sign-in was left pointing at
    /// ThemeSmoke.exe, which would have started a test harness instead of the
    /// application at the next sign-in.
    /// </remarks>
    private static bool IsArsenalItself()
    {
        try
        {
            return string.Equals(
                Path.GetFileNameWithoutExtension(strExeFilePath),
                "Arsenal",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            Logger.WriteLine("Could not identify the running executable: " + e.Message);
            return false;
        }
    }

    public static void StartupCheck()
    {
        // A harness may run this path. It may not rewrite where Windows starts Arsenal.
        if (!IsArsenalItself())
        {
            Logger.WriteLine("Startup task left alone: " + strExeFilePath + " is not Arsenal.");
            return;
        }

        using (TaskService taskService = new TaskService())
        {
            var task = GetUserTask(taskService);
            if (task != null)
            {
                try
                {
                    string action = task.Definition.Actions.FirstOrDefault()!.ToString().Trim();
                    bool needsReschedule = task.Name == legacyTaskName || task.Name == legacyUserTaskName;

                    if (!strExeFilePath.Equals(action, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!File.Exists(action))
                        {
                            Logger.WriteLine("Startup file doesn't exist: " + action);
                            needsReschedule = true;
                        }
                        else
                        {
                            try
                            {
                                // Both of these are nullable, and the version comparison
                                // below only exists to decide whether the scheduled task
                                // points at a stale executable. An unreadable version is
                                // not a reason to throw out of the whole startup check -
                                // it just means the question cannot be answered, so the
                                // task is left as it is.
                                var currentVer = Assembly.GetEntryAssembly()?.GetName().Version;
                                string? actionVersion = FileVersionInfo.GetVersionInfo(action).FileVersion;

                                if (currentVer is null || string.IsNullOrEmpty(actionVersion))
                                {
                                    // Neither is guaranteed to be readable. This comparison
                                    // only decides whether the scheduled task points at a
                                    // stale executable, so an unanswerable question leaves
                                    // the task alone rather than rescheduling on a guess.
                                    Logger.WriteLine("Can't compare assembly versions: version unavailable");
                                }
                                else
                                {
                                    var fv = actionVersion.Split('.');
                                    var scheduledVer = new Version(
                                        int.Parse(fv[0]),
                                        fv.Length > 1 ? int.Parse(fv[1]) : 0,
                                        fv.Length > 2 ? int.Parse(fv[2]) : 0,
                                        fv.Length > 3 ? int.Parse(fv[3]) : 0
                                    );
                                    if (currentVer > scheduledVer)
                                    {
                                        Logger.WriteLine($"Startup file is older {scheduledVer}, current is {currentVer}");
                                        needsReschedule = true;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.WriteLine("Can't compare assembly versions: " + ex.Message);
                            }
                        }
                    }

                    TaskRunLevel expectedRunLevel = AppConfig.Is("run_as_admin")
                        ? TaskRunLevel.Highest
                        : TaskRunLevel.LUA;
                    if (task.Definition.Principal.RunLevel != expectedRunLevel)
                    {
                        Logger.WriteLine($"Startup task run level changed to {expectedRunLevel}, rescheduling it");
                        needsReschedule = true;
                    }

                    if (needsReschedule)
                    {
                        if (!ProcessHelper.IsUserAdministrator() &&
                            (task.Definition.Principal.RunLevel == TaskRunLevel.Highest || expectedRunLevel == TaskRunLevel.Highest))
                        {
                            ProcessHelper.RunAsAdmin();
                            return;
                        }
                        Logger.WriteLine("Rescheduling to: " + strExeFilePath);
                        UnSchedule();
                        Schedule();
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Can't check startup task: {ex.Message}");
                }

                if (ProcessHelper.IsUserAdministrator())
                {
                    // Both directions, every start: register the boot task when it is
                    // missing, and take it away again once the executable it names stops
                    // being somewhere only administrators can write.
                    if (taskService.RootFolder.AllTasks.FirstOrDefault(t => t.Name == chargeTaskName) == null)
                        ScheduleCharge();
                    else
                        DropChargeTaskIfUnsafe(taskService);
                }

            }
        }
    }

    public static void UnscheduleCharge()
    {
        using (TaskService taskService = new TaskService())
        {
            try
            {
                taskService.RootFolder.DeleteTask(chargeTaskName, false);
                taskService.RootFolder.DeleteTask(legacyChargeTaskName, false);
            }
            catch (Exception e)
            {
                Logger.WriteLine("Can't remove charge limit task: " + e.Message);
            }
        }
    }

    /// <summary>
    /// The executable a registered task will actually run.
    /// </summary>
    /// <remarks>
    /// Read from the task rather than from <see cref="strExeFilePath"/>, because the two
    /// differ exactly when it matters: a task registered while Arsenal lived in Program
    /// Files still points there after the executable has been moved somewhere writable,
    /// and it is the registered path that Windows will run as SYSTEM.
    /// </remarks>
    static string? RegisteredExecutable(Microsoft.Win32.TaskScheduler.Task task)
    {
        try { return (task.Definition.Actions.FirstOrDefault() as ExecAction)?.Path?.Trim().Trim('"'); }
        catch (Exception e) { Logger.WriteLine("Can't read the task's executable: " + e.Message); return null; }
    }

    /// <summary>
    /// Removes a charge task that would run an executable a standard user can replace.
    /// </summary>
    static void DropChargeTaskIfUnsafe(TaskService taskService)
    {
        var charge = taskService.RootFolder.AllTasks.FirstOrDefault(t => t.Name == chargeTaskName);
        if (charge is null) return;

        string? registered = RegisteredExecutable(charge);
        if (PathSecurity.IsProtectedFile(registered)) return;

        Logger.WriteLine($"Removing the charge limit task: it runs {registered} as SYSTEM, and that file is not in an administrator-only location");
        UnscheduleCharge();
    }

    public static void ScheduleCharge()
    {

        if (strExeFilePath is null) return;

        // This task runs as SYSTEM, at boot, with nobody watching, so it is worth no more
        // than the file it points at. Arsenal is a portable executable and normally sits
        // in Downloads or a folder made at the root of a drive - both writable by the
        // user, and so by anything running as the user. Registering it there would let
        // whoever can replace that file run as SYSTEM on the next boot.
        //
        // The charge limit itself is not lost: Arsenal applies it when it starts at
        // logon. Only the boot-time task, which is what needs SYSTEM, is refused.
        if (!PathSecurity.IsProtectedFile(strExeFilePath))
        {
            Logger.WriteLine($"Charge limit task refused: {strExeFilePath} is in a location a standard user can write to. Move Arsenal to Program Files to schedule it at boot.");
            UnscheduleCharge();
            return;
        }

        using (TaskDefinition td = TaskService.Instance.NewTask())
        {
            td.RegistrationInfo.Description = "Arsenal Charge Limit";
            td.Triggers.Add(new BootTrigger());
            td.Triggers.Add(new EventTrigger
            {
                Subscription = "<QueryList><Query Id='0' Path='System'><Select Path='System'>*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27]]</Select></Query></QueryList>"
            }); 
            td.Actions.Add(strExeFilePath, "charge");

            td.Principal.UserId = "SYSTEM";
            td.Principal.LogonType = TaskLogonType.ServiceAccount;
            td.Principal.RunLevel = TaskRunLevel.Highest;

            td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.ExecutionTimeLimit = TimeSpan.FromSeconds(30);

            try
            {
                TaskService.Instance.RootFolder.RegisterTaskDefinition(chargeTaskName, td);
                Logger.WriteLine("Charge limit task scheduled: " + strExeFilePath);
            }
            catch (Exception e)
            {
                Logger.WriteLine("Can't create a charge limit task: " + e.Message);
            }
        }
    }

    public static void Schedule()
    {
        // The same guard as StartupCheck, and for the same reason: this writes the
        // running executable's path into the sign-in task, and a harness linking
        // Arsenal.Core is not the thing the user wants started at sign-in.
        if (!IsArsenalItself())
        {
            Logger.WriteLine("Startup task not written: " + strExeFilePath + " is not Arsenal.");
            return;
        }

        using (TaskDefinition td = TaskService.Instance.NewTask())
        {

            td.RegistrationInfo.Description = "Arsenal Auto Start";
            td.Triggers.Add(new LogonTrigger { UserId = WindowsIdentity.GetCurrent().Name, Delay = TimeSpan.FromSeconds(1) });
            // ConsoleConnect = fast user switch back; no SessionUnlock, it fires on every unlock
            td.Triggers.Add(new SessionStateChangeTrigger { StateChange = TaskSessionStateChangeType.ConsoleConnect, UserId = WindowsIdentity.GetCurrent().Name, Delay = TimeSpan.FromSeconds(1) });
            td.Actions.Add(strExeFilePath);

            td.Principal.LogonType = TaskLogonType.InteractiveToken;
            if (AppConfig.Is("run_as_admin") && ProcessHelper.IsUserAdministrator())
            {
                td.Principal.RunLevel = TaskRunLevel.Highest;

                // Not refused the way the SYSTEM task is: this one runs as the person who
                // asked for it, and they can already run this executable by hand. What is
                // new is that it runs elevated at logon without a prompt, so it is worth
                // saying where that file lives when anyone could have replaced it.
                if (!PathSecurity.IsProtectedFile(strExeFilePath))
                    Logger.WriteLine($"Startup task runs elevated from {strExeFilePath}, which a standard user can write to");
            }

            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.ExecutionTimeLimit = TimeSpan.Zero;

            try
            {
                TaskService.Instance.RootFolder.RegisterTaskDefinition(userTaskName, td);
                Logger.WriteLine("Startup task scheduled: " + strExeFilePath);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Can't create startup task: " + ex.Message);
                if (ProcessHelper.IsUserAdministrator())
                    MessageBox.Show("Can't create a start up task. Try running Task Scheduler by hand and manually deleting Arsenal task if it exists there.", "Scheduler Error", MessageBoxButtons.OK);
                else
                    ProcessHelper.RunAsAdmin();
            }
        }

        if (ProcessHelper.IsUserAdministrator()) ScheduleCharge();

    }

    public static void UnSchedule()
    {
        using (TaskService taskService = new TaskService())
        {
            try
            {
                taskService.RootFolder.DeleteTask(userTaskName, false);
                taskService.RootFolder.DeleteTask(legacyUserTaskName, false);
                taskService.RootFolder.DeleteTask(taskName, false);
                taskService.RootFolder.DeleteTask(legacyTaskName, false);
            }
            catch (Exception)
            {
                if (ProcessHelper.IsUserAdministrator())
                    MessageBox.Show("Can't remove task. Try running Task Scheduler by hand and manually deleting Arsenal task if it exists there.", "Scheduler Error", MessageBoxButtons.OK);
                else
                    ProcessHelper.RunAsAdmin();
            }
        }

        UnscheduleCharge();
    }
}
