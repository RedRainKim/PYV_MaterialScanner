using NLog;
using OpenCvSharp;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net;
using System.Net.Http;
using System.Text;

namespace PYV_MaterialScanner
{
    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        // app.config
        private readonly string RTSP_URL = ConfigurationManager.AppSettings["CameraRTSPUrl"] ?? "rtsp://default:url@0.0.0.0/stream";
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const string targetFolder = "logs"; // capture image folder

        // Alarm sound
        private readonly string QrScanSoundFile = "dingdong.wav";
        private readonly SoundPlayer? _soundPlayer;

        // H-Beam Tracker
        private readonly HbeamTracker _hbeamTracker = new HbeamTracker();

        private VideoCapture _capture = new();
        private bool _isCapturing = false;
        private DateTime _lastQrScanTime = DateTime.MinValue;
        private readonly TimeSpan _qrScanInterval = TimeSpan.FromMilliseconds(300); // QR scan interval

        // Last scaned QR code point
        private OpenCvSharp.Point[]? _lastQrPoints = null;
        private string? _lastQrText;
        private DateTime _lastQrDetectionTime = DateTime.MinValue;
        private readonly TimeSpan _qrDisplayDuration = TimeSpan.FromSeconds(5); // QR code box display duration

        // Reusable WriteableBitmap for better performance
        private WriteableBitmap? _writeableBitmap;
        private readonly object _bitmapLock = new object();

        // 렌더링 상태 플래그 (volatile: 스레드 간 가시성 보장)**
        private volatile bool _isRendering = false;

        // 메모리 재사용을 위한 버퍼**
        private byte[] _renderBuffer;

        // UI Update throttling
        private DateTime _lastUiUpdate = DateTime.MinValue;
        private readonly TimeSpan _uiUpdateInterval = TimeSpan.FromMilliseconds(33); // ~30 FPS

        // 비동기 검출 상태 플래그 (중복 실행 방지)
        private volatile bool _isDetecting = false;

        // 동적 라벨 Tracking
        private OpenCvSharp.Rect? _lastTrackedLabelRect = null;

        // Hikvision ISAPI 설정값 로드
        private readonly string CAMERA_IP = ConfigurationManager.AppSettings["CameraIP"] ?? "192.168.1.100";
        private readonly string CAMERA_USER = ConfigurationManager.AppSettings["CameraUser"] ?? "admin";
        private readonly string CAMERA_PASS = ConfigurationManager.AppSettings["CameraPass"] ?? "12345";
        private HttpClient? _ptzHttpClient;

        // 줌 제어 커맨드
        public ICommand ZoomInCommand { get; }
        public ICommand ZoomOutCommand { get; }
        public ICommand ZoomStopCommand { get; }

        // UI binding
        private ImageSource? _videoSource;
        public ImageSource? VideoSource
        {
            get => _videoSource;
            set { _videoSource = value; OnPropertyChanged(); }
        }

        private ImageSource? _capturedImageSource;
        public ImageSource? CapturedImageSource
        {
            get => _capturedImageSource;
            set { _capturedImageSource = value; OnPropertyChanged(); }
        }

        private string _cameraStatusText = "Disconnected";
        public string CameraStatusText
        {
            get => _cameraStatusText;
            set { _cameraStatusText = value; OnPropertyChanged(); }
        }

        private Brush _cameraStatusColor = Brushes.Red;
        public Brush CameraStatusColor
        {
            get => _cameraStatusColor;
            set { _cameraStatusColor = value; OnPropertyChanged(); }
        }

        // QR code scanned data
        private string _lastScannedData = "[No QR Code Scanned Yet]";
        public string LastScannedData
        {
            get => _lastScannedData;
            set { _lastScannedData = value; OnPropertyChanged(); }
        }

        // FPS display
        private string _fpsText = "FPS: 0";
        public string FpsText
        {
            get => _fpsText;
            set { _fpsText = value; OnPropertyChanged(); }
        }

        // --------------------------------------------------
        // 스캔 히스토리 (최대 50개)
        // --------------------------------------------------
        // 데이터를 담을 구조체 클래스를 먼저 만들고, 컬렉션의 타입을 해당 클래스로 지정합니다.
        public class ScanRecord
        {
            public string? Time { get; set; }
            public string? Data { get; set; }
        }

        public ObservableCollection<ScanRecord> ScanHistory { get; set; } = new ObservableCollection<ScanRecord>();


        // 명령 (Reconnect Button에 바인딩)
        public ICommand ConnectCommand { get; }

        // 명령 (Capture Button에 바인딩)
        public ICommand CaptureImageCommand { get; }

