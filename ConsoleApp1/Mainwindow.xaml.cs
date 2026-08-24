using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Whack911
{
    public partial class MainWindow : Window
    {
        private SipService? _sip;
        private AppSettings _settings;
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _callTimer;
        private DateTime[] _callStartTimes = new DateTime[2];
        private int _selectedLine = 0;

        private static readonly SolidColorBrush AmberBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x00));
        private static readonly SolidColorBrush GreenBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
        private static readonly SolidColorBrush RedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
        private static readonly SolidColorBrush CyanBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));
        private static readonly SolidColorBrush DimBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0x7A));
        private static readonly SolidColorBrush BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x42));

        public MainWindow()
        {
            InitializeComponent();

            _settings = AppSettings.Load();
            UpdatePrefixHint();

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
            _clockTimer.Start();

            _callTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _callTimer.Tick += (s, e) => UpdateCallTimers();
            _callTimer.Start();

            SelectLine(0);
            InitializeSip();

            PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

        private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Don't hijack keystrokes while the user is typing into a text box
            // (Enter-to-submit for dial/transfer boxes already handles itself).
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
            if (_sip == null) return;

            string keyName = e.Key.ToString();
            string? matchedAction = null;
            foreach (var kvp in _settings.KeyBindings)
            {
                if (kvp.Value == keyName) { matchedAction = kvp.Key; break; }
            }
            if (matchedAction == null) return;

            switch (matchedAction)
            {
                case "Line1Answer": await _sip.AnswerAsync(0); break;
                case "Line1Release": _sip.Release(0); break;
                case "Line1Hold": _sip.ToggleHold(0); break;
                case "Line2Answer": await _sip.AnswerAsync(1); break;
                case "Line2Release": _sip.Release(1); break;
                case "Line2Hold": _sip.ToggleHold(1); break;
            }
        }

        private void UpdatePrefixHint()
        {
            OutboundLabel.Text = string.IsNullOrEmpty(_settings.DialPrefix)
                ? "OUTBOUND"
                : $"OUTBOUND (+{_settings.DialPrefix})";
        }

        private void InitializeSip()
        {
            _sip = new SipService(_settings);
            _sip.RegistrationChanged += OnRegistrationChanged;
            _sip.LineStateChanged += OnLineStateChanged;
            _sip.LogMessage += OnLogMessage;

            if (_settings.IsComplete)
            {
                _ = _sip.StartAsync();
            }
            else
            {
                OnLogMessage("No PBX settings configured. Click SETTINGS to get started.");
            }
        }

        // ===== SipService events =====

        private void OnRegistrationChanged(bool success, string message)
        {
            Dispatcher.Invoke(() =>
            {
                RegStatusDot.Fill = success ? GreenBrush : RedBrush;
                RegStatusText.Text = success ? "ONLINE" : "OFFLINE";
            });
        }

        private void OnLineStateChanged(int lineIndex, LineState state, string? party)
        {
            Dispatcher.Invoke(() =>
            {
                var statusText = lineIndex == 0 ? Line1StatusText : Line2StatusText;
                var partyText = lineIndex == 0 ? Line1PartyText : Line2PartyText;
                var timerText = lineIndex == 0 ? Line1TimerText : Line2TimerText;
                var answerBtn = lineIndex == 0 ? Line1Answer : Line2Answer;
                var holdBtn = lineIndex == 0 ? Line1Hold : Line2Hold;
                var hangupBtn = lineIndex == 0 ? Line1Hangup : Line2Hangup;

                switch (state)
                {
                    case LineState.Idle:
                        statusText.Text = "STANDBY";
                        statusText.Foreground = DimBrush;
                        partyText.Text = "";
                        timerText.Text = "";
                        answerBtn.Visibility = Visibility.Collapsed;
                        holdBtn.IsEnabled = false;
                        hangupBtn.IsEnabled = false;
                        break;

                    case LineState.Dialing:
                        statusText.Text = "DIALING";
                        statusText.Foreground = AmberBrush;
                        partyText.Text = $"EXT {party}";
                        answerBtn.Visibility = Visibility.Collapsed;
                        hangupBtn.IsEnabled = true;
                        break;

                    case LineState.Ringing:
                        statusText.Text = "INCOMING CALL";
                        statusText.Foreground = RedBrush;
                        partyText.Text = $"EXT {party}";
                        answerBtn.Visibility = Visibility.Visible;
                        hangupBtn.IsEnabled = true;
                        break;

                    case LineState.InCall:
                        statusText.Text = "ON CALL";
                        statusText.Foreground = GreenBrush;
                        partyText.Text = $"EXT {party}";
                        answerBtn.Visibility = Visibility.Collapsed;
                        holdBtn.IsEnabled = true;
                        holdBtn.Content = "HOLD";
                        hangupBtn.IsEnabled = true;
                        _callStartTimes[lineIndex] = DateTime.Now;
                        break;

                    case LineState.OnHold:
                        statusText.Text = "ON HOLD";
                        statusText.Foreground = AmberBrush;
                        holdBtn.Content = "RESUME";
                        break;
                }
            });
        }

        private void UpdateCallTimers()
        {
            UpdateSingleTimer(0, Line1StatusText, Line1TimerText);
            UpdateSingleTimer(1, Line2StatusText, Line2TimerText);
        }

        private void UpdateSingleTimer(int lineIndex, System.Windows.Controls.TextBlock status, System.Windows.Controls.TextBlock timer)
        {
            if (status.Text == "ON CALL")
            {
                var elapsed = DateTime.Now - _callStartTimes[lineIndex];
                timer.Text = elapsed.ToString(@"mm\:ss");
            }
        }

        private void OnLogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogText.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
                LogScroller.ScrollToEnd();
            });
        }

        // ===== Line selection =====

        private void SelectLine(int index)
        {
            _selectedLine = index;
            SelectedLineText.Text = $"LINE {index + 1}";
            Line1Panel.BorderBrush = index == 0 ? CyanBrush : BorderBrush;
            Line2Panel.BorderBrush = index == 1 ? CyanBrush : BorderBrush;
        }

        private void Line1_Selected(object sender, MouseButtonEventArgs e) => SelectLine(0);
        private void Line2_Selected(object sender, MouseButtonEventArgs e) => SelectLine(1);

        // ===== Line 1 controls =====

        private async void Line1Answer_Click(object sender, RoutedEventArgs e) => await (_sip?.AnswerAsync(0) ?? System.Threading.Tasks.Task.CompletedTask);
        private void Line1Hold_Click(object sender, RoutedEventArgs e) => _sip?.ToggleHold(0);
        private void Line1Hangup_Click(object sender, RoutedEventArgs e) => _sip?.Release(0);

        // ===== Line 2 controls =====

        private async void Line2Answer_Click(object sender, RoutedEventArgs e) => await (_sip?.AnswerAsync(1) ?? System.Threading.Tasks.Task.CompletedTask);
        private void Line2Hold_Click(object sender, RoutedEventArgs e) => _sip?.ToggleHold(1);
        private void Line2Hangup_Click(object sender, RoutedEventArgs e) => _sip?.Release(1);

        // ===== Keypad =====

        private void Keypad_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Content is string digit)
            {
                _sip?.SendDtmf(_selectedLine, digit[0]);
                OnLogMessage($"Line {_selectedLine + 1}: sent DTMF '{digit}'");
            }
        }

        // ===== Transfer =====

        private async void Transfer_Click(object sender, RoutedEventArgs e)
        {
            string dest = TransferBox.Text.Trim();
            if (string.IsNullOrEmpty(dest) || _sip == null) return;
            await _sip.BlindTransferAsync(_selectedLine, dest);
            TransferBox.Text = "";
        }

        // ===== Local dial (no prefix) =====

        private void LocalDialBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            LocalPlaceholder.Visibility = string.IsNullOrEmpty(LocalDialBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LocalDialBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) LocalCall_Click(sender, e);
        }

        private async void LocalCall_Click(object sender, RoutedEventArgs e)
        {
            string ext = LocalDialBox.Text.Trim();
            if (string.IsNullOrEmpty(ext) || _sip == null) return;

            int targetLine = FindLineForOutboundCall();
            if (targetLine == -1)
            {
                OnLogMessage("Cannot dial: all lines busy.");
                return;
            }

            await _sip.CallAsync(targetLine, ext, applyPrefix: false);
            LocalDialBox.Text = "";
        }

        // ===== Outbound dial (prefix applied) =====

        private void OutboundDialBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            OutboundPlaceholder.Visibility = string.IsNullOrEmpty(OutboundDialBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OutboundDialBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) OutboundCall_Click(sender, e);
        }

        private async void OutboundCall_Click(object sender, RoutedEventArgs e)
        {
            string number = OutboundDialBox.Text.Trim();
            if (string.IsNullOrEmpty(number) || _sip == null) return;

            int targetLine = FindLineForOutboundCall();
            if (targetLine == -1)
            {
                OnLogMessage("Cannot dial: all lines busy.");
                return;
            }

            await _sip.CallAsync(targetLine, number, applyPrefix: true);
            OutboundDialBox.Text = "";
        }

        /// <summary>Line 1 is always preferred unless busy, regardless of UI selection.</summary>
        private int FindLineForOutboundCall()
        {
            if (_sip == null || _sip.Lines.Count == 0) return -1;
            if (_sip.Lines[0].State == LineState.Idle) return 0;
            for (int i = 1; i < _sip.Lines.Count; i++)
            {
                if (_sip.Lines[i].State == LineState.Idle) return i;
            }
            return -1;
        }

        // ===== Transfer box =====

        private void TransferBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Transfer_Click(sender, e);
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(_settings) { Owner = this };
            bool? result = settingsWindow.ShowDialog();

            if (result == true)
            {
                _settings = settingsWindow.UpdatedSettings;
                _settings.Save();
                UpdatePrefixHint();

                _sip?.Stop();
                InitializeSip();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _sip?.Stop();
            base.OnClosed(e);
        }
    }
}