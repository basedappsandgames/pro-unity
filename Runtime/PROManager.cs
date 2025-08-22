/// LICENSE WILD WEST LABS, INC: APACHE 2.0 [https://opensource.org/license/apache-2-0]
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Wildwest.Pro
{
    public enum PRORecordingSource
    {
        Normcore,
        Photon,
        Microphone,
    }

    /// <summary>
    /// Listens to Normcore's RealtimeAvatarVoice stream, collects X-second chunks,
    /// converts them to 16-bit PCM WAV, and uploads them to the backend via Database.UploadVoiceChunk.
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

        public PRORecordingSource RecordingSource { private get; set; }

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

        public async Task Initialize(
            Func<bool> canRecord,
            Func<AudioEventMetadata> getMetadata,
            int chunkDurationSec,
            string endpointUrl,
            string moderationsEndpointPath,
            string sessionsEndpointPath,
            string apiKey
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
            _chunkDurationSeconds = chunkDurationSec;
            // must set these before requesting session token
            EndpointUrl = endpointUrl;
            ModerationsEndpointPath = moderationsEndpointPath;
            SessionsEndpointPath = sessionsEndpointPath;
            ApiKey = apiKey;
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
            (bool flagged, PROModerationData[] data, string errorMessage) = await UploadVoiceChunk(
                ApiKey,
                _sessionToken,
                EndpointUrl + ModerationsEndpointPath,
                metadata.UserId,
                wavBytes,
                metadata.RoomId
            );
            if (flagged)
            {
                Debug.LogError(
                    "[PRO] Chunk flagged: " + string.Join(", ", data.SelectMany(d => d.s))
                );
            }
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
            Debug.LogError("API KEY: " + ApiKey);
            var payloadDict = new SessionTokenRequest { user_id = GetMetadata().UserId };
            string jsonPayload = JsonConvert.SerializeObject(payloadDict);
            Debug.LogError($"{EndpointUrl}{SessionsEndpointPath}");
            using UnityWebRequest request = new($"{EndpointUrl}{SessionsEndpointPath}", "POST");
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
            public PROModerationAnalysis[] results;
            public ErrorResponse error;
        }

        public class PROModerationAnalysis
        {
            public PROModerationData a;
        }

        public class PROModerationData
        {
            public bool hs; // has hate speech?
            public float chs; // confidence in hate speech
            public string[] s; // list of slurs
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
            bool flagged,
            PROModerationData[] data,
            string errorMessage
        )> UploadVoiceChunk(
            string apiKey,
            string sessionToken,
            string url,
            string userId,
            byte[] wavBytes,
            string roomId
        )
        {
            if (wavBytes == null || wavBytes.Length == 0)
            {
                return (false, new PROModerationData[0], "Empty buffer");
            }

            // Build multipart/form-data payload that matches the new moderation API.
            // Construct the required "metadata" JSON describing the attached media parts.
            string partName = "audio"; // Name referenced by metadata.items[0].part
            string metadataJson = JsonConvert.SerializeObject(
                new
                {
                    user_id = userId, // must be the meta user id
                    room_id = roomId,
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
            await request.SendWebRequest();
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
                return (false, new PROModerationData[0], errorMessage);
            }
            else
            {
                if (
                    proModerationResponse?.results != null
                    && proModerationResponse.results.Length > 0
                )
                {
                    // if any of the results a.hs is true, return true
                    return (
                        proModerationResponse.results.Any(result => result.a.hs),
                        proModerationResponse.results.Select(result => result.a).ToArray(),
                        null
                    );
                }
                else if (proModerationResponse?.error != null)
                {
                    string errorMessage = proModerationResponse.error.message;
                    Debug.LogError(
                        $"[PRO] request returned HTTP {request.responseCode} but contained an error: {errorMessage}. Raw Response: {responseData}"
                    );
                    return (false, new PROModerationData[0], errorMessage);
                }
                else
                {
                    string errorMessage = "Unexpected response structure";
                    Debug.LogError(
                        $"[PRO] response has unexpected structure (HTTP {request.responseCode}) despite success. Raw response: {responseData}"
                    );
                    return (false, new PROModerationData[0], errorMessage);
                }
            }
        }
    }
}
