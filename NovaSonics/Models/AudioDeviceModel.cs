using CommunityToolkit.Mvvm.ComponentModel;

namespace NovaSonics.Models
{
    public partial class AudioDeviceModel : ObservableObject
    {
        [ObservableProperty]
        private string _id = string.Empty;

        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private string _description = string.Empty;

        [ObservableProperty]
        private bool _isDefault;

        [ObservableProperty]
        private bool _isActive;

        [ObservableProperty]
        private bool _isExcluded;

        [ObservableProperty]
        private int _priorityOrder;

        [ObservableProperty]
        private bool _isNextDevice;

        [ObservableProperty]
        private bool _isPreviousDevice;

        public bool IsNovaHeadset => (Name.Contains("Nova 7", System.StringComparison.OrdinalIgnoreCase) ||
                                      Description.Contains("Nova 7", System.StringComparison.OrdinalIgnoreCase)) &&
                                     !Name.Contains("SteelSeries Sonar", System.StringComparison.OrdinalIgnoreCase);
    }
}
