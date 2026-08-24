using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Media;
using SIPSorceryMedia.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Whack911
{
    public enum LineState
    {
        Idle,
        Dialing,
        Ringing,   // incoming, not yet answered
        InCall,
        OnHold,
        Ended
    }

    public class CallLine
    {
        public int Index { get; }
        public SIPUserAgent? Agent { get; set; }
        public LineState State { get; set; } = LineState.Idle;
        public string? RemoteParty { get; set; }
        public SIPServerUserAgent? PendingIncoming { get; set; }
        public VoIPMediaSession? MediaSession { get; set; }

        public CallLine(int index)
        {
            Index = index;
        }
    }

    public class SipService
    {
        private const int LINE_COUNT = 2;

        private SIPTransport? _sipTransport;
        private SIPRegistrationUserAgent? _regUserAgent;
        private AppSettings _settings;
        private readonly List<CallLine> _lines = new();

        // Exactly ONE agent is ever "listening" for brand-new incoming INVITEs at
        // a time. Having multiple agents subscribed to OnIncomingCall on the same
        // shared transport was causing duplicate/cross-line firing - this fixes that.
        private SIPUserAgent? _incomingListenerAgent;

        private WaveOutEvent? _ringtonePlayer;
        private AudioFileReader? _ringtoneFile;

        public bool IsRegistered { get; private set; }
        public IReadOnlyList<CallLine> Lines => _lines;

        public event Action<int, LineState, string?>? LineStateChanged;
        public event Action<bool, string>? RegistrationChanged;
        public event Action<string>? LogMessage;

        public SipService(AppSettings settings)
        {
            _settings = settings;
        }

        public void UpdateSettings(AppSettings settings) => _settings = settings;

        private void Log(string message) => LogMessage?.Invoke(message);

        public async Task StartAsync()
        {
            if (!_settings.IsComplete)
            {
                Log("Cannot start: PBX settings incomplete. Open Settings to configure.");
                return;
            }

            _sipTransport = new SIPTransport();

            _sipTransport.SIPTransportRequestReceived += async (localEP, remoteEP, sipRequest) =>
            {
                if (sipRequest.Method == SIPMethodsEnum.OPTIONS || sipRequest.Method == SIPMethodsEnum.NOTIFY)
                {
                    var response = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                    await _sipTransport.SendResponseAsync(response);
                }
            };

            _lines.Clear();
            for (int i = 0; i < LINE_COUNT; i++)
            {
                _lines.Add(new CallLine(i));
            }

            ArmIncomingListener();

            _regUserAgent = new SIPRegistrationUserAgent(_sipTransport, _settings.Username, _settings.Password, _settings.Server, 300);

            _regUserAgent.RegistrationSuccessful += (uri, resp) =>
            {
                IsRegistered = true;
                Log($"Registered: {uri}");
                RegistrationChanged?.Invoke(true, "REGISTERED");
            };

            _regUserAgent.RegistrationFailed += (uri, resp, err) =>
            {
                IsRegistered = false;
                Log($"Registration failed: {err}");
                RegistrationChanged?.Invoke(false, err);
            };

            Log("Starting registration...");
            _regUserAgent.Start();
        }

        /// <summary>
        /// Creates a fresh agent whose sole job is to catch the NEXT incoming
        /// INVITE. Once a call arrives, it's handed off to a real line and this
        /// method is called again to arm a new listener for the call after that.
        /// </summary>
        private void ArmIncomingListener()
        {
            var listener = new SIPUserAgent(_sipTransport, null);
            _incomingListenerAgent = listener;

            listener.OnIncomingCall += (ua, req) =>
            {
                // A genuinely NEW call never has a To-tag. Anything WITH a To-tag
                // is a request within an EXISTING dialog (re-INVITE from Hold,
                // Transfer, etc. on some other line) - this listener has no
                // business treating that as a fresh incoming call. Without this
                // check, Hold/Transfer re-INVITEs were being misread as new
                // incoming calls, causing phantom rings, "all lines busy" log
                // spam, and interference with the real hold/transfer negotiation.
                if (!string.IsNullOrEmpty(req.Header.To?.ToTag))
                {
                    Log($"Ignoring in-dialog request (To-tag present, not a new call): {req.Header.CallId}");
                    return;
                }

                // Line 1 gets priority; Line 2 only rings if Line 1 is busy.
                CallLine? targetLine = null;
                foreach (var l in _lines)
                {
                    if (l.State == LineState.Idle) { targetLine = l; break; }
                }

                if (targetLine == null)
                {
                    Log("Incoming call rejected: all lines busy.");
                    listener.AcceptCall(req).Reject(SIPResponseStatusCodesEnum.BusyHere, null);
                    ArmIncomingListener();
                    return;
                }

                targetLine.Agent = listener;
                targetLine.RemoteParty = req.Header.From?.FromURI?.User ?? "UNKNOWN";
                targetLine.PendingIncoming = listener.AcceptCall(req);
                targetLine.State = LineState.Ringing;

                Log($"Line {targetLine.Index + 1}: incoming call from {targetLine.RemoteParty}");
                LineStateChanged?.Invoke(targetLine.Index, LineState.Ringing, targetLine.RemoteParty);
                PlayRingtone();

                AttachCallHandlers(listener, targetLine);

                // Arm a fresh listener immediately so a second incoming call
                // (destined for the other line) can still be caught.
                ArmIncomingListener();
            };
        }

        /// <summary>Wires up end-of-call handlers for one specific agent/line pairing.</summary>
        private void AttachCallHandlers(SIPUserAgent agent, CallLine line)
        {
            agent.OnCallHungup += (dialog) =>
            {
                Log($"Line {line.Index + 1}: call ended.");
                StopRingtone();
                ResetLine(line);
            };

            agent.ClientCallFailed += (uac, error, sipResponse) =>
            {
                Log($"Line {line.Index + 1}: call failed - {error}");
                ResetLine(line);
            };

            agent.ClientCallAnswered += (uac, resp) =>
            {
                Log($"Line {line.Index + 1}: answered.");
                line.State = LineState.InCall;
                LineStateChanged?.Invoke(line.Index, LineState.InCall, line.RemoteParty);
            };
        }

        private void ResetLine(CallLine line)
        {
            line.State = LineState.Idle;
            line.RemoteParty = null;
            line.PendingIncoming = null;
            line.Agent = null;
            LineStateChanged?.Invoke(line.Index, LineState.Idle, null);
        }

        private (VoIPMediaSession session, WindowsAudioEndPoint audio) CreateMediaSession()
        {
            var windowsAudio = new WindowsAudioEndPoint(new AudioEncoder(), _settings.AudioOutputDeviceIndex, _settings.AudioInputDeviceIndex);
            windowsAudio.RestrictFormats(format => format.Codec == SIPSorceryMedia.Abstractions.AudioCodecsEnum.PCMU);
            var session = new VoIPMediaSession(windowsAudio.ToMediaEndPoints());
            session.AcceptRtpFromAny = true;
            return (session, windowsAudio);
        }

        // ===== Ringtone =====

        private void PlayRingtone()
        {
            try
            {
                if (string.IsNullOrEmpty(_settings.RingtoneFile) || !File.Exists(_settings.RingtoneFile)) return;
                StopRingtone();
                _ringtoneFile = new AudioFileReader(_settings.RingtoneFile);
                _ringtonePlayer = _settings.AudioOutputDeviceIndex >= 0
                    ? new WaveOutEvent { DeviceNumber = _settings.AudioOutputDeviceIndex }
                    : new WaveOutEvent();
                _ringtonePlayer.Init(new LoopStream(_ringtoneFile));
                _ringtonePlayer.Play();
            }
            catch (Exception ex)
            {
                Log($"Ringtone playback error: {ex.Message}");
            }
        }

        public void StopRingtone()
        {
            _ringtonePlayer?.Stop();
            _ringtonePlayer?.Dispose();
            _ringtonePlayer = null;
            _ringtoneFile?.Dispose();
            _ringtoneFile = null;
        }

        // ===== Call control =====

        public async Task CallAsync(int lineIndex, string extension, bool applyPrefix = true)
        {
            if (lineIndex < 0 || lineIndex >= _lines.Count) return;
            var line = _lines[lineIndex];

            if (line.State != LineState.Idle)
            {
                Log($"Line {lineIndex + 1} is busy.");
                return;
            }

            var agent = new SIPUserAgent(_sipTransport, null);
            line.Agent = agent;
            AttachCallHandlers(agent, line);

            string dialTarget = applyPrefix ? (_settings.DialPrefix + extension) : extension;
            line.RemoteParty = extension;
            line.State = LineState.Dialing;
            LineStateChanged?.Invoke(lineIndex, LineState.Dialing, extension);

            var (session, _) = CreateMediaSession();
            line.MediaSession = session;
            string destination = $"sip:{dialTarget}@{_settings.Server}";
            Log($"Line {lineIndex + 1}: calling {dialTarget}...");

            bool result = await agent.Call(destination, _settings.Username, _settings.Password, session);

            if (!result)
            {
                Log($"Line {lineIndex + 1}: call did not connect.");
                ResetLine(line);
            }
        }

        public async Task AnswerAsync(int lineIndex)
        {
            if (lineIndex < 0 || lineIndex >= _lines.Count) return;
            var line = _lines[lineIndex];
            if (line.State != LineState.Ringing || line.PendingIncoming == null || line.Agent == null) return;

            StopRingtone();
            var (session, _) = CreateMediaSession();
            line.MediaSession = session;

            bool answered = await line.Agent.Answer(line.PendingIncoming, session);
            line.PendingIncoming = null;

            if (answered)
            {
                Log($"Line {lineIndex + 1}: answered.");
                line.State = LineState.InCall;
                LineStateChanged?.Invoke(lineIndex, LineState.InCall, line.RemoteParty);
            }
            else
            {
                Log($"Line {lineIndex + 1}: failed to answer.");
                ResetLine(line);
            }
        }

        /// <summary>
        /// Unified release: rejects if ringing, cancels if still dialing (not yet
        /// answered), or hangs up if actively connected/on hold. Handles every
        /// call state so the Release button always works.
        /// </summary>
        public void Release(int lineIndex)
        {
            if (lineIndex < 0 || lineIndex >= _lines.Count) return;
            var line = _lines[lineIndex];

            switch (line.State)
            {
                case LineState.Ringing:
                    StopRingtone();
                    line.PendingIncoming?.Reject(SIPResponseStatusCodesEnum.BusyHere, null);
                    Log($"Line {lineIndex + 1}: declined.");
                    ResetLine(line);
                    break;

                case LineState.Dialing:
                    line.Agent?.Cancel();
                    Log($"Line {lineIndex + 1}: call canceled.");
                    ResetLine(line);
                    break;

                case LineState.InCall:
                case LineState.OnHold:
                    if (line.Agent?.IsCallActive == true)
                    {
                        line.Agent.Hangup();
                        Log($"Line {lineIndex + 1}: hung up.");
                    }
                    ResetLine(line);
                    break;

                default:
                    break; // already idle, nothing to do
            }
        }

        public void ToggleHold(int lineIndex)
        {
            if (lineIndex < 0 || lineIndex >= _lines.Count) return;
            var line = _lines[lineIndex];
            if (line.Agent == null) return;

            if (line.State == LineState.InCall)
            {
                line.Agent.PutOnHold();
                line.State = LineState.OnHold;
                Log($"Line {lineIndex + 1}: on hold.");
                LineStateChanged?.Invoke(lineIndex, LineState.OnHold, line.RemoteParty);
            }
            else if (line.State == LineState.OnHold)
            {
                line.Agent.TakeOffHold();
                line.State = LineState.InCall;
                Log($"Line {lineIndex + 1}: resumed.");
                LineStateChanged?.Invoke(lineIndex, LineState.InCall, line.RemoteParty);
            }
        }

        public void SendDtmf(int lineIndex, char digit)
        {
            if (lineIndex < 0 || lineIndex >= _lines.Count) return;
            var line = _lines[lineIndex];
            if (line.Agent != null && line.State == LineState.InCall && byte.TryParse(digit.ToString(), out byte b))
            {
                _ = line.Agent.SendDtmf(b);
            }
        }

        public async Task BlindTransferAsync(int lineIndex, string destination)
        {
            if (lineIndex < 0 || lineIndex >= _lines.Count) return;
            var line = _lines[lineIndex];
            if ((line.State != LineState.InCall && line.State != LineState.OnHold) || line.Agent == null) return;

            string dialTarget = _settings.DialPrefix + destination;
            var destUri = SIPURI.ParseSIPURI($"sip:{dialTarget}@{_settings.Server}");
            Log($"Line {lineIndex + 1}: blind transfer to {dialTarget}...");

            bool result = await line.Agent.BlindTransfer(destUri, TimeSpan.FromSeconds(10), System.Threading.CancellationToken.None);

            if (result)
            {
                Log($"Line {lineIndex + 1}: transfer accepted - releasing line.");
                // Don't wait on the server to send us a BYE for the old leg -
                // free the line immediately so the console reflects reality
                // instead of showing a stale "on call" state.
                if (line.Agent.IsCallActive)
                {
                    line.Agent.Hangup();
                }
                ResetLine(line);
            }
            else
            {
                Log($"Line {lineIndex + 1}: transfer failed.");
            }
        }

        public void Stop()
        {
            StopRingtone();
            _regUserAgent?.Stop();
            _sipTransport?.Shutdown();
            IsRegistered = false;
            RegistrationChanged?.Invoke(false, "STOPPED");
        }
    }

    public class LoopStream : WaveStream
    {
        private readonly WaveStream _source;
        public LoopStream(WaveStream source) => _source = source;

        public override WaveFormat WaveFormat => _source.WaveFormat;
        public override long Length => _source.Length;
        public override long Position
        {
            get => _source.Position;
            set => _source.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = _source.Read(buffer, offset + totalRead, count - totalRead);
                if (read == 0)
                {
                    if (_source.Position == 0) break;
                    _source.Position = 0;
                    continue;
                }
                totalRead += read;
            }
            return totalRead;
        }
    }
}