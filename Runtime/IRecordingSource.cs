/// LICENSE WILD WEST LABS, INC: APACHE 2.0 [https://opensource.org/license/apache-2-0]
namespace Wildwest.Pro
{
    public interface IRecordingSource
    {
        PROManager ProManager { get; set; }
        bool CanRecord();
        void Initialize(PROManager proManager, int chunkDurationSec);
        void StartRecording();
        void StopRecording();
        void Dispose();
    }
}
