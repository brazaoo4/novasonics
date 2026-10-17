using System;
using System.Windows.Controls;
using System.Windows.Input;
using NovaSonics.Models;

namespace NovaSonics.Views
{
    public partial class EqualizerView : UserControl
    {
        public EqualizerView()
        {
            InitializeComponent();
        }

        private void Slider_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is Slider slider && slider.DataContext is EqualizerBandModel band)
            {
                float step = e.Delta > 0 ? 0.5f : -0.5f;
                band.GainDb = Math.Clamp(band.GainDb + step, -10.0f, 10.0f);
                e.Handled = true;
            }
        }
    }
}
