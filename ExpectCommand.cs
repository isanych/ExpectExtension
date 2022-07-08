using EnvDTE;
using EnvDTE80;
using EnvDTE90;
using EnvDTE100;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using System.Xml;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Text;

namespace ExpectExtension
{
    class IniFile
    {
        string Path;
        string Default = "DEFAULT";

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        static extern long WritePrivateProfileString(string Section, string Key, string Value, string FilePath);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        static extern int GetPrivateProfileString(string Section, string Key, string Default, StringBuilder RetVal, int Size, string FilePath);

        public IniFile(string IniPath)
        {
            Path = new FileInfo(IniPath).FullName;
        }

        public string Read(string Key, string Section = null)
        {
            var RetVal = new StringBuilder(255);
            GetPrivateProfileString(Section ?? Default, Key, "", RetVal, 255, Path);
            return RetVal.ToString();
        }

        public void Write(string Key, string Value, string Section = null)
        {
            WritePrivateProfileString(Section ?? Default, Key, Value, Path);
        }

        public void DeleteKey(string Key, string Section = null)
        {
            Write(Key, null, Section ?? Default);
        }

        public void DeleteSection(string Section = null)
        {
            Write(null, null, Section ?? Default);
        }

        public bool KeyExists(string Key, string Section = null)
        {
            return Read(Key, Section).Length > 0;
        }
    }
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class ExpectCommand
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int GenCommandId = 0x0100;
        public const int DoCommandId = 0x0101;
        public const int CheckCommandId = 0x0102;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("8fbea08b-8dd8-4a10-b352-9d37b9e60e08");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        //private readonly AsyncPackage package;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExpectCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private ExpectCommand(/*AsyncPackage package,*/ OleMenuCommandService commandService)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            //  this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
            commandService.AddCommand(new MenuCommand(delegate { Execute("-o generate "); }, new CommandID(CommandSet, GenCommandId)));
            commandService.AddCommand(new MenuCommand(delegate { Execute("-o do "); }, new CommandID(CommandSet, DoCommandId)));
            commandService.AddCommand(new MenuCommand(delegate { Execute(""); }, new CommandID(CommandSet, CheckCommandId)));
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static ExpectCommand Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        //private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        //{
        //    get
        //    {
        //        return this.package;
        //    }
        //}

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in ExpectCommand's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new ExpectCommand(/*package,*/ commandService);
        }

#nullable enable
#pragma warning disable VSTHRD010 // Invoke single-threaded types on Main thread
        class PaneHelper
        {
            private readonly IVsOutputWindowPane? pane;
            private readonly IVsOutputWindowPaneNoPump? noPump;
            private readonly Regex r = new Regex(@"(.+)\((\d+),(\d+)\): error\s*(.*)\s*:\s*(.+)");
            public PaneHelper()
            {
                if (Package.GetGlobalService(typeof(SVsOutputWindow)) is IVsOutputWindow o)
                {
                    Guid generalPaneGuid = VSConstants.GUID_BuildOutputWindowPane;
                    o.GetPane(ref generalPaneGuid, out pane);
                    noPump = pane as IVsOutputWindowPaneNoPump;
                    pane?.Clear();
                }
            }

            public bool Ok() { return pane != null; }

            public void Write(string message)
            {
                if (message == null) { return; }
                var m = r.Matches(message);
                message += System.Environment.NewLine;
                if (m.Count > 0)
                {
                    var fileName = m[0].Groups[1].Value;
                    var line = UInt32.Parse(m[0].Groups[2].Value) - 1;
                    var error = m[0].Groups[5].Value;
                    pane?.OutputTaskItemString(message, VSTASKPRIORITY.TP_HIGH, VSTASKCATEGORY.CAT_BUILDCOMPILE, "", -1, fileName, line, error);
                }
                else
                {
                    pane?.OutputStringThreadSafe(message);
                }
            }

            public void Activate()
            {
                pane?.Activate();
                pane?.FlushToTaskList();
            }
        }

        private string? Walk(ProjectItems items, PaneHelper pane)
        {
            foreach (var i in items)
            {
                if (i is ProjectItem pi)
                {
                    var name = pi.FileNames[0];
                    if (File.Exists(name) && Path.GetFileName(name) == "CMakeLists.txt")
                    {
                        return name;
                    }
                    var ret = Walk(pi.ProjectItems, pane);
                    if (ret != null) { return ret; }
                }
            }
            return null;
        }
