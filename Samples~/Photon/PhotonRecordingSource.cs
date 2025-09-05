/// LICENSE WILD WEST LABS, INC: APACHE 2.0 [https://opensource.org/license/apache-2-0]
/// This script is a Photon recording source for the PRO system.
/// IMPORT: Package Manager > PRO > Samples > Photon > IMPORT
/// Attach to a GameObject and drag the GameObject into the PRORecordingSource field in the PROConfig.
/// Must install Photon Voice from the Asset Store [https://assetstore.unity.com/packages/tools/audio/photon-voice-2-130518]
using System;
using UnityEngine;
#if PHOTON_UNITY_NETWORKING
using Photon.Voice;
using Photon.Voice.Unity;
#endif

namespace Wildwest.Pro
{
#if PHOTON_UNITY_NETWORKING
    public class PhotonPROProcessor : IProcessor<float>
    {
        public PROManager proManager;
        public bool IsRecording { get; private set; }

        public float[] Process(float[] buf)
        {
            if (!proManager)
                return Array.Empty<float>();

            if (IsRecording)
                proManager.OnVoiceData(buf);

            return buf;
        }

        public void Dispose() { }

        public void SetRecording(bool isRecording)
        {
            IsRecording = isRecording;
        }
    }
#endif
}

namespace Wildwest.Pro
{
    public class PhotonRecordingSource : MonoBehaviour, IRecordingSource
    {
        public string SourceName => "Photon_LocalVoiceAudioFloat";
        private PROManager _proManager;
        public PROManager ProManager
        {
            get => _proManager;
            set => _proManager = value;
        }

        public bool IsRecording { get; private set; }

#if PHOTON_UNITY_NETWORKING
        private LocalVoiceAudioFloat _voice;
        private bool _isInitialized;
        private PhotonPROProcessor _photonPROProcessor;

        public bool IsAvailable => _voice != null;

        public void Initialize(PROManager proManager, int chunkDurationSec)
        {
            if (_isInitialized)
                return;

            _proManager = proManager;
            _photonPROProcessor = new PhotonPROProcessor { proManager = _proManager };
            _isInitialized = true;
            Debug.Log("[PRO] PhotonRecordingSource: Initialized");
        }

        public bool CanRecord()
        {
            return _isInitialized && IsAvailable;
        }

        // init once we set up photon voice; called from Photon's Recorder via SendMessage
        public void PhotonVoiceCreated(PhotonVoiceCreatedParams voiceCreatedParams)
        {
            _voice = voiceCreatedParams.Voice as LocalVoiceAudioFloat;
            if (_voice != null)
            {
                _proManager.ConfigureChunker(_voice.Info.SamplingRate, _voice.Info.Channels);

                _voice.AddPostProcessor(_photonPROProcessor);
            }
        }

        public void StartRecording()
        {
            if (!_isInitialized)
                return;
            IsRecording = true;
            _photonPROProcessor.SetRecording(IsRecording);
        }

        public void StopRecording()
        {
            if (!_isInitialized)
                return;
            IsRecording = false;
            _photonPROProcessor.SetRecording(IsRecording);
        }

        public void Dispose()
        {
            if (!_isInitialized)
                return;
            StopRecording();
            _isInitialized = false;
        }
#else
        public bool IsAvailable => false;
        public bool IsRecording { get; private set; }

        public void Initialize(PROManager proManager, int chunkDurationSec)
        {
            _proManager = proManager;
            Debug.LogWarning("PhotonRecordingSource: Photon Voice is not installed");
        }

        public bool CanRecord()
        {
            return false;
        }

        public void StartRecording() 
        {
            IsRecording = true;
        }

        public void StopRecording() 
        {
            IsRecording = false;
        }

        public void Dispose() 
        {
            IsRecording = false;
        }
#endif
    }
}
