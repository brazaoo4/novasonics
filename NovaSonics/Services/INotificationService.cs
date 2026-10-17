namespace NovaSonics.Services
{
    public interface INotificationService
    {
        void ShowLowBatteryToast(int batteryPercent);
        void ShowCustomToast(string title, string message);
    }
}
