using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Whack911
{
    public class AppSettings
    {
        public string Server { get; set; } = "";
        public string Port { get; set; } = "5060";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";

        // -1 means "use Windows default device"
        public int AudioOutputDeviceIndex { get; set; } = -1;
        public int AudioInputDeviceIndex { get; set; } = -1;

        public string DialPrefix { get; set; } = "";
        public string RingtoneFile { get; set; } = "";

        // Maps action name (e.g. "Line1Answer") to a Key name (e.g. "F1").
        // Empty/missing entries mean no keybind is set for that action.
        public Dictionary<string, string> KeyBindings { get; set; } = new();

        public static readonly string[] BindableActions = new[]
        {
            "Line1Answer", "Line1Release", "Line1Hold",
            "Line2Answer", "Line2Release", "Line2Hold"
        };

        private static string SettingsPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Whack911",
                "settings.json");

        public static string SettingsFilePath => SettingsPath;

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null)
                    {
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load settings from {SettingsPath}:\n{ex.Message}",
                    "Settings Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(SettingsPath)!;
                Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save settings to {SettingsPath}:\n{ex.Message}",
                    "Settings Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(Server) &&
            !string.IsNullOrWhiteSpace(Username) &&
            !string.IsNullOrWhiteSpace(Password);
    }
}