        // 명령 (Open folder Button에 바인딩)
        public ICommand OpenFolderCommand => new RelayCommand(() =>
        {
            try
            {
                string exePath = AppDomain.CurrentDomain.BaseDirectory;
                string folderPath = Path.Combine(exePath, targetFolder);
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }
                System.Diagnostics.Process.Start("explorer.exe", folderPath);
            }
            catch (Exception ex)
            {
                Log.Error($"폴더 열기 실패: {ex.Message}");
                MessageBox.Show($"폴더 열기 실패: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });

        public string AppTitleWithVersion
        {
            get
            {
                var ver = Assembly.GetEntryAssembly()?.GetName().Version;
                // 예: 3.6.0 형태 (뒤에 0이 붙으면 Major.Minor.Build 까지만 표시)
                string verStr = ver != null ? $"ver_{ver.Major}.{ver.Minor}.{ver.Build}" : "ver_1.0.0";
                return $"PY-Vina RHF Charge Material Scanner {verStr}";
            }
        }

        /// <summary>
        /// MainViewModel Construction
        /// </summary>
        public MainViewModel()
        {
            // start
            Log.Info("====================================================================");
            Log.Info(" Application starting up...");
            Log.Info("====================================================================");

            // *IMPROVED FFMPEG CONFIG FOR LOW LATENCY*
            // rtsp_transport;tcp : 패킷 손실 방지 (필수)
            // buffer_size : 지연 시간을 줄이기 위해 버퍼 크기 축소 (2MB -> 200KB or less logic handled internally by low_delay)
            // fflags=nobuffer : 버퍼링 없이 즉시 디코딩
            // max_delay : 최대 지연 시간 단축 (500000 -> 100000us = 0.1s)
            Environment.SetEnvironmentVariable(
                "OPENCV_FFMPEG_CAPTURE_OPTIONS",
                "rtsp_transport;tcp|fflags;nobuffer|flags;low_delay|probesize;5000000|analyzeduration;2000000|max_delay;100000");

            // 프로그램 시작 시 강제 전체화면 모드 적용
            ApplyFullScreenMode();

            ConnectCommand = new RelayCommand(ExecuteReconnect);

            CaptureImageCommand = new RelayCommand(ExecuteManualCapture);

            // soundplayer
            string soundFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, QrScanSoundFile);
            if (File.Exists(soundFilePath))
            {
                _soundPlayer = new SoundPlayer(soundFilePath);
                _soundPlayer.Load(); // 미리 로드하여 재생 지연 최소화
            }
            else
            {
                Log.Warn($"Sound file not found: {soundFilePath}");
            }

            // Digest / Basic 인증을 자동 처리하는 HttpClient 생성
            var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(CAMERA_USER, CAMERA_PASS)
            };
            _ptzHttpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(2)
            };

            // 줌 커맨드 연결 (비동기 호출)
            ZoomInCommand = new RelayCommand(() => _ = ControlHikvisionZoomAsync("zoomin"));
            ZoomOutCommand = new RelayCommand(() => _ = ControlHikvisionZoomAsync("zoomout"));
            ZoomStopCommand = new RelayCommand(() => _ = ControlHikvisionZoomAsync("stop"));

            // 윈도우 실행 시 자동 연결 시도
            StartStreaming();
        }


