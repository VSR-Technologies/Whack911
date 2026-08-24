using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Whack911
{
    public class AudioDeviceItem
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public override string ToString() => Name;
    }

    public class RingtoneItem
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public override string ToString() => Name;
    }

    public class KeybindItem : INotifyPropertyChanged
    {
        public string Action { get; set; } = "";
        public string Label { get; set; } = "";

        private string _key = "";
        public string Key
        {
            get => _key;
            set { _key = value; OnPropertyChanged(nameof(Key)); OnPropertyChanged(nameof(KeyDisplay)); }
        }

        public string KeyDisplay => string.IsNullOrEmpty(Key) ? "(unset - click, then press a key)" : Key;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _original;
        public AppSettings UpdatedSettings { get; private set; }
        private readonly List<KeybindItem> _keybindItems = new();

        private static string RingtonesFolder =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ringtones");

        private static readonly Dictionary<string, string> ActionLabels = new()
        {
            { "Line1Answer", "Line 1: Answer" },
            { "Line1Release", "Line 1: Release" },
            { "Line1Hold", "Line 1: Hold/Resume" },
            { "Line2Answer", "Line 2: Answer" },
            { "Line2Release", "Line 2: Release" },
            { "Line2Hold", "Line 2: Hold/Resume" },
        };

        public SettingsWindow(AppSettings current)
        {
            InitializeComponent();
            _original = current;
            UpdatedSettings = current;

            LoadAudioDevices();
            LoadRingtones();
            LoadKeybinds();
            PopulateFields();
        }

        private void LoadKeybinds()
        {
            foreach (var action in AppSettings.BindableActions)
            {
                _keybindItems.Add(new KeybindItem
                {
                    Action = action,
                    Label = ActionLabels.TryGetValue(action, out var lbl) ? lbl : action,
                    Key = _original.KeyBindings.TryGetValue(action, out var k) ? k : ""
                });
            }
            KeybindList.ItemsSource = _keybindItems;
        }

        private void Keybind_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox tb || tb.Tag is not string action) return;

            e.Handled = true; // don't let the key actually type into the readonly box

            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                var itemClear = _keybindItems.Find(k => k.Action == action);
                if (itemClear != null) itemClear.Key = "";
                return;
            }

            // Ignore modifier-only presses
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.System)
                return;

            var item = _keybindItems.Find(k => k.Action == action);
            if (item != null)
            {
                item.Key = e.Key.ToString();
            }
        }

        private void LoadAudioDevices()
        {
            var outputDevices = new List<AudioDeviceItem>
            {
                new AudioDeviceItem { Index = -1, Name = "(Windows Default)" }
            };
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                outputDevices.Add(new AudioDeviceItem { Index = i, Name = caps.ProductName });
            }
            OutputDeviceCombo.ItemsSource = outputDevices;

            var inputDevices = new List<AudioDeviceItem>
            {
                new AudioDeviceItem { Index = -1, Name = "(Windows Default)" }
            };
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                inputDevices.Add(new AudioDeviceItem { Index = i, Name = caps.ProductName });
            }
            InputDeviceCombo.ItemsSource = inputDevices;
        }

        private void LoadRingtones()
        {
            var ringtones = new List<RingtoneItem>
            {
                new RingtoneItem { Path = "", Name = "(None)" }
            };

            try
            {
                Directory.CreateDirectory(RingtonesFolder);
                foreach (var file in Directory.GetFiles(RingtonesFolder, "*.wav"))
                {
                    ringtones.Add(new RingtoneItem
                    {
                        Path = file,
                        Name = Path.GetFileNameWithoutExtension(file)
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not read Ringtones folder:\n{ex.Message}", "Ringtones",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            RingtoneCombo.ItemsSource = ringtones;
        }

        private void PopulateFields()
        {
            ServerBox.Text = _original.Server;
            PortBox.Text = _original.Port;
            UsernameBox.Text = _original.Username;
            PasswordBox.Password = _original.Password;
            DialPrefixBox.Text = _original.DialPrefix;

            SelectDeviceByIndex(OutputDeviceCombo, _original.AudioOutputDeviceIndex);
            SelectDeviceByIndex(InputDeviceCombo, _original.AudioInputDeviceIndex);

            bool foundRingtone = false;
            foreach (var item in RingtoneCombo.Items)
            {
                if (item is RingtoneItem rt && rt.Path == _original.RingtoneFile)
                {
                    RingtoneCombo.SelectedItem = item;
                    foundRingtone = true;
                    break;
                }
            }
            if (!foundRingtone && RingtoneCombo.Items.Count > 0)
            {
                RingtoneCombo.SelectedIndex = 0;
            }
        }

        private void SelectDeviceByIndex(System.Windows.Controls.ComboBox combo, int index)
        {
            foreach (var item in combo.Items)
            {
                if (item is AudioDeviceItem device && device.Index == index)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var updated = new AppSettings
            {
                Server = ServerBox.Text.Trim(),
                Port = string.IsNullOrWhiteSpace(PortBox.Text) ? "5060" : PortBox.Text.Trim(),
                Username = UsernameBox.Text.Trim(),
                Password = PasswordBox.Password,
                AudioOutputDeviceIndex = (OutputDeviceCombo.SelectedItem as AudioDeviceItem)?.Index ?? -1,
                AudioInputDeviceIndex = (InputDeviceCombo.SelectedItem as AudioDeviceItem)?.Index ?? -1,
                DialPrefix = DialPrefixBox.Text.Trim(),
                RingtoneFile = (RingtoneCombo.SelectedItem as RingtoneItem)?.Path ?? ""
            };

            foreach (var item in _keybindItems)
            {
                if (!string.IsNullOrEmpty(item.Key))
                {
                    updated.KeyBindings[item.Action] = item.Key;
                }
            }

            if (string.IsNullOrWhiteSpace(updated.Server) ||
                string.IsNullOrWhiteSpace(updated.Username) ||
                string.IsNullOrWhiteSpace(updated.Password))
            {
                MessageBox.Show("Server, Extension, and Secret are required.", "Incomplete Settings",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdatedSettings = updated;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}