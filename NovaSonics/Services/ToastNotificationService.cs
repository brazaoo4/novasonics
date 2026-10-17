using System;
using Microsoft.Toolkit.Uwp.Notifications;

namespace NovaSonics.Services
{
    public class ToastNotificationService : INotificationService
    {
        public void ShowLowBatteryToast(int batteryPercent)
        {
            try
            {
                new ToastContentBuilder()
                    .AddText("⚠️ Arctis Nova 7 Low Battery Warning")
                    .AddText($"Headset is at {batteryPercent}% battery — Plug in for 15 mins to avoid low-battery beeps.")
                    .Show();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to show toast: {ex.Message}");
            }
        }

        public void ShowCustomToast(string title, string message)
        {
            try
            {
                new ToastContentBuilder()
                    .AddText(title)
                    .AddText(message)
                    .Show();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to show custom toast: {ex.Message}");
            }
        }
    }
}