        /// <summary>
        /// Main processing loop
        /// </summary>
        private void FrameProcessingLoop()
        {
            // FPS counter variable
            Stopwatch sw = new Stopwatch();
            sw.Start();
            long frameCount = 0;

            // -----------------------------------------------------------------------------------------
            // Get streaming frame
            // -----------------------------------------------------------------------------------------
            Log.Info("Frame processing loop started....");

            while (_isCapturing && _capture.IsOpened())
            {
                DateTime now = DateTime.Now;
                Mat? currentFrame = null;

                // 1. 락을 걸고 가장 최신 프레임만 복사
                lock (_frameLock)
                {
                    if (_newFrameAvailable && _latestFrame != null)
                    {
                        currentFrame = _latestFrame.Clone();
                        _newFrameAvailable = false;
                    }
                }

                // 새 프레임이 없으면 잠시 대기 (CPU 점유율 방지)
                if (currentFrame == null)
                {
                    Thread.Sleep(1);
                    continue;
                }

                try
                {
                    // Collecting captured frame information and FPS display.
                    CollectFrameInformation(currentFrame, sw, ref frameCount);


                    // ------------------------------------------------------------------------------------------------------------------
                    // ASYNC Label Detection Processing
                    // ------------------------------------------------------------------------------------------------------------------
                    bool shouldScan = (now - _lastQrScanTime) >= _qrScanInterval;
                   
                    if (shouldScan && !_isDetecting)
                    {
                        _lastQrScanTime = now;
                        _isDetecting = true; // locking

                        try
                        {
                            // Async task using MAT object,         
                            Mat fullFrameClone = currentFrame.Clone();                               

                            //
                            Task.Run(() =>
                            {
                                try
                                {
                                    // 실시간 하얀색 라벨 트래킹 시각화
                                    //Log.Info("Check Thread speed of Tracking Lable (DetectWhiteLableArea)");
                                    OpenCvSharp.Rect? trackedLabelRoi = _hbeamTracker.DetectWhiteLableArea(fullFrameClone);

                                    if (trackedLabelRoi.HasValue)
                                    {
                                        // 추적된 라벨 영역 오버레이 표시용 업데이트(오프셋 없이 원본 기준 좌표)
                                        OpenCvSharp.Rect labelRect = trackedLabelRoi.Value;

                                        //파란색 박스로 추적된 라벨 영역 그리기
                                        _lastTrackedLabelRect = labelRect;

                                        // 라벨 영역만 타이트하게 크롭하여 디코더에 전달 (속도/인식률 극대화)
                                        using Mat labelCropMat = new Mat(fullFrameClone, labelRect).Clone();

                                        // Scanning (3가지 모드)
                                        //var result = _hbeamTracker.DetectOriginalSizeOnlyEachSteps(labelCropMat);
                                        //var result = _hbeamTracker.DetectUpscaleSizeEachSteps(labelCropMat);
                                        //var result = _hbeamTracker.DetectOriginalPreprocessEachSteps(labelCropMat);
                                        var result = _hbeamTracker.DetectQR(labelCropMat, QrScanMode.Preprocess);

                                        // 결과 처리
                                        if (result.IsDetected && result.DecodedText != null)
                                        {
                                            Mat? uiLabelFrame = result.CroppedLabel.Clone();
                                            var resultPoints = result.ResultPoints;
                                            string? decodedText = result.DecodedText;
                                            string? detectionMethod = result.DetectionMethod;

                                            Application.Current.Dispatcher.Invoke(() =>
                                            {
                                                _lastQrDetectionTime = now;
                                                _lastQrText = decodedText;

                                                // Logging
                                                string ptsLog = resultPoints != null ? string.Join(",", resultPoints.Select(p => $"({p.X},{p.Y})")) : "N/A";
                                                Log.Info($"Label Detected: {detectionMethod} ==> {decodedText} : Pts(local): [{ptsLog}]");

                                                // 3. 좌표 복원 (searchRoi가 사라졌으므로 labelRect 오프셋만 더함)
                                                if (resultPoints != null && resultPoints.Length > 0)
                                                {
                                                    _lastQrPoints = new OpenCvSharp.Point[resultPoints.Length];
                                                    for (int i = 0; i < resultPoints.Length; i++)
                                                    {
                                                        _lastQrPoints[i] = new OpenCvSharp.Point(
                                                            resultPoints[i].X + labelRect.X,
                                                            resultPoints[i].Y + labelRect.Y
                                                        );
                                                    }
                                                }

                                                // UI Update every time a detection occurs
                                                UpdateCapturedImageSafe(uiLabelFrame);

                                                // New detection - 성공적인 스캔 처리 (Save the Acumulated crop image)                                
                                                if (LastScannedData != decodedText)
                                                {
                                                    Log.Info($"[New] Label Detected:(in loop) {decodedText}");
                                                    HandleSuccessfulScan(uiLabelFrame, decodedText, now);
                                                }

                                            });
                                            uiLabelFrame.Dispose();
                                        }
                                        result.Dispose();
                                    }

                                }
                                catch (Exception ex)
                                {
                                    Log.Error($"Async Detection Error: {ex.Message}");
                                }
                                finally
                                {
                                    fullFrameClone.Dispose();                                    
                                    _isDetecting = false; // 잠금 해제
                                }
                            });

                        }
                        catch (Exception ex)
                        {
                            // ROI Crop 시점에서 에러가 나면 비동기 스레드가 아예 실행되지 않으므로 여기서 락을 강제로 풀어줌.
                            Log.Error($"ROI Crop / Task Start Error: {ex.Message}");
                            _isDetecting = false;
                        }


                    } // end if shouldscan


                    // Label tracking 파란색 영역 표시
                    if (_lastTrackedLabelRect.HasValue && (now - _lastQrScanTime) < _qrDisplayDuration)
                    {
                        Cv2.Rectangle(currentFrame, _lastTrackedLabelRect.Value, new Scalar(255, 0, 0), 3);
                        Cv2.PutText(currentFrame, "Label Area", 
                            new OpenCvSharp.Point(_lastTrackedLabelRect.Value.X, _lastTrackedLabelRect.Value.Y - 10), HersheyFonts.HersheySimplex, 1.0, new Scalar(255, 0, 0), 3);
                    }

                    // DrawQrCodeBox, 마지막 감지 후 일정 시간 동안 QR 박스(또는 라벨 박스) 계속 표시
                    // (비동기 스레드가 _lastQrPoints를 업데이트하면 여기서 그려짐)
                    if (_lastQrPoints != null && (now - _lastQrDetectionTime) < _qrDisplayDuration)
                    {
                        DrawQrCodeBox(currentFrame, _lastQrPoints, _lastQrText);
                    }

                    // Throttled UI update
                    // 영상 출력은 검출 로직과 상관없이 계속 수행됨 (끊김 방지)
                    if ((now - _lastUiUpdate) >= _uiUpdateInterval)
                    {
                        UpdateVideoSource(currentFrame);
                        _lastUiUpdate = now;
                    }

                }
                catch (Exception ex)
                {
                    Log.Error($"Frame Processing error: {ex.Message}");
                }
                finally
                {
                    currentFrame.Dispose();
                }

            } // while end
        }



