namespace NovaSonics.Models
{
    public class UpdateInfoModel
    {
        public bool IsUpdateAvailable { get; set; }
        public string LatestVersion { get; set; } = string.Empty;
        public string ReleaseUrl { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string AssetName { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
    }
}
