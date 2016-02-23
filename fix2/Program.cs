using System;
using System.Collections.Generic;
using System.IO;

public class Program
{
    public static void Main(string[] args)
    {
        string cmdFn="", cmdPattern="", cmdError="";
        TimeSpan? cmdOffset=null;
        var cmdArgs = new LinkedList<string>(args);
        // Get the filename
        if (cmdArgs.Count > 0 && !cmdArgs.First.Value.StartsWith("/"))
        {
            cmdFn = cmdArgs.First.Value; cmdArgs.RemoveFirst();
        }
        // Search for further cmdSwitches
        while (cmdError == "" && cmdArgs.Count > 0)
        {
            var cmdSwitch = cmdArgs.First.Value; cmdArgs.RemoveFirst();
            if (cmdSwitch == "/rename")
            {
                if (cmdPattern != "") {cmdError = "duplicate /rename"; break;}
                cmdPattern = "%{datetime} - %{fn} - %{place}";
                if (cmdArgs.Count > 0 && !cmdArgs.First.Value.StartsWith("/")) {cmdPattern = cmdArgs.First.Value; cmdArgs.RemoveFirst();}
            }
            else if (cmdSwitch.StartsWith("/day") || cmdSwitch.StartsWith("/hour") || cmdSwitch.StartsWith("/minute"))
            {
                var len = 0; Func<int,TimeSpan> mkts = (n) => default(TimeSpan);
                if (cmdSwitch.StartsWith("/day")) {len = 4; mkts = (n) => TimeSpan.FromDays(n);}
                if (cmdSwitch.StartsWith("/hour")) {len = 5; mkts = (n) => TimeSpan.FromHours(n);}
                if (cmdSwitch.StartsWith("/minute")) {len = 7; mkts = (n) => TimeSpan.FromMinutes(n);}
                var snum = cmdSwitch.Substring(len);
                if (!snum.StartsWith("+") && !snum.StartsWith("-")) {cmdError = cmdSwitch; break;}
                var num = 0; if (!int.TryParse(snum, out num)) {cmdError = cmdSwitch; break;}
                cmdOffset = cmdOffset.HasValue ? cmdOffset : new TimeSpan(0);
                cmdOffset = cmdOffset + mkts(num);
            }
            else if (cmdSwitch == "/?")
            {
                cmdFn = "";
            }
            else
            {
                cmdError = cmdSwitch; break;
            }
        }
        
        if (cmdError != "") {Console.WriteLine("Unrecognized command: {0}", cmdError); return;}
        if (cmdArgs.Count > 0) throw new Exception("Failed to parse command line");
        if (cmdFn == "")
        {
            Console.WriteLine("FixCameraDate \"a.jpg\" [/rename [\"pattern\"]] [/day+n] [/hour+n] [/minute+n]");
            Console.WriteLine("  Filename can include * and ? wildcards");
            Console.WriteLine("  /rename: pattern defaults to \"%{datetime} - %{fn} - %{place}\" and");
            Console.WriteLine("           can include %{date/time/year/month/day/hour/minute/second/place}");
            Console.WriteLine("  /day,/hour,/minute: adjust the timestamp; can be + or -");
            Console.WriteLine();
            Console.WriteLine("EXAMPLES:");
            Console.WriteLine("FixCameraDate \"a.jpg\"");
            Console.WriteLine("FixCameraDate \"*.jpg\" /rename \"%{date} - %{time} - %{fn}.jpg\"");
            Console.WriteLine("FixCameraDate \"*D*.mov\" /hour+8 /rename");
            return;
        }
        
        var globPath = "", globMatch = cmdFn;
        if (globMatch.Contains("\\"))
        {
            globPath = GetDirectoryName(globMatch); globMatch = GetFileName(globMatch);
        }
        else
        {
            globPath = Environment.CurrentDirectory;
        }
            
        var globFiles = Directory.GetFiles(globPath, globMatch);
        if (globFiles.Length == 0) {Console.WriteLine("Not found - \"{0}\"", cmdFn);}

        var filesToDo = new Queue<FileToDo>();
        var gpsToDo = new Dictionary<int,FileToDo>();
        var gpsNextRequestId = 1;
        foreach (var globFile in globFiles)
        {
            filesToDo.Enqueue(new FileToDo {fn = globFile});
        }
    }
}
