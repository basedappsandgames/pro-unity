/// LICENSE WILD WEST LABS, INC: APACHE 2.0 [https://opensource.org/license/apache-2-0]
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace Wildwest.Pro
{
    public class MicrophoneRecordingSource : MonoBehaviour, IRecordingSource
    {
        public string SourceName => "Microphone";
        public bool IsRecording { get; private set; }
        private PROManager _proManager;
        public PROManager ProManager
        {
            get => _proManager;
            set => _proManager = value;
        }

        private AudioClip _microphoneClip;
        private string _deviceName;
        private int _sampleRate = 16000; // record at 16kHz
        private int _lastSamplePosition = 0;
        private float[] _audioBuffer;
        private int _chunkDurationSec;

        // Permission caching to avoid checking every frame
        private bool _hasPermissionCached = false;
        private bool _permissionCacheValid = false;

        /// <summary>
        /// Checks if we can record this frame
        /// </summary>
        public bool CanRecord()
        {
            if (!_permissionCacheValid)
            {
                _hasPermissionCached = HasMicrophonePermission();
                _permissionCacheValid = true;
            }
            return _hasPermissionCached && GetMicrophoneDeviceCount() > 0;
        }

        public void Initialize(PROManager proManager, int chunkDurationSec)
        {
            _proManager = proManager;
            _proManager.SetSampleRate(_sampleRate);
            _chunkDurationSec = chunkDurationSec;
        }

        public void StartRecording()
        {
            if (GetMicrophoneDeviceCount() <= 0)
                return;

            if (!HasMicrophonePermission())
            {
                Debug.LogError("[PRO] Microphone permission not granted");
                return;
            }

            _deviceName = GetDefaultMicrophone();
            if (_deviceName == "Not set")
            {
                Debug.LogError("[PRO] No microphone device available");
                return;
            }

            // Start microphone recording with a 15-second loop buffer
            _microphoneClip = Microphone.Start(_deviceName, true, _chunkDurationSec, _sampleRate);
            _lastSamplePosition = 0;

            // Allocate buffer for processing audio data
            _audioBuffer = new float[_sampleRate / 10]; // 100ms worth of samples for better buffering

            IsRecording = true;
        }

        public void StopRecording()
        {
            IsRecording = false;

            if (_microphoneClip != null)
            {
                Microphone.End(_deviceName);
                _microphoneClip = null;
            }

            _lastSamplePosition = 0;
            Debug.Log("[PRO] Stopped microphone recording");
        }

        public void Dispose()
        {
            StopRecording();
        }

        void FixedUpdate()
        {
            if (!IsRecording || _microphoneClip == null)
                return;

            int currentPosition = Microphone.GetPosition(_deviceName);

            // Handle wrap-around in the circular buffer
            if (currentPosition < _lastSamplePosition)
            {
                _lastSamplePosition = 0;
            }

            // Calculate how many new samples we have
            int newSamples = currentPosition - _lastSamplePosition;

            if (newSamples > 0)
            {
                // Ensure we don't exceed our buffer size
                int samplesToRead = Mathf.Min(newSamples, _audioBuffer.Length);

                // Get the new audio data from the microphone clip
                // Note: Unity's GetData reads from the beginning of the clip + offset
                _microphoneClip.GetData(_audioBuffer, _lastSamplePosition);

                // Create a properly sized array for the actual samples to send
                float[] frameData = new float[samplesToRead];
                System.Array.Copy(_audioBuffer, 0, frameData, 0, samplesToRead);

                // Send the audio data to PROManager
                if (frameData.Length > 0 && IsRecording)
                {
                    _proManager.OnVoiceData(frameData);
                }

                _lastSamplePosition = currentPosition;
            }
        }

        private bool HasMicrophonePermission()
        {
#if UNITY_ANDROID
            return Permission.HasUserAuthorizedPermission(Permission.Microphone);
#else
            return true;
#endif
        }

        private int GetMicrophoneDeviceCount()
        {
            return Microphone.devices.Length;
        }

        private string GetDefaultMicrophone()
        {
            return (Microphone.devices.Length > 0) ? Microphone.devices[0] : "Not set";
        }

        /// <summary>
        /// Refreshes the cached permission status. Call this when you suspect
        /// permissions may have changed (e.g., after requesting permissions,
        /// when app resumes from background)
        /// </summary>
        public void RefreshPermissionCache()
        {
            _permissionCacheValid = false;
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
            {
                // Refresh permission cache when app regains focus
                // in case user changed permissions in system settings
                RefreshPermissionCache();
            }
        }

        void OnApplicationPause(bool pauseStatus)
        {
            if (!pauseStatus)
            {
                // Refresh permission cache when app resumes from pause
                RefreshPermissionCache();
            }
        }
    }
}
