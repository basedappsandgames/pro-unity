/// LICENSE WILD WEST LABS, INC: APACHE 2.0 [https://opensource.org/license/apache-2-0]
using UnityEngine;
using System.Linq;

#if NORMCORE
using Normal.Realtime;
#endif

namespace Wildwest.Pro
{
    /// <summary>
    /// Recording source for Normcore RealtimeAvatarVoice.
    /// Handles frame-based audio delivery pattern.
    /// </summary>
    public class NormcoreRecordingSource : MonoBehaviour, IRecordingSource
    {
        public string SourceName => "Normcore_RealtimeAvatarVoice";
        public bool IsRecording { get; private set; }
        private PROManager _proManager;
        public PROManager ProManager
        {
            get => _proManager;
            set => _proManager = value;
        }

#if NORMCORE
        [SerializeField]
        private Realtime _realtime;

        [SerializeField]
        private RealtimeAvatarManager _avatarManager;
        private RealtimeAvatarVoice _realtimeAvatarVoice;
        private bool _isInitialized;

        public bool IsAvailable
        {
            get { return _realtime != null && _realtime.connected && _avatarManager != null; }
        }

        public void Initialize(PROManager proManager, int chunkDurationSec)
        {
            if (_isInitialized)
                return;

            _proManager = proManager;

            if (_realtime == null)
            {
                _realtime = Realtime.instances.First();
            }

            if (_avatarManager == null)
            {
                _avatarManager = Object.FindFirstObjectByType<RealtimeAvatarManager>();
            }

            if (_avatarManager != null)
            {
                _avatarManager.avatarCreated += OnAvatarCreated;
                _proManager.SetSampleRate(48000); // based on RealtimeAvatarVoice's sample rate
                _proManager.SetChannelCount(1); // RealtimeAvatarVoice is mono
                _isInitialized = true;
            }
            else
            {
                Debug.LogError("NormcoreRecordingSource: No RealtimeAvatarManager found");
            }
        }

        public bool CanRecord()
        {
            // only record if connected and multiple players
            return _isInitialized && IsAvailable && _avatarManager.avatars.Count > 1;
        }

        public void StartRecording()
        {
            if (!_isInitialized)
                return;

            IsRecording = true;

            if (_realtimeAvatarVoice != null)
            {
                _realtimeAvatarVoice.voiceData -= OnVoiceData; // remove if already subscribed
                _realtimeAvatarVoice.voiceData += OnVoiceData;
            }
        }

        public void StopRecording()
        {
            IsRecording = false;

            if (_realtimeAvatarVoice != null)
            {
                _realtimeAvatarVoice.voiceData -= OnVoiceData;
            }

            Debug.Log("[PRO] NormcoreRecordingSource: Stopped recording");
        }

        public void Dispose()
        {
            StopRecording();

            if (_avatarManager != null)
            {
                _avatarManager.avatarCreated -= OnAvatarCreated;
            }

            _isInitialized = false;
        }

        private void OnAvatarCreated(
            RealtimeAvatarManager avatarManager,
            RealtimeAvatar avatar,
            bool isLocalAvatar
        )
        {
            if (!isLocalAvatar)
                return;

            _realtimeAvatarVoice = avatar.GetComponentInChildren<RealtimeAvatarVoice>();

            if (_realtimeAvatarVoice != null)
            {
                _realtimeAvatarVoice.voiceData += OnVoiceData;
                Debug.Log("[PRO] <color=green>NormcoreRecordingSource: Connected to RealtimeAvatarVoice</color>");
            }
            else
            {
                Debug.LogWarning(
                    "[PRO] NormcoreRecordingSource: No RealtimeAvatarVoice found in local avatar"
                );
            }
        }

        private void OnVoiceData(float[] frame)
        {
            if (!IsRecording || _proManager == null)
                return;

            // Forward frame directly to PROManager - this is the frame-based pattern
            _proManager.OnVoiceData(frame);
        }

#else
        // Normcore not available
        public bool IsAvailable => false;

        public void Initialize(PROManager proManager, int chunkDurationSec)
        {
            Debug.LogWarning("NormcoreRecordingSource: Normcore package not available");
        }

        public bool CanRecord()
        {
            return false;
        }

        public void StartRecording() { }

        public void StopRecording() { }

        public void Dispose() { }
#endif
    }
}
