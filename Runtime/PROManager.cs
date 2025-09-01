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
        [
            SerializeField,
            Tooltip(
                "Silence threshold (absolute sample value); chunk is skipped if all samples <= threshold."
            )
        ]
        private float _silenceThreshold = 0.021f;

        private int _chunkDurationSeconds;

        // Delegates set by game code to control recording and provide metadata
        public Func<bool> CanRecord { private get; set; }
        public Func<AudioEventMetadata> GetMetadata { private get; set; }
        public Action<PROModerationResult[], string> OnData { private get; set; }

        // Internal buffers
        private float[] _chunkBuffer;
        private int _sampleIndex;
        private int _samplesPerChunk;
        private int _sampleRate = 16000;
        private int _channelCount = 1;

        private const int TargetSampleRate = 16000;
        private int _downsampleFactor = 1;

        private readonly MemoryStream _reusableStream = new MemoryStream();

        private string EndpointUrl;
        private string ModerationsEndpointPath;
        private string SessionsEndpointPath;
        private string ApiKey;

        private string _sessionToken;
        private DateTime _sessionTokenExpiresAt;
        private bool _isInitialized = false;

        private bool _requestFileUrl = false;
        private bool _requestSafetyScores = false;
        private bool _requestActions = false;
        private bool _requestTranscription = false;
        private bool _requestRuleViolations = false;

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
                Debug.LogError("PROManager: Already initialized");
                return;
            }
            if (apiKey == null || apiKey.Length == 0)
            {
                Debug.LogError("PROManager: API key is not set");
                return;
            }
            CanRecord = canRecord;
            GetMetadata = getMetadata;
            OnData = onData;
            _chunkDurationSeconds = chunkDurationSec;
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
            _sessionTokenExpiresAt = DateTime.UtcNow.AddSeconds(4 * 60 * 60); // 4 hours
            _isInitialized = true;
        }

        public void OnVoiceData(float[] frame)
        {
            if (!_isInitialized)
            {
                Debug.LogError(
                    "[PRO] PROManager: Not initialized. Are you sure you called Initialize()?"
                );
                return;
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

            // Initialise buffers on first callback
            if (_chunkBuffer == null)
            {
                InitialiseWithFrame();
            }

            // Copy into rolling buffer
            AppendChunks(frame);
        }

        public void AppendChunks(float[] frame)
        {
            if (_chunkBuffer == null)
            {
                return;
            }
            int copyLen = Mathf.Min(frame.Length, _chunkBuffer.Length - _sampleIndex);
            Array.Copy(frame, 0, _chunkBuffer, _sampleIndex, copyLen);
            _sampleIndex += copyLen;

            // If we over-filled (should only happen if last frame straddles boundary)
            if (_sampleIndex >= _samplesPerChunk)
            {
                _ = ProcessChunk();
                _sampleIndex = 0; // start next chunk fresh
            }
        }

        private void InitialiseWithFrame()
        {
            Debug.Log("<color=green>[PRO] PROManager: Started receiving audio data</color>");
            // Always initialize the buffer based on current settings
            _chunkBuffer = new float[_samplesPerChunk];
            _sampleIndex = 0;
        }

        private async Task ProcessChunk()
        {
            if (_sessionToken == null)
            {
                Debug.LogError(
                    "[PRO] UploadVoiceChunk called without a session token – skipping upload."
                );
                return;
            }
            if (DateTime.UtcNow > _sessionTokenExpiresAt)
            {
                Debug.LogError("[PRO] Session token expired, cannot send chunk");
                return;
            }

            // Convert to WAV & silence check
            var (wavBytes, tooQuiet) = PROAudioUtil.ConvertDownsampleAndWav(
                _chunkBuffer,
                _sampleRate,
                _downsampleFactor,
                _silenceThreshold,
                _channelCount,
                TargetSampleRate,
                _reusableStream
            );
            if (tooQuiet)
            {
                return; // skip upload
            }
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
            OnData(data, errorMessage);
        }

        public struct AudioEventMetadata
        {
            public string UserId;
            public string RoomId;
            public string Language;
        }

        public void SetChannelCount(int channelCount)
        {
            _channelCount = channelCount;
        }

        public void SetSampleRate(int sampleRate)
        {
            _sampleRate = sampleRate;
            _downsampleFactor = Mathf.Max(1, _sampleRate / TargetSampleRate);
            // Recalculate chunk size based on the new sample rate
            _samplesPerChunk = _sampleRate * _chunkDurationSeconds * _channelCount;
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
            bool requestEvaluation,
            bool requestTranscription,
            bool requestRuleViolations
        )
        {
            if (wavBytes == null || wavBytes.Length == 0)
            {
                return (new PROModerationResult[0], "Empty buffer");
            }

            // Build multipart/form-data payload that matches the new moderation API.
            // Construct the required "metadata" JSON describing the attached media parts.
            string partName = "audio"; // Name referenced by metadata.items[0].part
            string metadataJson = JsonConvert.SerializeObject(
                new
                {
                    user_id = userId, // must be the meta user id
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
            request.SetRequestHeader("x-api-key", apiKey);
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
                                SafetyScores = result.a.e,
                                Transcription = result.a.ta,
                                FileUrl = result.fileUrl,
                                RulesViolated = result.rv,
                                Actions = result.act,
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
