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

    public static void StartupCheck()
    {
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

                if (ProcessHelper.IsUserAdministrator() &&
                    taskService.RootFolder.AllTasks.FirstOrDefault(t => t.Name == chargeTaskName) == null)
                    ScheduleCharge();

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

    public static void ScheduleCharge()
    {

        if (strExeFilePath is null) return;

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

        using (TaskDefinition td = TaskService.Instance.NewTask())
        {

            td.RegistrationInfo.Description = "Arsenal Auto Start";
            td.Triggers.Add(new LogonTrigger { UserId = WindowsIdentity.GetCurrent().Name, Delay = TimeSpan.FromSeconds(1) });
            // ConsoleConnect = fast user switch back; no SessionUnlock, it fires on every unlock
            td.Triggers.Add(new SessionStateChangeTrigger { StateChange = TaskSessionStateChangeType.ConsoleConnect, UserId = WindowsIdentity.GetCurrent().Name, Delay = TimeSpan.FromSeconds(1) });
            td.Actions.Add(strExeFilePath);

            td.Principal.LogonType = TaskLogonType.InteractiveToken;
            if (AppConfig.Is("run_as_admin") && ProcessHelper.IsUserAdministrator())
                td.Principal.RunLevel = TaskRunLevel.Highest;

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
