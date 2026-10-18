using System;
using NovaSonics.Models;

namespace NovaSonics.Services
{
    public interface IConfigService
    {
        NovaSonicsConfig Config { get; }
        event EventHandler? ConfigImported;

        void Load();
        void Save();
        bool ExportConfig(string filePath);
        bool ImportConfig(string filePath);
    }
}
