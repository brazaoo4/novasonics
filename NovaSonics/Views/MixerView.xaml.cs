using System;
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
            Loaded += (s, e) =>
            {
                if (DataContext is MixerViewModel vm)
                {
                    vm.RebindSelectedDevices();
                }
            };
        }

        private void AppPill_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && sender is FrameworkElement element && element.DataContext is AppSessionModel app)
            {
                try
                {
                    var data = new DataObject("AppSessionModel", app);
                    DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
                }
                catch { }
            }
        }

        private void MasterApps_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("AppSessionModel") && e.Data.GetData("AppSessionModel") is AppSessionModel app
                && DataContext is MixerViewModel vm)
            {
                vm.MoveAppToChannel(app, AppChannel.Master);
            }
        }

        private void GameApps_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("AppSessionModel") && e.Data.GetData("AppSessionModel") is AppSessionModel app
                && DataContext is MixerViewModel vm)
            {
                vm.MoveAppToChannel(app, AppChannel.Game);
            }
        }

        private void MediaApps_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("AppSessionModel") && e.Data.GetData("AppSessionModel") is AppSessionModel app
                && DataContext is MixerViewModel vm)
            {
                vm.MoveAppToChannel(app, AppChannel.Media);
            }
        }

        private void ChatApps_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("AppSessionModel") && e.Data.GetData("AppSessionModel") is AppSessionModel app
                && DataContext is MixerViewModel vm)
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

        private void MoveToMedia_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AppSessionModel app && DataContext is MixerViewModel vm)
            {
                vm.MoveAppToChannel(app, AppChannel.Media);
            }
        }

        private void MoveToChat_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AppSessionModel app && DataContext is MixerViewModel vm)
            {
                vm.MoveAppToChannel(app, AppChannel.Chat);
            }
        }

        private void Master_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (DataContext is MixerViewModel vm)
            {
                float step = e.Delta > 0 ? 0.02f : -0.02f;
                vm.MasterVolume = Math.Clamp(vm.MasterVolume + step, 0f, 1f);
                e.Handled = true;
            }
        }

        private void Game_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (DataContext is MixerViewModel vm)
            {
                float step = e.Delta > 0 ? 0.02f : -0.02f;
                vm.GameVolume = Math.Clamp(vm.GameVolume + step, 0f, 1f);
                e.Handled = true;
            }
        }

        private void Media_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (DataContext is MixerViewModel vm)
            {
                float step = e.Delta > 0 ? 0.02f : -0.02f;
                vm.MediaVolume = Math.Clamp(vm.MediaVolume + step, 0f, 1f);
                e.Handled = true;
            }
        }

        private void Chat_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (DataContext is MixerViewModel vm)
            {
                float step = e.Delta > 0 ? 0.02f : -0.02f;
                vm.ChatVolume = Math.Clamp(vm.ChatVolume + step, 0f, 1f);
                e.Handled = true;
            }
        }

        private void Mic_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (DataContext is MixerViewModel vm)
            {
                float step = e.Delta > 0 ? 0.02f : -0.02f;
                vm.MicVolume = Math.Clamp(vm.MicVolume + step, 0f, 1f);
                e.Handled = true;
            }
        }
    }
}
