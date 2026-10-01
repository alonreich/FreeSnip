using System;
using System.IO;
using System.Xml.Linq;

namespace freesnip.helpers;

internal static class StartupTaskDefinition
{
    internal static readonly XNamespace Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string Create(string executablePath, string userSid, bool elevated = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        var ns = Namespace;
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "Triggers", new XElement(ns + "LogonTrigger",
                new XElement(ns + "Enabled", true), new XElement(ns + "UserId", userSid))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "User"),
                new XElement(ns + "UserId", userSid),
                new XElement(ns + "LogonType", "InteractiveToken"),
                new XElement(ns + "RunLevel", elevated ? "HighestAvailable" : "LeastPrivilege"))),
            new XElement(ns + "Settings",
                new XElement(ns + "AllowStartOnDemand", true),
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", false),
                new XElement(ns + "StopIfGoingOnBatteries", false),
                new XElement(ns + "AllowHardTerminate", false),
                new XElement(ns + "StartWhenAvailable", true),
                new XElement(ns + "RunOnlyIfNetworkAvailable", false),
                new XElement(ns + "IdleSettings",
                    new XElement(ns + "StopOnIdleEnd", false),
                    new XElement(ns + "RestartOnIdle", false)),
                new XElement(ns + "Enabled", true),
                new XElement(ns + "RunOnlyIfIdle", false),
                new XElement(ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "User"),
                new XElement(ns + "Exec", new XElement(ns + "Command", Path.GetFullPath(executablePath)),
                    new XElement(ns + "Arguments", "--autorun"),
                    new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(Path.GetFullPath(executablePath))))))).ToString();
    }
}
