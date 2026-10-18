using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovaSonics.Models;
using NovaSonics.ViewModels;

namespace NovaSonics.Views
{
    public partial class DeviceManagerView : UserControl
    {
        public DeviceManagerView()
        {
            InitializeComponent();
        }

        private void DeviceCard_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed &&
                e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase &&
                sender is FrameworkElement element &&
                element.DataContext is AudioDeviceModel dev)
            {
                try
                {
                    var data = new DataObject("AudioDeviceModel", dev);
                    DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
                }
                catch { }
            }
        }

        private void PriorityCard_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("AudioDeviceModel") &&
                e.Data.GetData("AudioDeviceModel") is AudioDeviceModel source &&
                sender is FrameworkElement element &&
                element.DataContext is AudioDeviceModel target &&
                DataContext is DeviceManagerViewModel vm)
            {
                vm.ReorderPriorityDevice(source, target);
                e.Handled = true;
            }
        }

        private void PriorityPanel_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("AudioDeviceModel") &&
                e.Data.GetData("AudioDeviceModel") is AudioDeviceModel source &&
                source.IsExcluded &&
                DataContext is DeviceManagerViewModel vm)
            {
                vm.RestoreDevice(source);
                e.Handled = true;
            }
        }

        private void ExcludedPanel_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("AudioDeviceModel") &&
                e.Data.GetData("AudioDeviceModel") is AudioDeviceModel source &&
                !source.IsExcluded &&
                DataContext is DeviceManagerViewModel vm)
            {
                vm.ExcludeDevice(source);
                e.Handled = true;
            }
        }
    }
}
