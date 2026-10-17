using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovaSonics.Models;
using NovaSonics.ViewModels;

namespace NovaSonics.Views
{
    public partial class MixerView : UserControl
    {
        public MixerView()
        {
            InitializeComponent();
        }

        private void AppPill_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && sender is FrameworkElement element && element.DataContext is AppSessionModel app)
            {
                var data = new DataObject("AppSessionModel", app);
                DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
            }
        }

        private void GameApps_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("AppSessionModel") && e.Data.GetData("AppSessionModel") is AppSessionModel app)
            {
                if (DataContext is MixerViewModel vm)
                {
                    vm.MoveAppToChannel(app, AppChannel.Game);
                }
            }
        }

        private void ChatApps_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("AppSessionModel") && e.Data.GetData("AppSessionModel") is AppSessionModel app)
            {
                if (DataContext is MixerViewModel vm)
                {
                    vm.MoveAppToChannel(app, AppChannel.Chat);
                }
            }
        }

        private void MoveToChat_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AppSessionModel app && DataContext is MixerViewModel vm)
            {
                vm.MoveAppToChannel(app, AppChannel.Chat);
            }
        }

        private void MoveToGame_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AppSessionModel app && DataContext is MixerViewModel vm)
            {
                vm.MoveAppToChannel(app, AppChannel.Game);
            }
        }
    }
}
