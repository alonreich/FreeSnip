using freesnip.native;
using freesnip.native.foundation;
using System;
using log4net;
using Windows.Data.Xml.Dom;
using Windows.Foundation.Metadata;
using Windows.UI.Notifications;

namespace freesnip.helpers
{
    public static class ToastHelper
    {
        private static readonly ILog Log = freesnip.foundation.core.LogHelper.GetLogger(typeof(ToastHelper));

        public static void ShowToast(string title, string message)
        {
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.UI.Notifications.ToastNotification"))
                {
                    Log.Warn("Toast notifications not supported on this OS.");
                    return;
                }

                XmlDocument toastXml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                XmlNodeList textElements = toastXml.GetElementsByTagName("text");
                textElements[0].AppendChild(toastXml.CreateTextNode(title ?? string.Empty));
                textElements[1].AppendChild(toastXml.CreateTextNode(message ?? string.Empty));
                ToastNotificationManager.CreateToastNotifier(DeploymentFootprint.AppName).Show(new ToastNotification(toastXml));
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to show toast notification: " + ex.Message);
            }
        }
    }
}
