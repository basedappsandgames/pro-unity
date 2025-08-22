/// LICENSE WILD WEST LABS, INC: APACHE 2.0 [https://opensource.org/license/apache-2-0]
using UnityEngine;

namespace Wildwest.Pro
{
    /// <summary>
    /// Example config script for PROManager. Must have.
    /// </summary>
    public class PROConfig : MonoBehaviour
    {
        [SerializeField]
        private PROManager _proManager;

        [SerializeField]
        private GameObject _recordingSourceObject;

        [SerializeField]
        private bool _debug = false;

        [SerializeField, Tooltip("Chunk duration in seconds.")]
        private int _chunkDurationSeconds = 15;
        [SerializeField, Tooltip("Base URL of endpoint")]
        private string _baseEndpointUrl = "http://localhost:9999";
        [SerializeField, Tooltip("API key for the endpoint")]
        private string _APIKey = "";

        // Endpoint to upload voice chunks for moderation
        private string _moderationsEndpointPath = "/api/v1/moderations"; // DO NOT CHANGE UNLESS YOU KNOW WHAT YOU ARE DOING

        // Endpoint to get JWT session token
        private string _sessionsEndpointPath = "/api/v1/sessions"; // DO NOT CHANGE UNLESS YOU KNOW WHAT YOU ARE DOING

        private IRecordingSource _recordingSource;

        // Start is called once before the first execution of Update after the MonoBehaviour is created
        void Start()
        {
            if (_proManager == null)
            {
                Debug.LogError("PROConfig: PROManager is not assigned");
                return;
            }
            _ = _proManager.Initialize(CanRecord, GetMetadata, _chunkDurationSeconds, _baseEndpointUrl, _moderationsEndpointPath, _sessionsEndpointPath, _APIKey); // MUST CALL
            if (_recordingSourceObject == null)
            {
                _recordingSourceObject = gameObject;
            }
            _recordingSource = _recordingSourceObject.GetComponent<IRecordingSource>();
            if (_recordingSource == null)
            {
                Debug.LogError(
                    "PROConfig: IRecordingSource was not found. Make sure you assign a GameObject that has an IRecordingSource behaviour on it."
                );
                return;
            }
            _recordingSource.Initialize(_proManager, _chunkDurationSeconds);
            _recordingSource.StartRecording();
        }

        void OnDestroy()
        {
            _recordingSource.Dispose();
        }

        private bool CanRecord()
        {
            if (_debug && Application.isEditor)
            {
                return true;
            }
            // your code here, eg check if headset is mounted
            return _recordingSource.CanRecord();
        }

        private PROManager.AudioEventMetadata GetMetadata()
        {
            return new PROManager.AudioEventMetadata
            {
                UserId = GetUserId(),
                RoomId = GetRoomId(),
                Language = Application.systemLanguage.ToString(),
            };
        }

        private string GetUserId()
        {
            // TODO: replace with your unique user ID (eg from your database, Meta User ID, etc)
            string userId = PlayerPrefs.GetString("PRO_USER_ID", "");
            if (string.IsNullOrEmpty(userId))
            {
                userId = System.Guid.NewGuid().ToString();
                PlayerPrefs.SetString("PRO_USER_ID", userId);
                PlayerPrefs.Save();
            }
            return userId;
        }

        private string GetRoomId()
        {
            // TODO: replace with your room ID (eg from Normcore, Photon, etc)
            return Application.productName.ToLower().Replace(" ", "_");
        }
    }
}
