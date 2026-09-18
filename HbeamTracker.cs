using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.IO;
using System.Configuration;
using System.Reflection.Metadata.Ecma335;
using System.Net.NetworkInformation;

namespace PYV_MaterialScanner
{
    /// <summary>
    /// 스캔 모드 선택용 Enum
    /// </summary>
    public enum QrScanMode
    {
        OriginalSizeOnly,
        UpscaleSize,
        Preprocess
    }

    /// <summary>
    /// QR 라벨 검출 결과 정보
    /// </summary>
    public class LabelDetectionResult : IDisposable
    {
        public bool IsDetected { get; set; }
        public Point[]? ResultPoints { get; set; }
        public Point Center { get; set; }
        public Mat? CroppedLabel { get; set; }
        public string? DetectionMethod { get; set; } // "QRDetector" or "EdgeDetection"
        public string? DecodedText { get; set; } // QR 디코딩 성공 시

        public void Dispose()
        {
            CroppedLabel?.Dispose();
        }
    }

    /// <summary>
    /// QR 코드 디텍터를 이용한 라벨 검출 클래스
    /// </summary>
    internal class HbeamTracker : IDisposable
    {
        // 처리과정 생성되는 중간 Mat 객체
        private Mat _gray = new Mat();
        private Mat _blurred = new Mat();
        private Mat _edges = new Mat();
        private Mat _dilated = new Mat();
        private Mat _kernel = new Mat();

        // QR 코드 디텍터
        private QRCodeDetector _qrDetector = new QRCodeDetector();
        private readonly ZXing.Windows.Compatibility.BarcodeReader _barcodeReader;
        private WeChatQRCode? _weChatQrDetector; //weChat

        private double _scaleFactor = 1.8;

        // 디버그 모드
        private volatile bool _debugMode = false;
        private string _debugFolder = @"C:\Debug";


        public HbeamTracker()
        {
            _kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));

