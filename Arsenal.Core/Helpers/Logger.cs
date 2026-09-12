using System.Diagnostics;
using Arsenal.Helpers;

public static class Logger
{
    public static string appPath = Environment.GetFolderPath(ProcessHelper.IsRunningAsSystem() ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.ApplicationData) + "\\Arsenal";
    public static string logFile = appPath + "\\log.txt";

    private static readonly Random _random = new Random();
    private static readonly object _lock = new object();

    public static void WriteLine(string logMessage)
    {
        Debug.WriteLine($"{DateTime.Now}: {logMessage}");

        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(appPath)) Directory.CreateDirectory(appPath);
                using StreamWriter writer = File.AppendText(logFile);
                writer.WriteLine($"{DateTime.Now}: {logMessage}");
            }
            catch { }

            if (_random.Next(100) == 1) Cleanup();
        }
    }

    public static void Cleanup()
    {
        try
        {
            // A damaged or long-running installation can have a very large log. Keep
            // only the bounded tail in memory instead of materialising the whole file
            // and two extra LINQ arrays during cleanup.
            const int retainedLineCount = 2000;
            var tail = new Queue<string>(retainedLineCount);
            foreach (string line in File.ReadLines(logFile))
            {
                if (tail.Count == retainedLineCount) tail.Dequeue();
                tail.Enqueue(line);
            }
            File.WriteAllLines(logFile, tail);
        }
        catch { }
    }

}
