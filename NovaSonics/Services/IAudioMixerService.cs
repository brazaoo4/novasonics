using System;
using System.Collections.ObjectModel;
using NovaSonics.Models;

namespace NovaSonics.Services
{
    public interface IAudioMixerService : IDisposable
    {
        ObservableCollection<AppSessionModel> ActiveSessions { get; }

        float MasterVolume { get; set; }
        float GameVolume { get; set; }
        float ChatVolume { get; set; }
        float MicVolume { get; set; }

        bool MasterMuted { get; set; }
        bool GameMuted { get; set; }
        bool ChatMuted { get; set; }
        bool MicMuted { get; set; }

        float ChatMixBalance { get; set; } // -1.0 (Game) to +1.0 (Chat)

        float MasterPeak { get; }
        float GamePeak { get; }
        float ChatPeak { get; }
        float MicPeak { get; }

        void Initialize();
        void RefreshSessions();
        void SetAppChannel(AppSessionModel app, AppChannel channel);
        void RebindEndpoint();
    }
}