            // initialize ZXing Barcode Reader
            _barcodeReader = new ZXing.Windows.Compatibility.BarcodeReader
            {
                // [Fix] 로테이션 기능을 끄고 QR의 자체 Finder Pattern 좌표를 신뢰함
                AutoRotate = false,
                Options = new ZXing.Common.DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new[] { ZXing.BarcodeFormat.QR_CODE }
                }
            };

            // app.config에서 debugMode 설정 읽기
            string debugModeConfig = ConfigurationManager.AppSettings["IsDebugMode"];
            if (!string.IsNullOrEmpty(debugModeConfig) && bool.TryParse(debugModeConfig, out bool isDebug))
            {
                _debugMode = isDebug;
            }

            // app.config에서 디버그 폴더 경로 읽기 (선택사항)
            string debugFolderConfig = ConfigurationManager.AppSettings["IsDebugFolder"];
            if (!string.IsNullOrEmpty(debugFolderConfig))
            {
                _debugFolder = debugFolderConfig;
            }

            // WeChatQRCode 딥러닝 모델 초기화
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string modelsDir = Path.Combine(baseDir, "Models"); // 실행파일 폴더 하위의 Models 폴더
                string detectProto = Path.Combine(modelsDir, "detect.prototxt");
                string detectModel = Path.Combine(modelsDir, "detect.caffemodel");
                string srProto = Path.Combine(modelsDir, "sr.prototxt");
                string srModel = Path.Combine(modelsDir, "sr.caffemodel");

                // 4개의 파일이 모두 존재하는 경우에만 활성화 (파일이 없으면 기존 로직으로 동작)
                if (File.Exists(detectProto) && File.Exists(detectModel) && File.Exists(srProto) && File.Exists(srModel))                                
                {
                    // WeChatQRCode.Create 호출
                    _weChatQrDetector = WeChatQRCode.Create(detectProto, detectModel, srProto, srModel);

                    // 성능 이슈: 뒤의 두 파라미터(SR 모델)를 빈 문자열("")로 넘겨서 초해상도 연산을 강제 비활성화!
                    //_weChatQrDetector = WeChatQRCode.Create(detectProto, detectModel, "", "");

                    System.Diagnostics.Debug.WriteLine("[WeChatQRCode] 딥러닝 모델 정상 로드 완료!");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[WeChatQRCode] 모델 파일(Models 폴더)을 찾을 수 없어 활성화되지 않았습니다.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WeChatQRCode] 초기화 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 흑백(그레이) / IR 카메라 환경 조건에서 동적 ROI 트래킹(패턴 밀집도 기반)
        /// 빛 반사(Glore)와 QR 라벨을 구분하는 엣지 밀집도(Edge Density) 방식 추가
        /// </summary>
        public Rect? DetectWhiteLableArea(Mat sourceFrame)
        {
            if (sourceFrame == null || sourceFrame.Empty()) return null;

            try
            {
                // 1. 빠른 연산을 위해 다운스케일링
                int targetWidth = 800;
                double scale = (double)sourceFrame.Width / targetWidth;
                using Mat small = new Mat();
                Cv2.Resize(sourceFrame, small, new Size(targetWidth, (int)(sourceFrame.Height / scale)));

                // 2. Grayscale 후 가벼운 블러
                using Mat gray = new Mat();
                Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);

                // 3. 밝은 영역 추출
                using Mat binary = new Mat();
                Cv2.Threshold(gray, binary, 160, 255, ThresholdTypes.Binary);

                // 4. 모폴로지 Close (커널 크기를 15x15로 줄여 작은 라벨이 주변 노이즈와 뭉치는 현상 방지)
                using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(15, 15));
                Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel);

                // 5. 외곽선(Contour) 탐색
                Cv2.FindContours(binary, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                Rect? bestRect = null;
                double bestScore = 0;
                double totalArea = small.Width * small.Height;

                foreach (var contour in contours)
                {
                    Rect rect = Cv2.BoundingRect(contour);
                    double rectArea = rect.Width * rect.Height;

                    // [개선 1] 면적 제한 대폭 강화 (거대한 롤러 반사광 원천 차단)
                    // 라벨은 전체 4K 화면에서 매우 작으므로 0.2% ~ 5% 사이로 타이트하게 제한
                    if (rectArea < totalArea * 0.002 || rectArea > totalArea * 0.05) continue;

                    // [개선 2] 종횡비 제한 (정사각형 ~ 약간 긴 직사각형)
                    double aspect = (double)Math.Max(rect.Width, rect.Height) / Math.Min(rect.Width, rect.Height);
                    if (aspect > 1.8) continue;

                    // [개선 3] Solidity (사각형 형태 채움률)
                    Point[] hull = Cv2.ConvexHull(contour);
                    double hullArea = Cv2.ContourArea(hull);
                    if (hullArea <= 0) continue;
                    double solidity = Cv2.ContourArea(contour) / hullArea;
                    if (solidity < 0.70) continue;

                    // [개선 4] 텍스처(표준편차) 검증 강화
                    using Mat roi = new Mat(gray, rect);
                    Cv2.MeanStdDev(roi, out Scalar mean, out Scalar stddev);

                    // 밋밋한 쇳덩이 반사광(stddev < 20)을 버리고, 흑백 대비가 강한 라벨(stddev > 30)만 통과
                    if (mean.Val0 < 130 || stddev.Val0 < 30) continue;

                    // [개선 5] 스코어링 공식 변경 (면적 가중치 배제)
                    // 오직 "질감의 뚜렷함(표준편차)"과 "사각형에 가까운 정도(Solidity)"만 곱해서 평가
                    double score = stddev.Val0 * solidity;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestRect = rect;
                    }
                }

                // 최적의 라벨 영역 반환
                if (bestRect.HasValue)
                {
                    Rect finalRect = new Rect(
                        (int)(bestRect.Value.X * scale), (int)(bestRect.Value.Y * scale),
                        (int)(bestRect.Value.Width * scale), (int)(bestRect.Value.Height * scale)
                    );

                    // [개선 6] 여유 마진 대폭 확대 (15% -> 50%)
                    finalRect.Inflate((int)(finalRect.Width * 0.50), (int)(finalRect.Height * 0.50));
                    return GetSafeCropRect(finalRect, sourceFrame.Width, sourceFrame.Height, 0);
                }
            }
            catch { /* 예외 무시 */ }

            return null;
        }

        /// <summary>
        /// 통합된 QR 라벨 검출 메서드
        /// </summary>
        public LabelDetectionResult DetectQR(Mat sourceFrame, QrScanMode scanMode)
        {
            var result = new LabelDetectionResult { IsDetected = false, DetectionMethod = "None", CroppedLabel = null };
            string debugPath = "";
            string filePrefix = "";

            if (_debugMode)
            {
                try
                {
                    debugPath = Path.Combine(_debugFolder, DateTime.Now.ToString("yyyyMMdd"));
                    if (!Directory.Exists(debugPath)) Directory.CreateDirectory(debugPath);
                    filePrefix = DateTime.Now.ToString("HHmmss") + "_";
                    Cv2.ImWrite(Path.Combine(debugPath, filePrefix + "1_original.png"), sourceFrame);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Error] Debug folder error: {ex.Message}");
                }
            }

            string? decodedText = null;
            Point2f[]? qrPoints = null;
            string? method = string.Empty;

            try
            {
                // Step 1 : 무조건 Original 사이즈로 먼저 시도 (가장 빠름)
                decodedText = TryDecodeSequence(sourceFrame, "1.Original", useWeChat: false, out qrPoints, out method);

                // Step 2 : 실패 시, 선택된 ScanMode에 따라 2차 시도 (Fallback)
                if (string.IsNullOrEmpty(decodedText))
                {
                    // 해상도 upscale : 성능문제로 사실상 사용 불가
                    if (scanMode == QrScanMode.UpscaleSize)
                    {
                        using (Mat upscaled = new Mat())
                        {
                            Cv2.Resize(sourceFrame, upscaled, new Size(0, 0), _scaleFactor, _scaleFactor, InterpolationFlags.Cubic);
                            decodedText = TryDecodeSequence(upscaled, "2.Upscaled", useWeChat: false, out qrPoints, out method);
                        }
                    }
                    else if (scanMode == QrScanMode.Preprocess) // 전처리 프로세스 모드
                    {
                        // Milde mode first (부드러운 전처리) - 찌그러지거나 구겨진 라벨용 (Deep Learning 친화적, WechatQR)
                        // 이진화 하지않고, 음영이 살아있는 상태에서 대비만 극대화 합니다.
                        using (Mat mildProcessed = PreprocessMildForDeepLearning(sourceFrame))
                        {
                            if (_debugMode) Cv2.ImWrite(Path.Combine(debugPath, filePrefix + "2_mild_processed.png"), mildProcessed);
                            decodedText = TryDecodeSequence(mildProcessed, "2.Mild-Grayscale", useWeChat: true, out qrPoints, out method);
                        }

                        //// Full preprocessing - 인식률 미비함 (성능확보 위해 삭제)
                        //if (string.IsNullOrEmpty(decodedText))
                        //{
                        //    using (Mat processed = PreprocessForQr(sourceFrame))
                        //    {
                        //        if (_debugMode) Cv2.ImWrite(Path.Combine(debugPath, filePrefix + "3_full_processed.png"), processed);
                        //        decodedText = TryDecodeSequence(processed, "3.Full-Pre_processed", useWeChat: false, out qrPoints, out method);
                        //    }
                        //}

                    }                    
                }

                // Step 3 :  좌표는 찾았는데 텍스트 변환이 불가한 경우(Warping 재시도)
                if (string.IsNullOrEmpty(decodedText) &&  qrPoints != null && qrPoints.Length >= 3)
                {
                    decodedText = RetryWithWarping(sourceFrame, qrPoints);
                    if (!string.IsNullOrEmpty(decodedText)) method = "3.Warping_Recovery";
                }


                // -----------------------------------------------------------
                // Result process...
                // -----------------------------------------------------------
                if (!string.IsNullOrEmpty(decodedText))
                {
                    result.IsDetected = true;
                    result.DecodedText = decodedText;
                    result.DetectionMethod = method;

                    if (qrPoints != null && qrPoints.Length >= 3)
                    {
                        result.ResultPoints = NormalizeQrPoints(qrPoints);
                        Rect boundingRect = Cv2.BoundingRect(result.ResultPoints);
                        result.Center = new Point(boundingRect.X + boundingRect.Width / 2, boundingRect.Y + boundingRect.Height / 2);
                        // result.CroppedLabel = CropLabelArea(sourceFrame, result.ResultPoints); // QR코드영역만
                        result.CroppedLabel = sourceFrame.Clone();
                    }
                    else
                    {
                        // Fallback 좌표
                        result.ResultPoints = new Point[] {
                            new Point(0,0), new Point(sourceFrame.Width, 0),
                            new Point(sourceFrame.Width, sourceFrame.Height), new Point(0, sourceFrame.Height)
                        };
                        result.Center = new Point(sourceFrame.Width / 2, sourceFrame.Height / 2);
                        result.CroppedLabel = sourceFrame.Clone(); // 한 번만 Clone 할당
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HbeamTracker] QR detector failed: {ex.Message}");
            }

            // 실패했을 경우 안전하게 빈 Mat 반환
            if (result.CroppedLabel == null) result.CroppedLabel = new Mat();

            return result;
        }


        /// <summary>
        /// 
        /// </summary>
        private string RetryWithWarping(Mat src, Point2f[] pts)
        {
            try
            {
                Size warpSize = new Size(512, 512);
                Point2f[] destPts = {
                    new Point2f(0, 0), new Point2f(512, 0),
                    new Point2f(512, 512), new Point2f(0, 512)
                };

                using (Mat matrix = Cv2.GetPerspectiveTransform(pts, destPts))
                using (Mat warped = new Mat())
                {
                    Cv2.WarpPerspective(src, warped, matrix, warpSize);
                    // 펴진 이미지는 대비를 한번 더 강화
                    using (var clahe = Cv2.CreateCLAHE(3.0, new Size(8, 8)))
                    {
                        Mat gray = warped.Channels() == 3 ? warped.CvtColor(ColorConversionCodes.BGR2GRAY) : warped.Clone();
                        clahe.Apply(gray, gray);
                        return DecodeQrWithWeChat(gray, out _);
                    }
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// [NEW Helper] ZXing -> OpenCV -> WeChat 순차 시도
        /// </summary>
        private string TryDecodeSequence(Mat targetFrame, string stepPrefix, bool useWeChat, out Point2f[] points, out string successfulMethod)
        {
            /*
            points = null;
            successfulMethod = string.Empty;
            string decodedText = string.Empty;

            // 1. ZXing
            successfulMethod = $"{stepPrefix}_ZXing";
            decodedText = DecodeQrWithZXing(targetFrame, out points);
            if (!string.IsNullOrEmpty(decodedText)) return decodedText;

            // 2. OpenCV
            successfulMethod = $"{stepPrefix}_OpenCV";
            decodedText = DecodeQrWithOpenCV(targetFrame, out points);
            if (!string.IsNullOrEmpty(decodedText)) return decodedText;

            if (useWeChat && _weChatQrDetector != null)     // high performance
            {
                // 3. WeChat
                if (_weChatQrDetector != null)
                {
                    successfulMethod = $"{stepPrefix}_WeChatQRCode";
                    decodedText = DecodeQrWithWeChat(targetFrame, out points);
                    if (!string.IsNullOrEmpty(decodedText)) return decodedText;
                }
            }

            successfulMethod = "None";
            */

            points = null;
            successfulMethod = "None";
            string text = null;

            // 1. WeChat
            if (useWeChat && _weChatQrDetector != null)
            {
                text = DecodeQrWithWeChat(targetFrame, out points);
                if (!string.IsNullOrEmpty(text)) { successfulMethod = $"{stepPrefix}_WeChat"; return text; }
            }

            // 2. ZXing
            text = DecodeQrWithZXing(targetFrame, out points);
            if (!string.IsNullOrEmpty(text)) { successfulMethod = $"{stepPrefix}_ZXing"; return text; }

            // 3. OpenCV
            text = DecodeQrWithOpenCV(targetFrame, out points);
            if (!string.IsNullOrEmpty(text)) { successfulMethod = $"{stepPrefix}_OpenCV"; return text; }

            return null;
        }


        
        /// <summary>
        /// QR 라벨을 검출하여 결과 정보만 반환합니다 (Drawing은 하지 않음)
        /// QR code scan procedure --> OpenCV 이미지 전처리 후 ZXing QR code 스캔.
        /// </summary>
        public LabelDetectionResult DetectOriginalPreprocessEachSteps(Mat sourceFrame)
        {
            var result = new LabelDetectionResult
            {
                IsDetected = false,
                DetectionMethod = "None",
                CroppedLabel = new Mat()
            };

            // Debug Path
            string debugPath = "";
            string filePrefix = "";
            if (_debugMode)
            {
                try
                {
                    string timestamp = DateTime.Now.ToString("yyyyMMdd");
                    debugPath = Path.Combine(_debugFolder, timestamp);
                    if (!Directory.Exists(debugPath))
                    {
                        Directory.CreateDirectory(debugPath);
                    }

                    // 파일명에 타임스탬프
                    filePrefix = DateTime.Now.ToString("HHmmss") + "_";
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Error] 디버그 폴더({debugPath}) 생성 예외: {ex.Message}");
                }
            }


            // -----------------------------------------------------------
            // QR Code scanning Strategy (Fallback Sequence)
            // -----------------------------------------------------------
            string decodedText = null;
            Point2f[] qrPoints = null;
            string method = string.Empty;

            try
            {
                if (_debugMode) Cv2.ImWrite(Path.Combine(debugPath, filePrefix + "1_original.png"), sourceFrame); // Debug mode

                // step 1 : Try detect Zxing
                method = "1.Original_ZXing";
                decodedText = DecodeQrWithZXing(sourceFrame, out qrPoints);
                if (string.IsNullOrEmpty(decodedText))
                {
                    // step 2 : Try detect OpenCV
                    method = "1.Original_OpenCV";
                    decodedText = DecodeQrWithOpenCV(sourceFrame, out qrPoints);

                    if (string.IsNullOrEmpty(decodedText) && _weChatQrDetector != null)
                    {
                        // setp 3 : Try detect WeChat
                        method = "1.Original_WeChatQRCode";
                        decodedText = DecodeQrWithWeChat(sourceFrame, out qrPoints);
                    }
                }

                // Try again with pre-processing image
                if (string.IsNullOrEmpty(decodedText))
                {
                    using (Mat processed = PreprocessForQr(sourceFrame))
                    {
                        // step 1 : Try detect ZXing
                        method = "2.Pre-processed_ZXing";
                        decodedText = DecodeQrWithZXing(processed, out qrPoints);

                        if (string.IsNullOrEmpty(decodedText))
                        {
                            // step 2 : Try detect OpenCV
                            method = "2.Pre-processed_OpenCV";
                            decodedText = DecodeQrWithOpenCV(processed, out qrPoints);

                            if (string.IsNullOrEmpty(decodedText) && _weChatQrDetector != null)
                            {
                                // setp 3 : Try detect WeChat
                                method = "2.Pre-processed_WeChatQRCode";
                                decodedText = DecodeQrWithWeChat(processed, out qrPoints);
                            }
                        }
                    }
                }
                                
                // -----------------------------------------------------------
                // Result process...
                // -----------------------------------------------------------
                if (!string.IsNullOrEmpty(decodedText)) 
                {
                    result.IsDetected = true;
                    result.DecodedText = decodedText;
                    result.DetectionMethod = method;

                    // ROI 
                    result.CroppedLabel = sourceFrame.Clone();

                    // points covert (Point2f[] -> Point[])
                    if (qrPoints != null && qrPoints.Length >= 3) 
                    {
                        // [개선] 화면 기준 정렬을 버리고 QR 고유 방향(물리적 위치) 고정
                        result.ResultPoints = NormalizeQrPoints(qrPoints);

                        // Center
                        Rect boundingRect = Cv2.BoundingRect(result.ResultPoints);
                        result.Center = new Point(
                            boundingRect.X + boundingRect.Width / 2,
                            boundingRect.Y + boundingRect.Height / 2
                        );

                        //
                        result.CroppedLabel = CropLabelArea(sourceFrame, result.ResultPoints);
                    }
                    else
                    {
                        // Fallback 좌표
                        result.ResultPoints = new Point[] {
                            new Point(0,0), new Point(sourceFrame.Width, 0),
                            new Point(sourceFrame.Width, sourceFrame.Height), new Point(0, sourceFrame.Height)
                        };
                        result.Center = new Point(sourceFrame.Width / 2, sourceFrame.Height / 2);
                        result.CroppedLabel = sourceFrame.Clone();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HbeamTracker] QR detector failed: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// QR 라벨을 검출하여 결과 정보만 반환합니다 (Drawing은 하지 않음)
        /// QR code scan procedure --> OpenCV 이미지 전처리 후 ZXing QR code 스캔.
        /// </summary>
        public LabelDetectionResult DetectUpscaleSizeEachSteps(Mat sourceFrame)
        {
            var result = new LabelDetectionResult
            {
                IsDetected = false,
                DetectionMethod = "None",
                CroppedLabel = new Mat()
            };

            // 디버그 폴더 생성
            string debugPath = "";
            string filePrefix = "";
            if (_debugMode)
            {
                try
                {
                    string timestamp = DateTime.Now.ToString("yyyyMMdd");
                    debugPath = Path.Combine(_debugFolder, timestamp);
                    if (!Directory.Exists(debugPath))
                    {
                        Directory.CreateDirectory(debugPath);
                    }

                    // 파일명에 타임스탬프
                    filePrefix = DateTime.Now.ToString("HHmmss") + "_";
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Error] 디버그 폴더({debugPath}) 생성 예외: {ex.Message}");
                }
            }


            // -----------------------------------------------------------
            // QR Code scanning Strategy (Fallback Sequence)
            // -----------------------------------------------------------
            string decodedText = null;
            Point2f[] qrPoints = null;
            string method = string.Empty;

            try
            {
                if (_debugMode) Cv2.ImWrite(Path.Combine(debugPath, filePrefix + "1_original.png"), sourceFrame); // Debug mode

                // step 1 : Try detect Zxing
                method = "1.Original_ZXing";
                decodedText = DecodeQrWithZXing(sourceFrame, out qrPoints);
                if (string.IsNullOrEmpty(decodedText))
                {
                    // step 2 : Try detect OpenCV
                    method = "1.Original_OpenCV";
                    decodedText = DecodeQrWithOpenCV(sourceFrame, out qrPoints);

                    if (string.IsNullOrEmpty(decodedText) && _weChatQrDetector != null)
                    {
                        // setp 3 : Try detect WeChat
                        method = "1.Original_WeChatQRCode";
                        decodedText = DecodeQrWithWeChat(sourceFrame, out qrPoints);
                    }
                }

                // Try again with upper scale
                if (string.IsNullOrEmpty(decodedText))
                {
                    using (Mat upscaled = new Mat())
                    {
                        // up scale
                        Cv2.Resize(sourceFrame, upscaled, new Size(0, 0), _scaleFactor, _scaleFactor, InterpolationFlags.Cubic);

                        // step 1 : Try detect ZXing
                        method = "2.Upscaled_ZXing";
                        decodedText = DecodeQrWithZXing(upscaled, out qrPoints);
                        if (string.IsNullOrEmpty(decodedText))
                        {
                            // step 2 : Try detect OpenCV
                            method = "2.Upscaled_OpenCV";
                            decodedText = DecodeQrWithOpenCV(upscaled, out qrPoints);

                            if (string.IsNullOrEmpty(decodedText) && _weChatQrDetector != null)
                            {
                                // setp 3 : Try detect WeChat
                                method = "2.Upscaled_WeChatQRCode";
                                decodedText = DecodeQrWithWeChat(upscaled, out qrPoints);
                            }
                        }
                    }
                }


                // -----------------------------------------------------------
                // Result process...
                // -----------------------------------------------------------
                if (!string.IsNullOrEmpty(decodedText))
                {
                    result.IsDetected = true;
                    result.DecodedText = decodedText;
                    result.DetectionMethod = method;

                    // ROI 
                    result.CroppedLabel = sourceFrame.Clone();

                    // points covert (Point2f[] -> Point[])
                    if (qrPoints != null && qrPoints.Length >= 3)
                    {
                        // [개선] 화면 기준 정렬을 버리고 QR 고유 방향(물리적 위치) 고정
                        result.ResultPoints = NormalizeQrPoints(qrPoints);

                        // Center
                        Rect boundingRect = Cv2.BoundingRect(result.ResultPoints);
                        result.Center = new Point(
                            boundingRect.X + boundingRect.Width / 2,
                            boundingRect.Y + boundingRect.Height / 2
                        );

                        //
                        result.CroppedLabel = CropLabelArea(sourceFrame, result.ResultPoints);
                    }
                    else
                    {
                        // Fallback 좌표
                        result.ResultPoints = new Point[] {
                            new Point(0,0), new Point(sourceFrame.Width, 0),
                            new Point(sourceFrame.Width, sourceFrame.Height), new Point(0, sourceFrame.Height)
                        };
                        result.Center = new Point(sourceFrame.Width / 2, sourceFrame.Height / 2);
                        result.CroppedLabel = sourceFrame.Clone();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HbeamTracker] QR detector failed: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// QR 라벨을 검출하여 결과 정보만 반환합니다 (Drawing은 하지 않음)
        /// QR code scan procedure --> OpenCV 이미지 전처리 후 ZXing QR code 스캔.
        /// </summary>
        public LabelDetectionResult DetectOriginalSizeOnlyEachSteps(Mat sourceFrame)
        {
            var result = new LabelDetectionResult
            {
                IsDetected = false,
                DetectionMethod = "None",
                CroppedLabel = new Mat()
            };

            // 디버그 폴더 생성
            string debugPath = "";
            string filePrefix = "";
            if (_debugMode)
            {
                try
                {
                    string timestamp = DateTime.Now.ToString("yyyyMMdd");
                    debugPath = Path.Combine(_debugFolder, timestamp);
                    if (!Directory.Exists(debugPath))
                    {
                        Directory.CreateDirectory(debugPath);
                    }

                    // 파일명에 타임스탬프
                    filePrefix = DateTime.Now.ToString("HHmmss") + "_";
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Error] 디버그 폴더({debugPath}) 생성 예외: {ex.Message}");
                }
            }


            // -----------------------------------------------------------
            // QR Code scanning Strategy (Fallback Sequence)
            // -----------------------------------------------------------
            string decodedText = null;
            Point2f[] qrPoints = null;
            string method = "";

            try
            {
                if (_debugMode) Cv2.ImWrite(Path.Combine(debugPath, filePrefix + "1_original.png"), sourceFrame); // Debug mode

                // step 1 : Try detect Zxing
                method = "1.Original_ZXing";
                decodedText = DecodeQrWithZXing(sourceFrame, out qrPoints);
                if (string.IsNullOrEmpty(decodedText))
                {
                    // step 2 : Try detect OpenCV
                    method = "1.Original_OpenCV";
                    decodedText = DecodeQrWithOpenCV(sourceFrame, out qrPoints);

                    if (string.IsNullOrEmpty(decodedText) && _weChatQrDetector != null)
                    {
                        // setp 3 : Try detect WeChat
                        method = "1.Original_WeChatQRCode";
                        decodedText = DecodeQrWithWeChat(sourceFrame, out qrPoints);
                    }
                }

                // -----------------------------------------------------------
                // Result process...
                // -----------------------------------------------------------
                if (!string.IsNullOrEmpty(decodedText))
                {
                    result.IsDetected = true;
                    result.DecodedText = decodedText;
                    result.DetectionMethod = method;

                    // ROI 
                    result.CroppedLabel = sourceFrame.Clone();

                    // points covert (Point2f[] -> Point[])
                    if (qrPoints != null && qrPoints.Length >= 3)
                    {
                        // [개선] 화면 기준 정렬을 버리고 QR 고유 방향(물리적 위치) 고정
                        result.ResultPoints = NormalizeQrPoints(qrPoints);

                        // Center
                        Rect boundingRect = Cv2.BoundingRect(result.ResultPoints);
                        result.Center = new Point(
                            boundingRect.X + boundingRect.Width / 2,
                            boundingRect.Y + boundingRect.Height / 2
                        );

                        //
                        result.CroppedLabel = CropLabelArea(sourceFrame, result.ResultPoints);
                    }
                    else
                    {
                        // Fallback 좌표
                        result.ResultPoints = new Point[] {
                            new Point(0,0), new Point(sourceFrame.Width, 0),
                            new Point(sourceFrame.Width, sourceFrame.Height), new Point(0, sourceFrame.Height)
                        };
                        result.Center = new Point(sourceFrame.Width / 2, sourceFrame.Height / 2);
                        result.CroppedLabel = sourceFrame.Clone();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HbeamTracker] QR detector failed: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// ★ NEW: 딥러닝(WeChat) 및 구겨진 라벨에 특화된 가벼운 전처리 ★
        /// 이진화(Threshold)를 수행하면 구겨진 격자가 끊어지므로, Grayscale 질감을 그대로 유지하면서 대비만 향상시킵니다.
        /// </summary>
        public static Mat PreprocessMildForDeepLearning(Mat src)
        {
            Mat processed = new Mat();
            try
            {
                // 1. Grayscale 변환
                if (src.Channels() == 3)
                    Cv2.CvtColor(src, processed, ColorConversionCodes.BGR2GRAY);
                else
                    src.CopyTo(processed);

                // 2. 감마 보정 (Gamma Correction)
                // 밝게 날아간 영역의 대비를 회복 (gamma > 1.0 : 어두운 부분 강조)
                double gamma = 1.4;
                using (Mat lut = new Mat(1, 256, MatType.CV_8U))
                {
                    for (int i = 0; i < 256; i++)
                    {
                        double p = i / 255.0;
                        lut.Set<byte>(0, i, (byte)Math.Min(Math.Max(Math.Pow(p, gamma) * 255.0, 0), 255));
                    }
                    Cv2.LUT(processed, lut, processed);
                }

                // 3. 양방향 필터 (Bilateral Filter)
                // 금속 질감 노이즈는 줄이고 QR 코드 모듈의 경계선(Edge)은 보존
                using (Mat filtered = new Mat())
                {
                    Cv2.BilateralFilter(processed, filtered, d: 5, sigmaColor: 75, sigmaSpace: 75);
                    filtered.CopyTo(processed);
                }

                // 4. CLAHE (적응형 히스토그램 균일화)
                // 국지적인 대비 향상을 통해 굴곡진 부위의 식별력 강화
                using (var clahe = Cv2.CreateCLAHE(clipLimit: 3.5, tileGridSize: new Size(8, 8)))
                {
                    clahe.Apply(processed, processed);
                }

                // 5. 가벼운 샤프닝 (Unsharp Masking)
                // 굴곡에 의한 블러 현상 보정
                using (Mat blurred = new Mat())
                {
                    Cv2.GaussianBlur(processed, blurred, new Size(0, 0), 1.0);
                    Cv2.AddWeighted(processed, 1.3, blurred, -0.3, 0, processed);
                }

            }
            catch
            {
                if (processed.Empty()) src.CopyTo(processed);
            }
            return processed;
        }

        /// <summary>
        /// Image Pre-processing
        /// 1. Grayscale
        /// 2. CLAHE (Contrast Limited Adaptive Histogram Equalization)
        /// 3. Gaussian Blur (Reduce Noise)
        /// 4. Adaptive Threshold (Binary)
        /// 5. Morphology (Close - QR pattern improve)
        /// </summary>
        public static Mat PreprocessForQr(Mat src)
        {
            Mat processed = new Mat();
            Mat gray = new Mat();

            try
            {
                // Image size 
                if (src.Width > 1920)
                {
                    double scale = 1920.0 / src.Width;
                    Cv2.Resize(src, gray, new Size(0, 0), scale, scale, InterpolationFlags.Area);
                }
                else
                {
                    src.CopyTo(gray);
                }

                // 1. Grayscale 
                if (gray.Channels() == 3)   
                    Cv2.CvtColor(gray, gray, ColorConversionCodes.BGR2GRAY);

                // New: Gamma Correction 감마보정
                // 빛 번짐으로 하얗게 날아간(Washed-out) 라벨 영역의 숨은 픽셀(흐릿한 회색 패턴)을 강제로 어둡게 끌어내립니다.
                double gamma = 1.5; // 1.0 보다 큰 값을 주면 밝은 영역이 어두워지며 묻혀있던 디테일이 살아납니다.
                using (Mat lut = new Mat(1, 256, MatType.CV_8U))
                {
                    for (int i = 0; i < 256; i++)
                    {
                        double p = i / 255.0;
                        lut.Set<byte>(0, i, (byte)Math.Min(Math.Max(Math.Pow(p, gamma) * 255.0, 0), 255));
                    }
                    Cv2.LUT(gray, lut, gray);
                }

                // Updated: Black Top-Hat 알고리즘, 조명 균일화
                // 1. Close 연산으로 어두운 QR 패턴을 덮어버리고 순수한 '빛 반사 배경 조명 맵'만 추출합니다.
                // 2. 추출된 배경에서 원본을 빼서 국지적인 빛 반사를 상쇄시킵니다.
                using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(35, 35)))
                using (Mat background = new Mat())
                {
                    Cv2.MorphologyEx(gray, background, MorphTypes.Close, kernel); // 배경 조명 맵 생성
                    Cv2.Subtract(background, gray, gray); // 배경 - 원본 = 어두운 QR 패턴만 밝게 추출됨
                    Cv2.BitwiseNot(gray, gray); // 색상 반전 (QR을 다시 검게, 배경을 희게 복구)
                }

                // 2. CLAHE(조명 보정 및 대비 향상) : ClipLimit를 조절하여 과도한 노이즈 증폭 방지(2.0 ~ 4.0)
                using (var clahe = Cv2.CreateCLAHE(clipLimit: 4.0, tileGridSize: new Size(8, 8)))
                {
                    clahe.Apply(gray, gray);
                }

                //// 3. Gaussian Blur : 고주파 노이즈 제거 (진동 이미지 개선을 위해 제거)
                //Cv2.GaussianBlur(gray, gray, new Size(3, 3), 0);

                ////3.Bilateral Filter 적용(Gaussian Blur 대체)
                //// 블러는 노이즈와 함께 QR의 경계선도 뭉개버립니다. 
                //// 양방향 필터(Bilateral)를 사용하여 노이즈는 없애고 QR코드의 Edge(경계선)는 선명하게 유지합니다.
                ////using (Mat temp = new Mat())
                ////{
                ////d: 필터링에 사용할 픽셀 이웃 직경(일반적으로 5~9)
                ////     sigmaColor: 색상 공간의 표준편차(클수록 색이 섞임)
                ////     sigmaSpace: 좌표 공간의 표준편차
                ////    Cv2.BilateralFilter(gray, temp, d: 5, sigmaColor: 50, sigmaSpace: 50);
                ////    temp.CopyTo(gray);
                ////}

                // 3. Custom Sharpening (미세 진동 / 모션 블러 극복 위한)
                // 주변 픽셀과의 대비를 극대화 하여 흐릿한 경계선을 깎아냅니다.
                using (Mat kernel = new Mat(3, 3, MatType.CV_32F))
                {
                    float[] kernelData = new float[]
                    {
                         -1f, -1f, -1f,
                         -1f, 9f, -1f,
                         -1f, -1f, -1f
                    };
                    
                    kernel.SetArray(kernelData);

                    Cv2.Filter2D(gray, gray, MatType.CV_8U, kernel);
                }

                // 4. Unsharp Masking (샤픈처리 복구) : add '26.3.11
                // CCTV 렌즈의 포커스 아웃이나 흔들림으로 뭉개진 경계선을 날카롭게 복구
                using (Mat blurred = new Mat())
                {
                    Cv2.GaussianBlur(gray, blurred, new Size(0, 0), 2.0);
                    Cv2.AddWeighted(gray, 1.5, blurred, -0.5, 0, gray);
                }

                // 5. Adaptive Threshold (지역적 이진화)
                // BlockSize : QR 코드 셀 크기에 따라 조절 (홀수, 19~25 추천)
                // C: 평균값에서 뺄 상수 (노이즈 필터링 여고할, 2~5 추천)
                Cv2.AdaptiveThreshold(gray, processed,
                    maxValue: 255, 
                    adaptiveMethod: AdaptiveThresholdTypes.GaussianC, 
                    thresholdType: ThresholdTypes.Binary, 
                    blockSize: 21, 
                    c: 4);

            }
            catch (Exception)
            {
                // 오류발생 시 원본 번환
                if (processed.Empty()) gray.CopyTo(processed);
            }
            finally
            {
                gray.Dispose();
            }

            return processed;
        }


        /// <summary>
        /// Crop된 라벨 이미지에서 QR 코드를 디코딩합니다 (OpenCV)
        /// </summary>
        public string DecodeQrWithOpenCV(Mat img, out Point2f[] points)
        {
            points = null;
            try
            {
                // DetectAndDecode는 내부적으로 Detect를 수행하므로 좌표를 얻을 수 있음
                return _qrDetector.DetectAndDecode(img, out points);
            }
            catch { return null; }
        }

        /// <summary>
        /// Crop된 라벨 이미지에서 QR 코드를 디코딩합니다 (ZXing)
        /// </summary>
        public string DecodeQrWithZXing(Mat img, out Point2f[] points)
        {
            points = null;
            try
            {
                using (var bitmap = img.ToBitmap())
                {
                    var result = _barcodeReader.Decode(bitmap);

                    // ZXing 결과 좌표를 OpenCV Point2f로 변환
                    if (result != null && result.ResultPoints != null)
                    {
                        points = result.ResultPoints.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
                    }

                    return result?.Text;
                }
            }
            catch { return null; }
        }


        /// <summary>
        /// [NEW] WeChatQRCode 딥러닝 스캐너를 이용한 디코딩
        /// </summary>
        public string DecodeQrWithWeChat(Mat img, out Point2f[] points)
        {
            points = null;
            if (_weChatQrDetector == null || img == null || img.Empty()) return null; // 모델이 로드되지 않았으면 패스

            try
            {                
                Mat[]? ptsMats = null;
                string[]? results = null;
                _weChatQrDetector.DetectAndDecode(img, out ptsMats, out results);

                // Check result
                if (results != null && results.Length > 0 && !string.IsNullOrEmpty(results[0]))
                {
                    // Check 좌표
                    if (ptsMats != null && ptsMats.Length > 0)
                    {
                        Mat ptMat = ptsMats[0];
                        // 반환된 행렬이 4개의 모서리 좌표(4행 2열)를 가지는지 확인
                        if (ptMat != null && !ptMat.IsDisposed && ptMat.Rows >= 4 && ptMat.Cols >= 2)
                        {
                            points = new Point2f[4];
                            for (int i = 0; i < 4; i++)
                            {
                                points[i] = new Point2f(ptMat.At<float>(i, 0), ptMat.At<float>(i, 1));
                            }
                        }
                    }

                    // 메모리 누수 방지를 위해 Mat 배열 정리
                    foreach (var m in ptsMats) m.Dispose();

                    return results[0];
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WeChatQRCode] Decode Error: {ex.Message}");
            }
            return null;
        }


        /// <summary>
        /// ZXing의 ResultPoints를 분석하여 라벨의 물리적 방향에 따른 [Top-Left, Top-Right, Bottom-Right, Bottom-Left] 순서로 정규화합니다.
        /// </summary>
        private Point[] NormalizeQrPoints(Point2f[] zxingPoints)
        {
            List<Point2f> pts = zxingPoints.ToList();

            // 1. 3점만 있는 경우 (QR Finder Patterns) - 4번째 점 추정
            if (pts.Count == 3)
            {
                // ZXing 기본 순서: 0:BL, 1:TL, 2:TR 기준
                // Vector TL->TR를 BL에 더해 BR(3번점) 계산
                Point2f bl = pts[0];
                Point2f tl = pts[1];
                Point2f tr = pts[2];
                pts.Add(new Point2f(bl.X + (tr.X - tl.X), bl.Y + (tr.Y - tl.Y)));
            }

            // 2. 중심점 계산
            float cx = pts.Average(p => p.X);
            float cy = pts.Average(p => p.Y);

            // 3. 중심점 기준 각도(Atan2)로 정렬하여 항상 볼록 사각형 유지 (꼬임 방지)
            var sortedPts = pts
                .Select(p => new { Point = p, Angle = Math.Atan2(p.Y - cy, p.X - cx) })
                .OrderBy(a => a.Angle)
                .Select(a => new Point((int)Math.Round(a.Point.X), (int)Math.Round(a.Point.Y)))
                .ToArray();

            // 4. 시작점을 '좌측 상단에 가장 가까운 점'으로 회전시켜 출력 순서 일관성 부여
            // (화면 좌표 0,0에 가장 가까운 점을 0번 인덱스로)
            int startIndex = 0;
            double minDist = double.MaxValue;
            for (int i = 0; i < sortedPts.Length; i++)
            {
                double dist = Math.Pow(sortedPts[i].X, 2) + Math.Pow(sortedPts[i].Y, 2);
                if (dist < minDist)
                {
                    minDist = dist;
                    startIndex = i;
                }
            }

            // 인덱스 재정렬 (startIndex부터 시작하도록)
            Point[] finalPoints = new Point[sortedPts.Length];
            for (int i = 0; i < sortedPts.Length; i++)
            {
                finalPoints[i] = sortedPts[(startIndex + i) % sortedPts.Length];
            }

            return finalPoints;
        }

        // Helper: 안전한 Crop 영역 계산
        private Rect GetSafeCropRect(Rect baseRect, int imgW, int imgH, int margin)
        {
            int x = Math.Max(0, baseRect.X - margin);
            int y = Math.Max(0, baseRect.Y - margin);
            int w = Math.Min(imgW - x, baseRect.Width + margin * 2);
            int h = Math.Min(imgH - y, baseRect.Height + margin * 2);
            return new Rect(x, y, w, h);
        }

        /// <summary>
        /// QR 코드 위치를 기반으로 이미지를 여유 있게 크롭합니다. (오류 방지 로직 대폭 강화)
        /// </summary>
        private Mat CropLabelArea(Mat source, Point[] points)
        {
            if (points == null || points.Length == 0) return source.Clone();

            try
            {
                Rect qrRect = Cv2.BoundingRect(points);
                int centerX = qrRect.X + qrRect.Width / 2;
                int centerY = qrRect.Y + qrRect.Height / 2;

                // QR 코드 크기의 3.5배 영역으로 여유 있게 크롭
                int expandWidth = (int)(qrRect.Width * 3.5);
                int expandHeight = (int)(qrRect.Height * 3.5);

                int cropX = centerX - (expandWidth / 2);
                int cropY = centerY - (expandHeight / 2);

                int x1 = Math.Max(0, cropX);
                int y1 = Math.Max(0, cropY);
                int x2 = Math.Min(source.Width, cropX + expandWidth);
                int y2 = Math.Min(source.Height, cropY + expandHeight);

                int finalWidth = x2 - x1;
                int finalHeight = y2 - y1;

                // 이미지를 벗어난 잘못된 크기가 나올 경우 안전하게 원본 복사
                if (finalWidth <= 0 || finalHeight <= 0 || x1 >= source.Width || y1 >= source.Height)
                {
                    return source.Clone();
                }

                Rect finalCropRect = new Rect(x1, y1, finalWidth, finalHeight);
                return new Mat(source, finalCropRect).Clone();
            }
            catch
            {
                // 어떤 예외상황(오류)이 발생해도 프로그램이 멈추거나 UI가 사라지지 않고 원본 프레임을 반환
                return source.Clone();
            }
        }

        public bool IsDebugMode() => _debugMode;
        public string GetDebugFolder() => _debugFolder;

        public void Dispose()
        {
            _gray?.Dispose();
            _blurred?.Dispose();
            _edges?.Dispose();
            _dilated?.Dispose();
            _kernel?.Dispose();
            _qrDetector?.Dispose();
        }
    }
}