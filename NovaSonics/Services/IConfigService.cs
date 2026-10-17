using NovaSonics.Models;

namespace NovaSonics.Services
{
    public interface IConfigService
    {
        NovaSonicsConfig Config { get; }

        void Load();
        void Save();
        bool ExportConfig(string filePath);
        bool ImportConfig(string filePath);
    }
}
