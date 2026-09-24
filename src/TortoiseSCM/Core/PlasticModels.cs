// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace TortoiseSCM
{
    public enum PlasticCommand { Status, Add, Checkout, Checkin, Undo, Update, History, Diff, Gluon }

    public sealed class PlasticCommandRequest
    {
        public PlasticCommand Command { get; set; }
        public string WorkingDirectory { get; set; }
        public IList<string> Paths { get; set; }
        public string Comment { get; set; }
        public bool Recursive { get; set; }
        public bool IncludePrivate { get; set; }
        public bool Force { get; set; }
        public string DiffSpec { get; set; }
        public PlasticCommandRequest() { Paths = new List<string>(); }
    }

    public sealed class PlasticCommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public bool TimedOut { get; set; }
        public bool Succeeded { get { return ExitCode == 0 && !TimedOut; } }
        public PlasticCommandResult() { Output = ""; Error = ""; }
    }

    public sealed class PlasticStatusItem
    {
        public string Path { get; set; }
        public string OldPath { get; set; }
        public string Status { get; set; }
        public string StatusCode { get; set; }
        public string StatusDescription { get; set; }
        public bool IsDirectory { get; set; }
    }

    public sealed class PlasticWorkspace
    {
        public string RootPath { get; set; }
        public string Name { get; set; }
        public string Repository { get; set; }
        public string Selector { get; set; }
        public bool IsPartial { get; set; }
    }

    public sealed class PlasticProcessCommand
    {
        public string FileName { get; set; }
        public IList<string> Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public bool Interactive { get; set; }
    }

    public sealed class PlasticClientConfig
    {
        public string CmPath { get; set; }
        public string GluonPath { get; set; }
        public TimeSpan Timeout { get; set; }
        public string SettingsPath { get; set; }
        public string DiffToolPath { get; set; }
        public string DiffToolArguments { get; set; }
        public string MergeToolPath { get; set; }
        public string MergeToolArguments { get; set; }

        public PlasticClientConfig()
        {
            Timeout = TimeSpan.FromMinutes(5);
            SettingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TortoiseSCM", "settings.xml");
            CmPath = FindExecutable("cm.exe");
            GluonPath = FindExecutable("gluon.exe");
            DiffToolPath = MergeToolPath = "";
            DiffToolArguments = "\"{base}\" \"{local}\"";
            MergeToolArguments = "\"{base}\" \"{local}\" \"{remote}\" \"{merged}\"";
        }

        public static PlasticClientConfig Load()
        { return Load(null); }

        public static PlasticClientConfig Load(string settingsPath)
        {
            var result = new PlasticClientConfig();
            if (!String.IsNullOrWhiteSpace(settingsPath)) result.SettingsPath = System.IO.Path.GetFullPath(settingsPath);
            if (!File.Exists(result.SettingsPath)) return result;
            XDocument doc = SafeXml.Load(File.ReadAllText(result.SettingsPath));
            if (doc.Root == null || doc.Root.Name != "TortoiseSCM") throw new InvalidDataException("Invalid TortoiseSCM settings.");
            result.CmPath = (string)doc.Root.Element("CmPath") ?? result.CmPath;
            result.GluonPath = (string)doc.Root.Element("GluonPath") ?? result.GluonPath;
            result.DiffToolPath = (string)doc.Root.Element("DiffToolPath") ?? result.DiffToolPath;
            result.DiffToolArguments = (string)doc.Root.Element("DiffToolArguments") ?? result.DiffToolArguments;
            result.MergeToolPath = (string)doc.Root.Element("MergeToolPath") ?? result.MergeToolPath;
            result.MergeToolArguments = (string)doc.Root.Element("MergeToolArguments") ?? result.MergeToolArguments;
            double seconds;
            if (Double.TryParse((string)doc.Root.Element("TimeoutSeconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds) && seconds > 0)
                result.Timeout = TimeSpan.FromSeconds(seconds);
            return result;
        }

        public void Save()
        {
            if (Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("Timeout");
            PlasticToolArguments.ValidateConfiguration(DiffToolPath, DiffToolArguments, false);
            PlasticToolArguments.ValidateConfiguration(MergeToolPath, MergeToolArguments, true);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath));
            new XDocument(new XElement("TortoiseSCM", new XElement("CmPath", CmPath), new XElement("GluonPath", GluonPath),
                new XElement("DiffToolPath", DiffToolPath), new XElement("DiffToolArguments", DiffToolArguments),
                new XElement("MergeToolPath", MergeToolPath), new XElement("MergeToolArguments", MergeToolArguments),
                new XElement("TimeoutSeconds", Timeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)))).Save(SettingsPath);
        }

        private static string FindExecutable(string name)
        {
            var directories = new List<string>();
            directories.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PlasticSCM5", "client"));
            directories.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PlasticSCM5", "client"));
            directories.Add(@"D:\Program Files\PlasticSCM5\client");
            directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
            foreach (string directory in directories)
            {
                if (String.IsNullOrWhiteSpace(directory)) continue;
                try { string path = System.IO.Path.Combine(directory.Trim('"'), name); if (File.Exists(path)) return path; }
                catch (ArgumentException) { }
            }
            return name;
        }
    }

    internal static class SafeXml
    {
        internal static XDocument Load(string xml)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var text = new StringReader(xml))
            using (var reader = XmlReader.Create(text, settings)) return XDocument.Load(reader);
        }
    }
}
