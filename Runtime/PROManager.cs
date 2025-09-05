/// LICENSE WILD WEST LABS, INC: APACHE 2.0 [https://opensource.org/license/apache-2-0]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Wildwest.Pro
{
    /// <summary>
    /// Listens to a recording source, collects X-second chunks,
    /// converts them to 16-bit PCM WAV, and uploads them to the backend via UploadVoiceChunk.
    /// </summary>
    public class PROManager : MonoBehaviour
    {
        [Header("Audio Processing")]
        [SerializeField]
        private AudioChunkerBehaviour Chunker;

        private int _chunkDurationSeconds;

        // Delegates set by game code to control recording and provide metadata
        public Func<bool> CanRecord { private get; set; }
        public Func<AudioEventMetadata> GetMetadata { private get; set; }
        public Action<PROModerationResult[], string> OnData { private get; set; }

        private string EndpointUrl;
        private string ModerationsEndpointPath;
        private string SessionsEndpointPath;
        private string ApiKey; // optional if you use your own server to get a PRO JWT and SetSessionToken

        private string _sessionToken;
        private bool _isInitialized = false;

        // default to not sending back any fields to client
        private bool _requestFileUrl = false;
        private bool _requestSafetyScores = false;
        private bool _requestActions = false;
        private bool _requestTranscription = false;
        private bool _requestRuleViolations = false;

        private bool loggedFirstChunk = false;

        public async Task Initialize(
            Func<bool> canRecord,
            Func<AudioEventMetadata> getMetadata,
            Action<PROModerationResult[], string> onData,
            int chunkDurationSec,
            string endpointUrl,
            string moderationsEndpointPath,
            string sessionsEndpointPath,
            string apiKey,
            bool requestActions = true,
            bool requestFileUrl = false,
            bool requestEvaluation = false,
            bool requestTranscription = false,
            bool requestRuleViolations = false
        )
        {
            if (_isInitialized)
            {
                Debug.LogWarning(
                    "PROManager: Already initialized, you should not be calling this method multiple times"
                );
            }
            if (apiKey == null || apiKey.Length == 0)
            {
                Debug.LogError("PROManager: API key is not set");
                return;
            }
            CanRecord = canRecord;
            GetMetadata = getMetadata;
            OnData = onData;
            SetChunkDurationSeconds(chunkDurationSec);
            // must set these before requesting session token
            EndpointUrl = endpointUrl;
            ModerationsEndpointPath = moderationsEndpointPath;
            SessionsEndpointPath = sessionsEndpointPath;
            ApiKey = apiKey;
            _requestFileUrl = requestFileUrl;
            _requestSafetyScores = requestEvaluation;
            _requestActions = requestActions;
            _requestTranscription = requestTranscription;
            _requestRuleViolations = requestRuleViolations;
            var sessionTokenResponse = await RequestSessionToken();
            _sessionToken = sessionTokenResponse.jwt;
            _isInitialized = true;
        }

        // Used your own server to get the PRO JWT for a more secure authentication?
        // Use this method instead of Initialize to pass in the session token
        public async Task InitializeWithSessionToken(
            Func<bool> canRecord,
            Func<AudioEventMetadata> getMetadata,
            Action<PROModerationResult[], string> onData,
            int chunkDurationSec,
            string endpointUrl,
            string moderationsEndpointPath,
            string sessionsEndpointPath,
            string sessionToken,
            bool requestActions = true,
            bool requestFileUrl = false,
            bool requestEvaluation = false,
            bool requestTranscription = false,
            bool requestRuleViolations = false
        )
        {
            if (_isInitialized)
            {
                Debug.LogWarning(
                    "PROManager: Already initialized, you should not be calling this method multiple times"
                );
            }
            if (sessionToken == null || sessionToken.Length == 0)
            {
                Debug.LogError("PROManager: Session token is not set");
                return;
            }
            CanRecord = canRecord;
            GetMetadata = getMetadata;
            OnData = onData;
            SetChunkDurationSeconds(chunkDurationSec);
            EndpointUrl = endpointUrl;
            ModerationsEndpointPath = moderationsEndpointPath;
            SessionsEndpointPath = sessionsEndpointPath;
            _requestFileUrl = requestFileUrl;
            _requestSafetyScores = requestEvaluation;
            _requestActions = requestActions;
            _requestTranscription = requestTranscription;
            _requestRuleViolations = requestRuleViolations;
            _sessionToken = sessionToken;
        }

        public void SetApiKey(string apiKey)
        {
            ApiKey = apiKey;
            CheckSetInitialized();
        }

        public void SetCanRecord(Func<bool> canRecord)
        {
            CanRecord = canRecord;
            CheckSetInitialized();
        }

        public void SetGetMetadata(Func<AudioEventMetadata> getMetadata)
        {
            GetMetadata = getMetadata;
            CheckSetInitialized();
        }

        public void SetOnData(Action<PROModerationResult[], string> onData)
        {
            OnData = onData;
            CheckSetInitialized();
        }

        public void SetChunkDurationSeconds(int chunkDurationSeconds)
        {
            _chunkDurationSeconds = chunkDurationSeconds;
            Chunker.Configure(chunkDurationSeconds);
            CheckSetInitialized();
        }

        // [Deprecated]
        public void SetSampleRate(int sampleRate)
        {
            Chunker.Configure(_chunkDurationSeconds, sampleRate, null);
        }

        // [Deprecated]
        public void SetChannelCount(int channelCount)
        {
            Chunker.Configure(_chunkDurationSeconds, null, channelCount);
        }

        public void SetEndpointUrl(string endpointUrl)
        {
            EndpointUrl = endpointUrl;
            CheckSetInitialized();
        }

        public void SetModerationsEndpointPath(string moderationsEndpointPath)
        {
            ModerationsEndpointPath = moderationsEndpointPath;
            CheckSetInitialized();
        }

        public void SetSessionsEndpointPath(string sessionsEndpointPath)
        {
            SessionsEndpointPath = sessionsEndpointPath;
            CheckSetInitialized();
        }

        public void SetSessionToken(string sessionToken)
        {
            _sessionToken = sessionToken;
            CheckSetInitialized();
        }

        private void CheckSetInitialized()
        {
            bool hasCanRecord = CanRecord != null;
            bool hasGetMetadata = GetMetadata != null;
            bool hasOnData = OnData != null;
            bool hasChunkDurationSeconds = _chunkDurationSeconds != 0;
            bool hasEndpointUrl = EndpointUrl != null;
            bool hasModerationsEndpointPath = ModerationsEndpointPath != null;
            bool hasSessionsEndpointPath = SessionsEndpointPath != null;
            bool hasSessionToken = _sessionToken != null;
            if (
                hasGetMetadata
                && hasOnData
                && hasChunkDurationSeconds
                && hasEndpointUrl
                && hasModerationsEndpointPath
                && hasSessionsEndpointPath
                && hasSessionToken
            )
            {
                _isInitialized = true;
            }
        }

        // called from IRecordingSource to fill the buffer with voice data
        public void OnVoiceData(float[] frame)
        {
            if (!_isInitialized)
            {
                Debug.LogWarning(
                    "[PRO] PROManager: Not initialized. Are you sure you initialized all required fields?"
                );
            }

            // Respect caller's policy
            if (CanRecord != null && !CanRecord())
            {
                return;
            }

            if (frame == null || frame.Length == 0)
            {
                return;
            }

            if (!loggedFirstChunk)
            {
                Debug.Log($"<color=green>[PRO] Processing first chunk of audio data</color>");
                loggedFirstChunk = true;
            }

            if (Chunker.TryAppend(frame, out float[] completeChunk))
            {
                ProcessChunk(completeChunk);
            }
        }

        public async Task ProcessChunk(float[] chunk)
        {
            if (_sessionToken == null)
            {
                Debug.LogError(
                    "[PRO] UploadVoiceChunk called without a session token – skipping upload. Did you call Initialize or SetSessionToken?"
                );
                return;
            }

            // Convert to 16kHz WAV & silence check
            byte[] wavBytes = Chunker.Encode(chunk);
            if (wavBytes == null || wavBytes.Length == 0)
            {
                return;
            }

            var metadata = GetMetadata();
            (PROModerationResult[] data, string errorMessage) = await UploadVoiceChunk(
                ApiKey,
                _sessionToken,
                EndpointUrl + ModerationsEndpointPath,
                metadata.UserId,
                wavBytes,
                metadata.RoomId,
                _requestActions,
                _requestFileUrl,
                _requestSafetyScores,
                _requestTranscription,
                _requestRuleViolations
            );
            if (errorMessage != null)
            {
                Debug.LogError(
                    "<color=red>[PRO] Error uploading chunk: " + errorMessage + "</color>"
                );
            }
            OnData(data, errorMessage); // call the OnData delegate with the moderation results
        }

        public struct AudioEventMetadata
        {
            public string UserId;
            public string RoomId;
            public string Language;
        }

        // called by a RecordingSource to set the sample rate and channels based on the input device
        public void ConfigureChunker(int sampleRate, int channelCount)
        {
            Chunker.Configure(_chunkDurationSeconds, sampleRate, channelCount);
        }

        private class SessionTokenRequest
        {
            public string user_id;
        }

        private class SessionTokenResponse
        {
            public string jwt;
        }

        private async Task<SessionTokenResponse> RequestSessionToken()
        {
            var payloadDict = new SessionTokenRequest { user_id = GetMetadata().UserId };
            string jsonPayload = JsonConvert.SerializeObject(payloadDict);
            using UnityWebRequest request = new UnityWebRequest(
                $"{EndpointUrl}{SessionsEndpointPath}",
                "POST"
            );
            request.SetRequestHeader("Authorization", "Bearer " + ApiKey);
            byte[] jsonToSend = new System.Text.UTF8Encoding().GetBytes(jsonPayload);
            request.uploadHandler = new UploadHandlerRaw(jsonToSend);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            try
            {
                var asyncOp = request.SendWebRequest();
                while (!asyncOp.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError(
                        $"[PRO] Session token request error: {request.error} - Response: {request.downloadHandler?.text}"
                    );
                    throw new Exception($"Session token request failed: {request.error}");
                }
                else
                {
                    string responseData = request.downloadHandler.text;
                    try
                    {
                        SessionTokenResponse tokenData =
                            JsonConvert.DeserializeObject<SessionTokenResponse>(responseData);

                        // Data validation moved here, but state update remains in IdentityManager
                        if (tokenData?.jwt == null)
                        {
                            Debug.LogError(
                                $"[PRO] Session token response parsing error: 'data' field is null. Response: {responseData}"
                            );
                            throw new Exception("Authentication failed: Invalid response data.");
                        }

                        return tokenData;
                    }
                    catch (JsonException jsonEx)
                    {
                        Debug.LogError(
                            $"[PRO] Session token response JSON parsing error: {jsonEx.Message}. Response: {responseData}"
                        );
                        throw new Exception(
                            "Authentication failed: Could not parse server response.",
                            jsonEx
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[PRO] An exception occurred during the session token request: {ex}"
                );
                throw;
            }
        }

        public class ErrorResponse
        {
            public string message;
            public string code;
        }

        public class PROModerationResponse
        {
            public PROModerationResponseItem[] results;
            public ErrorResponse error;
        }

        public class PROModerationResponseItem
        {
            public PROModerationAnalysis a;
            public string fileUrl;
            public string id;
            public string type; // "audio" | "text" | "image" | "video"
            public PROActionData[] act; // actions to take
            public string[] rv; // list of rule violations
        }

        public class PROModerationAnalysis
        {
            public bool hs; // has hate speech?
            public float chs; // confidence in hate speech
            public string[] s; // list of slurs
            public string ta; // transcribed audio
            public Dictionary<string, double> e; // 6 categories of safety scores
        }

        public class PROModerationResult
        {
            public Dictionary<string, double> SafetyScores;
            public string Transcription;
            public string FileUrl;
            public string[] RulesViolated;
            public PROActionData[] Actions;
        }

        public enum PROAction
        {
            None,
            Timeout,
            Kick,
            Strike,
            Ban,
            Custom,
        }

        public class PROActionData
        {
            public PROAction Action;
            public int CustomNumber;
            public string CustomString;
        }

        /// <summary>
        /// Uploads a 16-bit PCM WAV byte array as multipart/form-data to the PRO voice moderation
        /// endpoint. The payload matches the format expected by PROManager so the backend can
        /// handle it in a uniform way.
        /// </summary>
        /// <param name="wavBytes">Raw WAV file bytes (including header).</param>
        /// <param name="userId">Player userId (sent as form field "userId").</param>
        /// <param name="roomId">Room identifier (sent as form field "roomId").</param>
        /// <param name="sessionToken">Session token used for request authentication.</param>
        /// <returns>Tuple containing flagged status and error message (null if successful).</returns>
        public static async Task<(
            PROModerationResult[] results,
            string errorMessage
        )> UploadVoiceChunk(
            string apiKey,
            string sessionToken,
            string url,
            string userId,
            byte[] wavBytes,
            string roomId,
            bool requestActions,
            bool requestFileUrl,
            bool requestEvaluation, // show safety scores in response
            bool requestTranscription,
            bool requestRuleViolations
        )
        {
            if (wavBytes == null || wavBytes.Length == 0)
            {
                return (new PROModerationResult[0], "Empty buffer");
            }

            if ((sessionToken == null || sessionToken.Length == 0))
            {
                Debug.LogError("Must send a session token to the PROManager");
                return (new PROModerationResult[0], "Must send a session token to the PROManager");
            }

            // Build multipart/form-data payload that matches the new moderation API.
            // Construct the required "metadata" JSON describing the attached media parts.
            string partName = "audio"; // Name referenced by metadata.items[0].part
            string metadataJson = JsonConvert.SerializeObject(
                new
                {
                    user_id = userId, // must be a unique user id
                    room_id = roomId,
                    request_file_url = requestFileUrl,
                    request_evaluation = requestEvaluation,
                    request_actions = requestActions,
                    request_transcription = requestTranscription,
                    request_rule_violations = requestRuleViolations,
                    items = new[]
                    {
                        new
                        {
                            id = "audio_1",
                            type = "audio",
                            mime = "audio/wav",
                            part = partName,
                        },
                    },
                }
            );

            WWWForm form = new WWWForm();
            // Add the metadata part with explicit application/json content type.
            form.AddBinaryData(
                "metadata",
                System.Text.Encoding.UTF8.GetBytes(metadataJson),
                "metadata.json",
                "application/json"
            );
            // Add the audio binary payload using the part name declared above.
            form.AddBinaryData(partName, wavBytes, "audio.wav", "audio/wav");

            using UnityWebRequest request = UnityWebRequest.Post(url, form);

            request.SetRequestHeader("Authorization", "Bearer " + sessionToken);
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.SetRequestHeader("x-api-key", apiKey);
            }
            var asyncOpUpload = request.SendWebRequest();
            while (!asyncOpUpload.isDone)
            {
                await Task.Yield();
            }
            string responseData = request.downloadHandler?.text;
            PROModerationResponse proModerationResponse = null;
            try
            {
                if (!string.IsNullOrEmpty(responseData))
                {
                    proModerationResponse = JsonConvert.DeserializeObject<PROModerationResponse>(
                        responseData
                    );
                }
            }
            catch (JsonException jsonEx)
            {
                Debug.LogError(
                    $"[PRO] Error parsing PRO moderation response JSON: {jsonEx.Message}. Raw Response: {responseData}"
                );
            }

            if (request.result != UnityWebRequest.Result.Success || request.responseCode >= 400)
            {
                string serverErrorMessage = proModerationResponse?.error?.message;
                string serverErrorCode = proModerationResponse?.error?.code;

                string errorMessage;
                if (!string.IsNullOrEmpty(serverErrorMessage))
                {
                    errorMessage = serverErrorMessage;
                    Debug.LogError(
                        $"[PRO] server error (HTTP {request.responseCode}): {serverErrorMessage} (Code: {serverErrorCode})"
                    );
                }
                else
                {
                    errorMessage = $"Network error: {request.error}";
                    Debug.LogError(
                        $"[PRO] network/HTTP error: {request.error} - Status Code: {request.responseCode}. Raw Response: {responseData}"
                    );
                }
                return (new PROModerationResult[0], errorMessage);
            }
            else
            {
                if (
                    proModerationResponse?.results != null
                    && proModerationResponse.results.Length > 0
                )
                {
                    return (
                        proModerationResponse
                            .results.Select(result => new PROModerationResult
                            {
                                SafetyScores = result.a.e, // only shows if request_evaluation (requestSafetyScores) is true
                                Transcription = result.a.ta, // only shows if request_transcription is true
                                FileUrl = result.fileUrl, // only shows if request_file_url is true
                                RulesViolated = result.rv, // only shows if request_rule_violations is true
                                Actions = result.act, // only shows if request_actions is true
                            })
                            .ToArray(),
                        null
                    );
                }
                else if (proModerationResponse?.error != null)
                {
                    string errorMessage = proModerationResponse.error.message;
                    Debug.LogError(
                        $"[PRO] request returned HTTP {request.responseCode} but contained an error: {errorMessage}. Raw Response: {responseData}"
                    );
                    return (new PROModerationResult[0], errorMessage);
                }
                else
                {
                    string errorMessage = "Unexpected response structure";
                    Debug.LogError(
                        $"[PRO] response has unexpected structure (HTTP {request.responseCode}) despite success. Raw response: {responseData}"
                    );
                    return (new PROModerationResult[0], errorMessage);
                }
            }
        }
    }
}