        /// <summary>
        /// Streaming Main : Connect camera and display stream
        /// </summary>        
        private Thread? _captureThread;
        private readonly object _frameLock = new object();
        private Mat? _latestFrame = null; // 가장 최신 프레임만 보관
        private bool _newFrameAvailable = false;
        public async void StartStreaming()
        {
            // reconnection
            if (_isCapturing)
            {
                StopStreaming();
                await Task.Delay(1000); // delay for a moment
            }

            // connection
            if (string.IsNullOrEmpty(RTSP_URL) || RTSP_URL.Contains("default"))
            {
                UpdateStatus("Error: RTSP URL not configured!", Brushes.DarkRed);
                return;
            }

            UpdateStatus("Connecting...", Brushes.Red);
            Log.Info($"Start connecting camera... {RTSP_URL}");

            await Task.Run(() =>
            {
                // check again
                if (_capture.IsOpened()) _capture.Release();

                // FFMPEG options set in constructor.
                _capture.Open(RTSP_URL, VideoCaptureAPIs.FFMPEG);

                // Set buffer size to reduce latency
                _capture.Set(VideoCaptureProperties.BufferSize, 1); // Reduced from 1 for stability
                _capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('H', '2', '6', '4'));
            });

            // 연결 실패 처리
            if (!_capture.IsOpened())
            {
                UpdateStatus("Connection Failed!", Brushes.Red);
                return;
            }

            // Initialize WriteableBitmap immediately after connection
            int frameWidth = (int)_capture.FrameWidth;
            int frameHeight = (int)_capture.FrameHeight;

