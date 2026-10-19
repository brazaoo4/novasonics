using System;
using System.Threading;
using System.Threading.Tasks;
using NovaSonics.Models;

namespace NovaSonics.Services
{
    public interface IUpdateService
    {
        Version CurrentVersion { get; }
        bool IsInstalledVersion { get; }
        string CurrentVersionDisplay { get; }

        Task<UpdateInfoModel?> CheckForUpdateAsync(CancellationToken cancellationToken = default);
        Task DownloadAndApplyUpdateAsync(UpdateInfoModel updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    }
}
