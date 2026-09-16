#include <Windows.h>
#include <Audioclient.h>
#include <Mmdeviceapi.h>
#include <Wrl/client.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <mutex>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;

#define LOOPBACK_EXPORT extern "C" __declspec(dllexport)

namespace {

constexpr int kSampleRate = 48000;
constexpr int kMaximumBufferedSamples = kSampleRate;

class WasapiLoopback {
 public:
  WasapiLoopback() : samples_(kMaximumBufferedSamples) {
    worker_ = std::thread(&WasapiLoopback::CaptureLoop, this);
  }

  ~WasapiLoopback() {
    stopping_.store(true);
    HANDLE event_handle = sample_event_.load();
    if (event_handle) SetEvent(event_handle);
    if (worker_.joinable()) worker_.join();
  }

  bool WaitUntilReady() {
    std::unique_lock<std::mutex> lock(initialization_mutex_);
    if (!initialization_condition_.wait_for(
            lock, std::chrono::seconds(3),
            [this] { return initialization_finished_; })) {
      return false;
    }
    return SUCCEEDED(initialization_result_);
  }

  int Read(float* output, int sample_count) {
    if (!output || sample_count <= 0) return -1;

    std::lock_guard<std::mutex> lock(samples_mutex_);
    if (buffered_samples_ < static_cast<size_t>(sample_count)) return 0;

    for (int i = 0; i < sample_count; ++i) {
      output[i] = samples_[read_position_];
      read_position_ = (read_position_ + 1) % samples_.size();
    }
    buffered_samples_ -= sample_count;
    return sample_count;
  }

  int BufferedSamples() const {
    std::lock_guard<std::mutex> lock(samples_mutex_);
    return static_cast<int>(buffered_samples_);
  }

  int StreamLatencyMilliseconds() const {
    return stream_latency_milliseconds_.load();
  }

 private:
  void FinishInitialization(HRESULT result) {
    {
      std::lock_guard<std::mutex> lock(initialization_mutex_);
      initialization_result_ = result;
      initialization_finished_ = true;
    }
    initialization_condition_.notify_one();
  }

  void CaptureLoop() {
    HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool uninitialize_com = SUCCEEDED(result);
    if (result == RPC_E_CHANGED_MODE) result = S_OK;
    if (FAILED(result)) {
      FinishInitialization(result);
      return;
    }

    ComPtr<IMMDeviceEnumerator> enumerator;
    result = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr,
                              CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
    if (FAILED(result)) {
      FinishInitialization(result);
      if (uninitialize_com) CoUninitialize();
      return;
    }

    ComPtr<IMMDevice> device;
    // Unity follows the Windows default output endpoint, whose general-use
    // role is eConsole. The communications role may point at a headset instead.
    result = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);
    if (FAILED(result)) {
      FinishInitialization(result);
      if (uninitialize_com) CoUninitialize();
      return;
    }

    ComPtr<IAudioClient> audio_client;
    result = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                              reinterpret_cast<void**>(audio_client.GetAddressOf()));
    if (FAILED(result)) {
      FinishInitialization(result);
      if (uninitialize_com) CoUninitialize();
      return;
    }

    WAVEFORMATEX format{};
    format.wFormatTag = WAVE_FORMAT_IEEE_FLOAT;
    format.nChannels = 1;
    format.nSamplesPerSec = kSampleRate;
    format.wBitsPerSample = 32;
    format.nBlockAlign = format.nChannels * format.wBitsPerSample / 8;
    format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;

    constexpr DWORD stream_flags =
        AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
        AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
        AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    result = audio_client->Initialize(AUDCLNT_SHAREMODE_SHARED, stream_flags,
                                      0, 0, &format, nullptr);
    if (FAILED(result)) {
      FinishInitialization(result);
      if (uninitialize_com) CoUninitialize();
      return;
    }