            if (frameWidth > 0 && frameHeight > 0)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    lock (_bitmapLock)
                    {
                        _writeableBitmap = new WriteableBitmap(
                            frameWidth, frameHeight,
                            96, 96, PixelFormats.Bgr24, null);
                        VideoSource = _writeableBitmap;

                        // Initialize buffer
                        int bufferSize = frameWidth * frameHeight * 3; // Bgr24
                        _renderBuffer = new byte[bufferSize];
                    }
                });
                Log.Info($"WriteableBitmap initialized: {frameWidth}x{frameHeight}");
            }

            UpdateStatus("Connected", Brushes.Green);
            _isCapturing = true;

            Log.Info($"Connected camera... {RTSP_URL}");

            // Screen capture thread
            _captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest, // 읽기 우선순위 높임
                Name = "RTSP_Capture_Thread"
            };
            _captureThread.Start();

            // Start Processing
            await Task.Run(() => FrameProcessingLoop());
        }



        // 캡처 전용 루프: 딜레이 없이 무조건 읽어서 최신 프레임 갱신
        private void CaptureLoop()
        {
            Log.Info("Capture thread started...");
            while (_isCapturing && _capture.IsOpened())
            {
                Mat tempFrame = new Mat();
                // 읽기에 실패하더라도 즉시 다시 시도하거나 짧게 대기
                if (_capture.Read(tempFrame) && !tempFrame.Empty())
                {
                    lock (_frameLock)
                    {
                        // 이전 프레임이 처리되지 못하고 남아있다면 폐기 (Frame Drop)
                        _latestFrame?.Dispose();
                        _latestFrame = tempFrame;
                        _newFrameAvailable = true;
                    }
                }
                else
                {
                    tempFrame.Dispose();
                    Thread.Sleep(1); // 읽기 실패 시에만 잠깐 대기
                }
            }
        }

        private void UpdateStatus(string text, Brush color)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                CameraStatusText = text;
                CameraStatusColor = color;
            });
        }


        public void StopStreaming()
        {
            _isCapturing = false;

            // **IMPROVED: Wait for processing loop to finish**
            if (_captureThread != null && _captureThread.IsAlive)
            {
                // Wait some delay
                bool joined = _captureThread.Join(500);
                if (!joined) Log.Warn("Capture thread did not finish in time !!! ");
            }

            // 자원 해제 (lock을 걸 필요가 있는지 확인, 혹은 메인 스레드에서만 호출되므로 안전)
            if (_capture != null)
            {
                // 이미 Release 되었을 수 있으므로 체크
                if (_capture.IsOpened())
                {
                    _capture.Release();
                }
                _capture.Dispose();
                _capture = new VideoCapture(); // 재사용을 위해 인스턴스 초기화 (선택사항)
            }

            lock (_bitmapLock)
            {
                _writeableBitmap = null;
                _renderBuffer = null;
            }

            Log.Info("Streaming stopped and resources cleaned up.");
        }

        public void ExecuteReconnect()
        {
            Log.Info("Reconnect button clicked.");

            MessageBoxResult result = MessageBox.Show("Are you sure you want to reconnect the camera?", "Confirm Reconnect", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                StartStreaming();
            }
        }

        /// <summary>
        /// 메인 윈도우 전체화면 실행.
        /// </summary>
        private void ApplyFullScreenMode()
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var window = Application.Current.MainWindow;
                if (window != null)
                {
                    // 1. 타이틀 바(최소화/닫기 버튼 포함) 제거
                    window.WindowStyle = WindowStyle.None;

                    // 2. 창 최대화
                    window.WindowState = WindowState.Maximized;

                    // 3. (선택사항) 크기 조절 불가능하게 설정
                    window.ResizeMode = ResizeMode.NoResize;

                    // 4. (선택사항) 항상 위에 표시하려면 주석 해제
                    //window.Topmost = true;
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded); // 윈도우 로드 후 실행
        }


        /// <summary>
        /// *new: Optimized video source update using reusable WriteableBitmap
        /// </summary>
        private void UpdateVideoSource(Mat frame)
        {
            if (frame == null || frame.Empty() || frame.IsDisposed)
            {
                Log.Error("UpdateVideoSource: called with invalid frame.");
                return;
            }
            
            // 이미 랜더링 중이면 이번 프레임은 드랍 (Frame drop) 
            if (_isRendering) return;
            // 종료 중이면 렌더링 스킵
            if (Application.Current == null || Application.Current.Dispatcher.HasShutdownStarted) return;

            _isRendering = true; // Start rendering

            try
            {
                // UI 스레드 유효성 검사
                if (Application.Current == null || Application.Current.Dispatcher == null || Application.Current.Dispatcher.HasShutdownStarted)
                {
                    _isRendering = false;
                    return;
                }

                // Copy frame data to byte array BEFORE async operation**
                int frameWidth = frame.Width;
                int frameHeight = frame.Height;
                int dataSize = (int)(frame.Total() * frame.ElemSize());
                long stride = frame.Step();

                // Reallocate render buffer if size mismatch**
                if (_renderBuffer == null || _renderBuffer.Length != dataSize)
                {
                    _renderBuffer = new byte[dataSize];
                }

                // Copy data from Mat to byte array
                Marshal.Copy(frame.Data, _renderBuffer, 0, dataSize);

                // Use BeginInvoke instead of Invoke to prevent blocking**
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        // Check windows status (minimized etc.)
                        if (Application.Current.MainWindow != null && Application.Current.MainWindow.WindowState == WindowState.Minimized)
                        {
                            return;
                        }

                        lock (_bitmapLock)
                        {
                            if (_writeableBitmap == null ||
                                _writeableBitmap.PixelWidth != frameWidth ||
                                _writeableBitmap.PixelHeight != frameHeight)
                            {
                                _writeableBitmap = new WriteableBitmap(
                                    frameWidth, frameHeight,
                                    96, 96, PixelFormats.Bgr24, null);
                                VideoSource = _writeableBitmap;
                            }

                            _writeableBitmap.Lock();
                            try
                            {
                                // Direct copy from OpenCV Mat to WriteableBitmap
                                unsafe
                                {
                                    byte* pDst = (byte*)_writeableBitmap.BackBuffer;
                                    fixed (byte* pSrc = _renderBuffer) // 재 사용된 버퍼 사용
                                    {
                                        int srcStride = (int)stride;
                                        int dstStride = _writeableBitmap.BackBufferStride;

                                        // 라인 단위 복사
                                        for (int y = 0; y < frameHeight; y++)
                                        {
                                            // Buffer.MemoryCopy 사용
                                            Buffer.MemoryCopy(
                                                pSrc + (y * srcStride), 
                                                pDst + (y * dstStride),
                                                dstStride,
                                                Math.Min(srcStride, dstStride));
                                        }
                                    }
                                }
                                _writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, frameWidth, frameHeight));
                            }
                            finally
                            {
                                _writeableBitmap.Unlock();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // UI 스레드 내부 예외는 외부 try-catch로 잡히지 않으므로 여기서 로깅
                        Log.Error($"UpdateVideoSource: UI Async Update Failed: {ex.Message}");
                    }
                    finally 
                    {
                        _isRendering = false; // Rendering 완료
                    }

                }, System.Windows.Threading.DispatcherPriority.Render); // Render 우선순위 사용

            }
            catch (Exception ex)
            {
                Log.Error($"UpdateVideoSource: Video source update error: {ex.Message}");
                _isRendering = false; // 예외 발생 시에도 플래그 해제 필수
            }
        }

        /// <summary>
        /// 캡처된 이미지를 UI에 안전하게 업데이트합니다.
        /// Mat이 이미 Dispose된 후 UI 스레드가 접근하여 발생하는 충돌을 방지합니다.
        /// </summary>
        private void UpdateCapturedImageSafe(Mat cropMat)
        {
            if (cropMat == null || cropMat.Empty() || cropMat.IsDisposed) return;

            try
            {
                // 1. 메타데이터 및 픽셀 데이터 추출 (현재 스레드에서 수행)
                int width = cropMat.Width;
                int height = cropMat.Height;
                int step = (int)cropMat.Step();

                // 데이터 복사
                long dataSize = cropMat.Total() * cropMat.ElemSize();
                byte[] pixelData = new byte[dataSize];
                Marshal.Copy(cropMat.Data, pixelData, 0, (int)dataSize);

                // 2. UI 스레드에서 비트맵 생성 (복사한 데이터 사용)
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        WriteableBitmap wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
                        wb.Lock();
                        unsafe
                        {
                            byte* pDest = (byte*)wb.BackBuffer;
                            fixed (byte* pSrc = pixelData)
                            {
                                int backStride = wb.BackBufferStride;
                                for (int y = 0; y < height; y++)
                                {
                                    // 라인 단위 복사
                                    Buffer.MemoryCopy(
                                        pSrc + (y * step),
                                        pDest + (y * backStride),
                                        backStride,
                                        Math.Min(step, backStride));
                                }
                            }
                        }
                        wb.AddDirtyRect(new Int32Rect(0, 0, width, height));
                        wb.Unlock();

                        CapturedImageSource = wb;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Captured Image UI Update Failed: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error($"UpdateCapturedImageSafe Error: {ex.Message}");
            }
        }

        /// <summary>
        /// QQR 스캔 성공 시 공통 처리 로직
        /// </summary>
        /// <param name="originalFrame"></param>
        /// <param name="qrData"></param>
        /// <param name="scanTime"></param>
        private void HandleSuccessfulScan(Mat src, string qrData, DateTime scanTime)
        {
            if (src == null || src.IsDisposed || src.Empty())
            {
                Log.Warn("HandleSuccessfulScan called with invalid source frame.");
                return;
            }

            try
            {
                _lastQrDetectionTime = scanTime;
                _lastQrText = qrData;

                // 이미지 저장 Mat 복사
                Mat frameTosave = src.Clone();

                // UI 업데이트
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (LastScannedData != qrData)
                        {
                            ScanHistory.Insert(0, new ScanRecord 
                            {
                                Time = _lastQrDetectionTime.ToString("yyyy-MM-dd HH:mm:ss"), 
                                Data = qrData 
                            });

                            while (ScanHistory.Count > 50)
                            {
                                ScanHistory.RemoveAt(ScanHistory.Count - 1);
                            }

                            LastScannedData = qrData;
                            PlayScanSound();

                            // Save image in background
                            Task.Run(() =>
                            {
                                try
                                {
                                    using (frameTosave)
                                    {
                                        SaveScannedImage(frameTosave, qrData, scanTime);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.Error($"Background image save error: {ex.Message}");
                                }
                            });
                        }
                        else
                        {
                            frameTosave?.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"UI update error: {ex.Message}");
                        frameTosave?.Dispose();
                    }
                });

            }
            catch (Exception ex)
            {
                Log.Error($"Handle successful scan error: {ex.Message}");
                src?.Dispose();
            }
        }


        private void DrawQrCodeBox(Mat frame, OpenCvSharp.Point[] qrPoints, string? qrData)
        {
            try
            {
                if (frame.IsDisposed || frame.Empty()) return;
                if (qrPoints == null || qrPoints.Length < 3) return;
                if (string.IsNullOrEmpty(qrData)) return;

                // QR decoding success
                Scalar boxColor = Scalar.FromRgb(0, 255, 0);
                int thickness = 4;
                int textThickness = 8;

                // 1. Sort points clockwise to ensure we draw a proper polygon (avoid bowtie)
                var sortedPoints = SortPointsClockwise(qrPoints);

                // 2. Clamp points to be within the frame (safety)
                for (int k = 0; k < sortedPoints.Length; k++)
                {
                    sortedPoints[k].X = Math.Max(0, Math.Min(frame.Width - 1, sortedPoints[k].X));
                    sortedPoints[k].Y = Math.Max(0, Math.Min(frame.Height - 1, sortedPoints[k].Y));
                }

                // 3. Draw the exact polygon
                for (int i = 0; i < sortedPoints.Length; i++)
                {
                    Cv2.Line(frame, sortedPoints[i], sortedPoints[(i + 1) % sortedPoints.Length], boxColor, thickness);

                    // Draw small corner markers to visualize points clearly
                    Cv2.Circle(frame, sortedPoints[i], 5, Scalar.Red, -1);
                }

                // 4. Also draw a bounding rect for stability (helps if points are jittery)
                OpenCvSharp.Rect boundingRect = Cv2.BoundingRect(sortedPoints);

                // 5. Draw Text Label
                if (!string.IsNullOrEmpty(qrData))
                {
                    // Place text above the top-most point
                    int textY = boundingRect.Top - 20;
                    string displayTxt = qrData.Substring(0, 11) + "..."; // Heat number + product seq

                    // Draw black background for text readability
                    OpenCvSharp.Size textSize = Cv2.GetTextSize(displayTxt, HersheyFonts.HersheySimplex, 1.4, textThickness, out int baseline);
                    Cv2.Rectangle(frame,
                        new OpenCvSharp.Point(boundingRect.Left, textY - textSize.Height - 5),
                        new OpenCvSharp.Point(boundingRect.Left + textSize.Width, textY + 5),
                        Scalar.Black, -1);

                    Cv2.PutText(frame, displayTxt, new OpenCvSharp.Point(boundingRect.Left, textY),
                        HersheyFonts.HersheySimplex, 1.4, boxColor, 2);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Draw QR box error: {ex.Message}");
            }
        }

        /// <summary>
        /// Helper to sort points clockwise around their center to prevent "bowtie" shapes.
        /// </summary>
        private OpenCvSharp.Point[] SortPointsClockwise(OpenCvSharp.Point[] points)
        {
            if (points == null || points.Length == 0) return new OpenCvSharp.Point[0];

            // Calculate center
            double cx = points.Average(p => p.X);
            double cy = points.Average(p => p.Y);

            // Sort by angle
            return points.OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx)).ToArray();
        }

        private void CollectFrameInformation(Mat frame, Stopwatch sw, ref long frameCount)
        {
            frameCount++;
            if (sw.ElapsedMilliseconds >= 1000) // 1초마다 FPS 업데이트
            {
                double fps = frameCount * 1000.0 / sw.ElapsedMilliseconds;
                string resolution = $"{frame.Width}x{frame.Height}";
                string type = frame.Type().ToString();
                int channels = frame.Channels();

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    // 카메라에서 직접 읽어온 FPS 대신 실제 처리 FPS를 표시
                    FpsText = $"FPS: {fps:F2} | Res: {resolution} | Type: {type} | Ch: {channels}";
                });

                sw.Restart();
                frameCount = 0;
            }
        }

        /// <summary>
        /// Save image file
        /// </summary>
        private void SaveScannedImage(Mat frame, string qrData, DateTime scanTime)
        {
            try
            {
                if (frame == null || frame.Empty())
                {
                    Log.Warn("Cannot save image: frame is empty");
                    return;
                }

                // root foler(exe location)
                string exePath = AppDomain.CurrentDomain.BaseDirectory;
                string yearMonth = scanTime.ToString("yyyy-MM");
                string day = scanTime.ToString("dd");
                string folderPath = Path.Combine(exePath, "logs", yearMonth, day);

                // if not exist foler
                if (!Directory.Exists(folderPath)) { Directory.CreateDirectory(folderPath); }

                // File name : timestamp + heat number
                string prefix = qrData.Length >= 11 ? qrData.Substring(0, 11) : qrData;

                // 파일명에 사용할 수 없는 문자 제거
                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    prefix = prefix.Replace(c, '_');
                }

                string timestamp = scanTime.ToString("HHmmss");
                string fileName = $"{timestamp}_{prefix}.jpg";
                string filePath = Path.Combine(folderPath, fileName);

                // Save image
                Cv2.ImWrite(filePath, frame);
                Log.Info($"QR scanned image file save complete: {filePath}");

            }
            catch (Exception ex)
            {
                Log.Error($"Image file save fail !!! : {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[Image file save fail] {ex.Message}");
            }
        }

        /// <summary>
        /// Mannual Capture
        /// </summary>
        private void ExecuteManualCapture()
        {
            Log.Info("Manual image capture requested.");

            Mat frameToSave = new Mat();

            // Copy current frame
            lock (_frameLock)
            {
                if (_latestFrame != null && !_latestFrame.Empty() && !_latestFrame.IsDisposed)
                {
                    frameToSave = _latestFrame.Clone();
                }
            }

            if (frameToSave == null)
            {
                MessageBox.Show("No active video frame to capture. Please ensure the camera is connected.",
                "Capture Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string exePath = AppDomain.CurrentDomain.BaseDirectory;
                // 새로운 폴더 경로 생성 (logs/QR scan fault)
                string folderPath = Path.Combine(exePath, targetFolder, "QR_Scanfault");

                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }

                // 타임스탬프로 파일명 생성
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string fileName = $"ManualCapture_{timestamp}.jpg";
                string filePath = Path.Combine(folderPath, fileName);

                // 이미지 저장
                Cv2.ImWrite(filePath, frameToSave);
                Log.Info($"Manual captured image saved to: {filePath}");

                // UI 쓰레드에서 팝업창 띄우기 및 뷰어 실행 여부 확인
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBoxResult result = MessageBox.Show(
                        "Image saved, Do you want to open image now?",
                        "Capture Success",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            // 기본 이미지 뷰어로 파일 열기
                            var p = new Process();
                            p.StartInfo = new ProcessStartInfo(filePath)
                            {
                                UseShellExecute = true // 운영체제 기본 설정 앱 사용
                            };
                            p.Start();
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Failed to open image viewer: {ex.Message}");
                            MessageBox.Show($"Failed to open image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error($"Manual capture saving failed: {ex.Message}");
                MessageBox.Show($"Failed to save image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // 사용이 끝난 프레임 메모리 해제
                frameToSave?.Dispose();
            }

        }

        private void PlayScanSound()
        {
            try
            {
                // _soundPlayer가 null이 아니며 로드되었을 경우에만 재생
                _soundPlayer?.Play();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to play sound: {ex.Message}");
            }
        }

        /// <summary>
        /// Hikvision 카메라 광학 줌 제어 (ISAPI PUT 요청)
        /// </summary>
        /// <param name="action">zoomin, zoomout, stop</param>
        public async Task ControlHikvisionZoomAsync(string action)
        {
            if (_ptzHttpClient == null) return;

            try
            {
                // 채널 1 기준 Continuous PTZ 엔드포인트
                string url = $"http://{CAMERA_IP}/ISAPI/PTZCtrl/channels/1/continuous";

                int zoomValue = 0;
                if (action == "zoomin") zoomValue = 30;        // 줌 인 속도 (1 ~ 100)
                else if (action == "zoomout") zoomValue = -30; // 줌 아웃 속도 (-1 ~ -100)
                else if (action == "stop") zoomValue = 0;      // 줌 정지

                string xmlBody = $@"<?xml version=""1.0"" encoding=""UTF-8""?><PTZData><pan>0</pan><tilt>0</tilt><zoom>{zoomValue}</zoom></PTZData>";

                using var content = new StringContent(xmlBody, Encoding.UTF8, "application/xml");
                HttpResponseMessage response = await _ptzHttpClient.PutAsync(url, content);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warn($"[Hikvision] Zoom {action} 실패: HTTP {response.StatusCode}");
                }
                else 
                {
                    Log.Info($"[Hikvision] Zoom {action} 성공: HTTP {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Hikvision] Zoom {action} 예외: {ex.Message}");
            }
        }

        // INotifyPropertyChanged 구현
        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // IDisposable 구현
        private bool _disposed = false;
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // 관리 리소스 해제
                    StopStreaming();
                    _capture?.Dispose();
                    _soundPlayer?.Dispose();
                    _hbeamTracker?.Dispose();
                }
                // 비관리 리소스 해제
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }



    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}