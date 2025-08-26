/// LICENSE WILD WEST LABS, INC: APACHE 2.0 [https://opensource.org/license/apache-2-0]
using System.Threading.Tasks;
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

        [SerializeField, Tooltip("API key for the endpoint")]
        private string _APIKey = "";
        [SerializeField, Tooltip("Request actions from the backend")]
        private bool _requestActions = true;
        [SerializeField, Tooltip("Request evaluation from the backend")]
        private bool _requestEvaluation = true;

        [SerializeField, Tooltip("Request transcription from the backend")]
        private bool _requestTranscription = true;

        [SerializeField, Tooltip("Request file url from the backend")]
        private bool _requestFileUrl = false; // only set to true if you need to send the file somewhere else

        [SerializeField, Tooltip("Request rule violations from the backend")]
        private bool _requestRuleViolations = false;

        private string _baseEndpointUrl = "https://staypro.hello-4d0.workers.dev"; // DO NOT CHANGE UNLESS YOU KNOW WHAT YOU ARE DOING

        // Endpoint to upload voice chunks for moderation
        private string _moderationsEndpointPath = "/api/v1/moderations"; // DO NOT CHANGE UNLESS YOU KNOW WHAT YOU ARE DOING

        // Endpoint to get JWT session token: this is used to authenticate the player with the backend
        private string _sessionsEndpointPath = "/api/v1/sessions"; // DO NOT CHANGE UNLESS YOU KNOW WHAT YOU ARE DOING

        private IRecordingSource _recordingSource;

        // Start is called once before the first execution of Update after the MonoBehaviour is created
        async void Start()
        {
            await Initialize();
        }

        async Task Initialize()
        {
            if (_proManager == null)
            {
                Debug.LogError("PROConfig: PROManager is not assigned");
                return;
            }
            await _proManager.Initialize(
                CanRecord,
                GetMetadata,
                _chunkDurationSeconds,
                _baseEndpointUrl,
                _moderationsEndpointPath,
                _sessionsEndpointPath,
                _APIKey,
                _requestActions,
                _requestFileUrl,
                _requestEvaluation,
                _requestTranscription,
                _requestRuleViolations
            ); // MUST CALL
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