    REFERENCE_TIME stream_latency = 0;
    if (SUCCEEDED(audio_client->GetStreamLatency(&stream_latency))) {
      stream_latency_milliseconds_.store(
          static_cast<int>(stream_latency / 10000));
    }

    HANDLE event_handle = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!event_handle) {
      FinishInitialization(HRESULT_FROM_WIN32(GetLastError()));
      if (uninitialize_com) CoUninitialize();
      return;
    }
    sample_event_.store(event_handle);

    result = audio_client->SetEventHandle(event_handle);
    ComPtr<IAudioCaptureClient> capture_client;
    if (SUCCEEDED(result)) {
      result = audio_client->GetService(IID_PPV_ARGS(&capture_client));
    }
    if (SUCCEEDED(result)) result = audio_client->Start();

    FinishInitialization(result);
    if (SUCCEEDED(result)) {
      while (!stopping_.load()) {
        WaitForSingleObject(event_handle, 100);
        if (stopping_.load()) break;

        UINT32 packet_frames = 0;
        while (SUCCEEDED(capture_client->GetNextPacketSize(&packet_frames)) &&
               packet_frames > 0) {
          BYTE* data = nullptr;
          UINT32 frame_count = 0;
          DWORD flags = 0;
          result = capture_client->GetBuffer(&data, &frame_count, &flags,
                                              nullptr, nullptr);
          if (FAILED(result)) break;

          WriteSamples(reinterpret_cast<const float*>(data), frame_count,
                       (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0);
          capture_client->ReleaseBuffer(frame_count);
        }
      }
      audio_client->Stop();
    }

    sample_event_.store(nullptr);
    CloseHandle(event_handle);
    if (uninitialize_com) CoUninitialize();
  }

  void WriteSamples(const float* input, size_t sample_count, bool silent) {
    std::lock_guard<std::mutex> lock(samples_mutex_);
    for (size_t i = 0; i < sample_count; ++i) {
      if (buffered_samples_ == samples_.size()) {
        read_position_ = (read_position_ + 1) % samples_.size();
        --buffered_samples_;
      }

      samples_[write_position_] = silent ? 0.0f : std::clamp(input[i], -1.0f, 1.0f);
      write_position_ = (write_position_ + 1) % samples_.size();
      ++buffered_samples_;
    }
  }

  mutable std::mutex samples_mutex_;
  std::vector<float> samples_;
  size_t read_position_ = 0;
  size_t write_position_ = 0;
  size_t buffered_samples_ = 0;

  std::thread worker_;
  std::atomic<bool> stopping_{false};
  std::atomic<HANDLE> sample_event_{nullptr};
  std::atomic<int> stream_latency_milliseconds_{0};

  std::mutex initialization_mutex_;
  std::condition_variable initialization_condition_;
  bool initialization_finished_ = false;
  HRESULT initialization_result_ = E_FAIL;
};

}  // namespace

LOOPBACK_EXPORT void* unity_wasapi_loopback_create() {
  try {
    auto* loopback = new WasapiLoopback();
    if (!loopback->WaitUntilReady()) {
      delete loopback;
      return nullptr;
    }
    return loopback;
  } catch (...) {
    return nullptr;
  }
}

LOOPBACK_EXPORT void unity_wasapi_loopback_destroy(void* state) {
  delete static_cast<WasapiLoopback*>(state);
}

LOOPBACK_EXPORT int unity_wasapi_loopback_read(
    void* state, float* output, int sample_count) {
  if (!state) return -1;
  return static_cast<WasapiLoopback*>(state)->Read(output, sample_count);
}

LOOPBACK_EXPORT int unity_wasapi_loopback_buffered_samples(void* state) {
  if (!state) return 0;
  return static_cast<WasapiLoopback*>(state)->BufferedSamples();
}

LOOPBACK_EXPORT int unity_wasapi_loopback_latency_ms(void* state) {
  if (!state) return 0;
  return static_cast<WasapiLoopback*>(state)->StreamLatencyMilliseconds();
}
