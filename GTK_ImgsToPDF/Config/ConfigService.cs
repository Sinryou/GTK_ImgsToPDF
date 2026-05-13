using System.Text.Json;

namespace GTK_ImgsToPDF.Config {
    internal sealed class ConfigService {
        private readonly string _configPath;

        public AppConfig Config { get; private set; }

        public ConfigService() {
            string appName = AppDomain.CurrentDomain.FriendlyName;

            string appData = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);

            string appDir = Path.Combine(appData, appName);

            Directory.CreateDirectory(appDir);

            _configPath = Path.Combine(appDir, "config.json");

            Config = Load();
        }

        private AppConfig Load() {
            if (!File.Exists(_configPath)) {
                return new AppConfig();
            }

            try {
                string json = File.ReadAllText(_configPath);

                return JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig) ?? new AppConfig();
            }
            catch {
                return new AppConfig();
            }
        }

        public void Save() {
            string json = JsonSerializer.Serialize(Config, AppJsonContext.Default.AppConfig);

            File.WriteAllText(_configPath, json);
        }
    }
}