#pragma warning restore VSTHRD010 // Invoke single-threaded types on Main thread

        class CmakeCache
        {
            private readonly IList<string> cache;
            public CmakeCache(string buildDir)
            {
                var cacheFile = Path.Combine(buildDir, "CMakeCache.txt");
                cache = File.Exists(cacheFile) ? File.ReadAllLines(cacheFile) : Array.Empty<string>();
            }
            public string? Find(string name)
            {
                foreach (var s in cache)
                {
                    if (s.StartsWith(name)) { return s.Substring(name.Length + 1); }
                }
                return null;
            }
        }

        private void Run(string fileName, string arguments, PaneHelper pane)
        {
            System.Diagnostics.Process p = new System.Diagnostics.Process();
            p.StartInfo.FileName = fileName;
            p.StartInfo.Arguments = arguments;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardInput = true;
            p.StartInfo.RedirectStandardError = true;
            p.OutputDataReceived += (sender, args) => pane.Write(args.Data);
            p.ErrorDataReceived += (sender, args) => pane.Write(args.Data);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            p.Close();
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private void Execute(string operation)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(DTE)) is EnvDTE80.DTE2 dte) || dte.ActiveDocument == null) { return; }
            if (dte.Solution == null) { return; }
            dte.Documents.SaveAll();
            var buildDir = Path.GetDirectoryName(dte.Solution.FileName);
            IniFile? ini = null;
            var cache = new CmakeCache(buildDir);
            var maketools = cache.Find("CMAKE_HOME_DIRECTORY:INTERNAL");
            if (maketools == null) { return; }
            string[] iniDirs = { buildDir, Path.GetDirectoryName(maketools), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            foreach (string iniDir in iniDirs)
            {
                var name = Path.Combine(buildDir, ".expectconfig");
                if (!File.Exists(name)) { continue; }
                ini = new IniFile(name);
                break;
            }
            bool isTrace = ini?.KeyExists("trace") ?? false;
            bool isKeepTemp = ini?.KeyExists("keep-temp") ?? true;
            string options = ini?.Read("options") ?? "";
            string temp = ini?.Read("temp") ?? "";
            if (temp == "") { temp = Path.Combine(buildDir, "regtest"); }
            var python = cache.Find("PYTHON_COMMAND:STRING") ?? "python";
            var outDir = "";
            if (dte.Solution.SolutionBuild?.ActiveConfiguration is EnvDTE.SolutionConfiguration conf)
            {
                outDir = getOutDir(Path.Combine(buildDir, "qac_com/qac.vcxproj"), conf.Name) ?? getOutDir(Path.Combine(buildDir, "qacpp_com/qacpp.vcxproj"), conf.Name) ?? "";
            }
            var args = $"{maketools}/expect.py {operation}-o check {dte.ActiveDocument.FullName}{options}";
            if (isTrace) { args += " --trace"; }
            if (temp != "") { args += " --temp " + temp; }
            if (isKeepTemp) { args += " --keep-temp"; }
            if (outDir != "") { args += " --path " + outDir; }
            if (options != "") { args += " " + options; }
            var pane = new PaneHelper();
            if (isTrace)
            {
                pane.Write(python + " " + args);
            }
            Run(python, args, pane);
            pane.Activate(); // Brings this pane into view
        }

        private string? getOutDir(string project, string confName)
        {
            if (!File.Exists(project)) { return null; }
            var doc = new XmlDocument();
            doc.Load(project);
            foreach (var n in doc.GetElementsByTagName("OutDir"))
            {
                if ((n is System.Xml.XmlElement e) && e.GetAttribute("Condition").Contains(confName)) { return e.InnerText; }
            }
            return null;
        }

        private string? FindMaketools(string s)
        {
            var name = Path.GetFileName(s);
            if (name == null) { return null; }
            var path = Path.GetDirectoryName(s);
            return name == "maketools" ? path : FindMaketools(path);
        }
    }
}
