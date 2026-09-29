#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8625
using OpenCvSharp;
using OpenCvSharp.Extensions;
using ZXing;
using ZXing.Common;
using ZXing.Windows.Compatibility;
using NLog;
using System.IO;
using System.Configuration;


namespace PYV_MaterialScanner
{
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
    /// 딥러닝 WeChat + 기존 이진화/모폴로지 전처리 + ZXing 융합 트래커
    /// </summary>
    internal class HbeamTracker : IDisposable
    {
        // Nlog 로거 선언 (로그 레벨: Trace, Debug, Info, Warn, Error, Fatal)
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        // YOLO 디텍터 추가
        private YoloLabelDetector? _yoloDetector;

        // QR 코드 디텍터
        private QRCodeDetector _qrDetector = new QRCodeDetector();
        private readonly BarcodeReader _barcodeReader;
        private WeChatQRCode? _weChatQrDetector; //weChat

        // 디버그 모드
        private volatile bool _debugMode = false;
        private string _debugFolder = @"C:\Debug";


        public HbeamTracker()
        {
            // initialize ZXing Barcode Reader
            _barcodeReader = new BarcodeReader
            {
                AutoRotate = false,
                Options = new DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new[] { BarcodeFormat.QR_CODE }
                }
            };

            // App.config 설정 로드
            string? debugModeConfig = ConfigurationManager.AppSettings["IsDebugMode"];
            if (!string.IsNullOrEmpty(debugModeConfig) && bool.TryParse(debugModeConfig, out bool isDebug))
            {
                _debugMode = isDebug;
            }

            // app.config에서 디버그 폴더 경로 읽기 (선택사항)
            string? debugFolderConfig = ConfigurationManager.AppSettings["QRLabelDebugFolder"];
            if (!string.IsNullOrEmpty(debugFolderConfig))
            {
                _debugFolder = debugFolderConfig;
            }
            ////////////////////////////////////////////////////////////
            // WeChatQRCode 4대 Caffe 딥러닝 모델 로드
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
                    _weChatQrDetector = WeChatQRCode.Create(detectProto, detectModel, srProto, srModel);
                    Log.Info("[WeChatQRCode] Caffe 딥러닝 모델 로드 성공!");
                }
                else
                {
                    Log.Warn("[WeChatQRCode] Models 폴더 내 모델 파일이 누락되어 비활성화되었습니다.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[WeChatQRCode] 모델 초기화 실패");
            }

            ////////////////////////////////////////////////////////////
            // YOLO 디텍터 초기화
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string yoloModelPath = Path.Combine(baseDir, "Models", "label_detector.onnx");
                _yoloDetector = new YoloLabelDetector(yoloModelPath);
            }
            catch (Exception ex)
            {
                Log.Error($"[HbeamTracker] YOLO 디텍터 생성 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 딥러닝 YOLOv8n ONNX 추론을 통해 4K 영상에서 종이 라벨 구역을 검출
        /// </summary>
        public Rect? DetectWhiteLableArea(Mat sourceFrame)
        {
            if (_yoloDetector != null && _yoloDetector.IsLoaded)
            {
                return _yoloDetector.DetectLabel(sourceFrame);
            }
            return null;
        }


        ///////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [단계별 종합 검출 파이프라인]
        /// Step 1: 원본 해상도 (WeChat -> ZXing -> 180도 회전)
        /// Step 2: Mild 전처리 (그레이스케일 + CLAHE + 샤프닝 -> WeChat)
        /// Step 3: [기존 복원] 강력한 적응형 이진화 + 조명균일화 (PreprocessForQr -> ZXing)
        /// Step 4: Perspective Warping 투영 복구
        /// </summary>
        ///////////////////////////////////////////////////////////////////////////
        public LabelDetectionResult DetectQR(Mat sourceFrame)
        {
            var result = new LabelDetectionResult
            {
                IsDetected = false,
                DetectionMethod = "None",
                CroppedLabel = null
            };

            if (sourceFrame == null || sourceFrame.Empty()) return result;

            string? decodedText = null;
            Point2f[]? qrPoints = null;
            string method = string.Empty;

            try
            {
                // -------------------------------------------------------------
                // Step 1: 원본 해상도 우선 시도 (0도 -> 180도 회전)
                // -------------------------------------------------------------
                decodedText = TryDecodeWithRotation(sourceFrame, "1.Original", useWeChat: true, out qrPoints, out method);

                // -------------------------------------------------------------
                // Step 2: Mild 전처리 (딥러닝 친화적 음영 유지 + 2배 업스케일)
                // -------------------------------------------------------------
                if (string.IsNullOrEmpty(decodedText))
                {
                    using (Mat mild = PreprocessMildForDeepLearning(sourceFrame))
                    {
                        decodedText = TryDecodeWithRotation(mild, "2.Mild", useWeChat: true, out qrPoints, out method);
                    }
                }


                // -------------------------------------------------------------
                // Step 3: [기존 핵심 복원] 강력한 이진화 + 조명 제거 (PreprocessForQr)
                // 넓은 영역의 쇳덩이/롤러 노이즈를 전부 하얗게 날리고 QR 흑백 패턴만 남겨 ZXing에 전달
                // -------------------------------------------------------------
                if (string.IsNullOrEmpty(decodedText))
                {
                    using (Mat binaryProcessed = PreprocessForQr(sourceFrame))
                    {
                        decodedText = TryDecodeWithRotation(binaryProcessed, "3.Binary", useWeChat: false, out qrPoints, out method);
                    }
                }

                // -------------------------------------------------------------
                // Step 4: Perspective Warping 복구 (외곽 좌표는 찾았으나 해독이 덜 된 경우)
                // -------------------------------------------------------------
                if (string.IsNullOrEmpty(decodedText) && qrPoints != null && qrPoints.Length >= 3)
                {
                    decodedText = RetryWithWarping(sourceFrame, qrPoints);
                    if (!string.IsNullOrEmpty(decodedText)) method = "4.Warping_Recovery";
                }

                // -------------------------------------------------------------
                // 결과 패키징 및 좌표 복원
                // -------------------------------------------------------------
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

                        // UI 프리뷰용 라벨 영역 5.5배 크롭
                        result.CroppedLabel = CropLabelArea(sourceFrame, result.ResultPoints);
                    }
                    else
                    {
                        result.ResultPoints = new Point[]
                        {
                            new Point(0, 0), new Point(sourceFrame.Width, 0),
                            new Point(sourceFrame.Width, sourceFrame.Height), new Point(0, sourceFrame.Height)
                        };
                        result.Center = new Point(sourceFrame.Width / 2, sourceFrame.Height / 2);
                        result.CroppedLabel = sourceFrame.Clone();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[HbeamTracker] QR detection error: {ex.Message}");
            }

            if (result.CroppedLabel == null) result.CroppedLabel = new Mat();
            return result;
        }

        /// <summary>
        /// 넓은 화면에서 쇳덩이 노이즈를 지우고 순수 QR 패턴만 남기는 핵심 전처리
        /// </summary>
        public static Mat PreprocessForQr(Mat src)
        {
            Mat gray = new Mat();
            if (src.Channels() == 3)
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            else
                src.CopyTo(gray);

            // 1. CLAHE를 통한 국지적 명암 대비 극대화
            // 그림자 진 부분의 텍스트는 뚜렷해지고 너무 밝은 금속 반사 부위의 대비는 조절
            using (var clahe = Cv2.CreateCLAHE(clipLimit: 2.5, tileGridSize: new Size(8, 8)))
            {
                clahe.Apply(gray, gray);
            }

            // 2. 조명 불균일 상쇄 (Black Top-Hat 변형: Close 연산 후 차감)
            using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(21, 21)))
            using (Mat background = new Mat())
            {
                Cv2.MorphologyEx(gray, background, MorphTypes.Close, kernel);
                Cv2.Subtract(background, gray, gray);
                Cv2.BitwiseNot(gray, gray);
            }

            // 3. 지역 적응형 이진화 (Adaptive Threshold) - 쇳덩이 표면을 백색으로 정리
            Mat binary = new Mat();
            Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 21, 4);

            gray.Dispose();
            return binary;
        }

        /// <summary>
        /// 0도, 90도, 180도, 270도 4방향을 순차적으로 회전 탐색하고 검출 좌표를 원래 좌표계로 역산 복원.
        /// </summary>
        private string? TryDecodeWithRotation(Mat frame, string stepPrefix, bool useWeChat, out Point2f[]? points, out string method)
        {
            points = null;
            method = string.Empty;

            // 회전 단계 정의: (라벨, 회전 플래그, 역산용 가로, 역산용 세로)
            var rotateSteps = new (string Suffix, RotateFlags? Flag)[]
            {
                ("", null),                                      // 0도 (회전 없음)
                ("_Rot90",  RotateFlags.Rotate90Clockwise),      // 90도 시계방향
                ("_Rot180", RotateFlags.Rotate180),              // 180도 반전
                ("_Rot270", RotateFlags.Rotate90Counterclockwise)// 270도 (90도 반시계)
            };

            foreach (var (suffix, flag) in rotateSteps)
            {
                using Mat currentMat = flag.HasValue ? new Mat() : frame.Clone();
                if (flag.HasValue)
                {
                    Cv2.Rotate(frame, currentMat, flag.Value);
                }

                string currentStepName = $"{stepPrefix}{suffix}";
                string? text = TryDecodeSequence(currentMat, currentStepName, useWeChat, out Point2f[]? detectedPts, out method);

                if (!string.IsNullOrEmpty(text))
                {
                    // 검출된 좌표가 있다면 회전 상태에 따라 원본 frame 좌표계로 역변환
                    if (detectedPts != null && detectedPts.Length > 0)
                    {
                        points = new Point2f[detectedPts.Length];
                        for (int i = 0; i < detectedPts.Length; i++)
                        {
                            float x = detectedPts[i].X;
                            float y = detectedPts[i].Y;

                            if (!flag.HasValue) // 0도
                            {
                                points[i] = new Point2f(x, y);
                            }
                            else if (flag.Value == RotateFlags.Rotate90Clockwise) // 90도 회전 좌표 역산
                            {
                                points[i] = new Point2f(frame.Width - 1 - y, x);
                            }
                            else if (flag.Value == RotateFlags.Rotate180) // 180도 회전 좌표 역산
                            {
                                points[i] = new Point2f(frame.Width - 1 - x, frame.Height - 1 - y);
                            }
                            else if (flag.Value == RotateFlags.Rotate90Counterclockwise) // 270도 회전 좌표 역산
                            {
                                points[i] = new Point2f(y, frame.Height - 1 - x);
                            }
                        }
                    }
                    return text;
                }
            }
            return null;
        }

        /// <summary>
        /// WeChat -> ZXing -> OpenCV 순차 디코딩 실행
        /// </summary>
        private string? TryDecodeSequence(Mat targetFrame, string stepPrefix, bool useWeChat, out Point2f[]? points, out string successfulMethod)
        {
            points = null;
            successfulMethod = "None";

            // 1. WeChatQRCode (딥러닝)
            if (useWeChat && _weChatQrDetector != null)
            {
                string? text = DecodeQrWithWeChat(targetFrame, out points);
                if (!string.IsNullOrEmpty(text))
                {
                    successfulMethod = $"{stepPrefix}_WeChat";
                    return text;
                }
            }

            // 2. ZXing
            string? zxingText = DecodeQrWithZXing(targetFrame, out points);
            if (!string.IsNullOrEmpty(zxingText))
            {
                successfulMethod = $"{stepPrefix}_ZXing";
                return zxingText;
            }

            // 3. OpenCV 기본 디텍터
            string? cvText = DecodeQrWithOpenCV(targetFrame, out points);
            if (!string.IsNullOrEmpty(cvText))
            {
                successfulMethod = $"{stepPrefix}_OpenCV";
                return cvText;
            }

            return null;
        }

        /// <summary>
        /// WeChat 디코딩 단독 호출
        /// </summary>
        public string? DecodeQrWithWeChat(Mat img, out Point2f[]? points)
        {
            points = null;
            if (_weChatQrDetector == null || img == null || img.Empty()) return null;

            try
            {
                _weChatQrDetector.DetectAndDecode(img, out Mat[]? ptsMats, out string[]? results);

                if (results != null && results.Length > 0 && !string.IsNullOrEmpty(results[0]))
                {
                    if (ptsMats != null && ptsMats.Length > 0)
                    {
                        Mat ptMat = ptsMats[0];
                        if (ptMat != null && !ptMat.IsDisposed && ptMat.Rows >= 4 && ptMat.Cols >= 2)
                        {
                            points = new Point2f[4];
                            for (int i = 0; i < 4; i++)
                            {
                                points[i] = new Point2f(ptMat.At<float>(i, 0), ptMat.At<float>(i, 1));
                            }
                        }
                    }

                    if (ptsMats != null)
                    {
                        foreach (var m in ptsMats) m.Dispose();
                    }

                    return results[0];
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WeChatQRCode] 디코딩 오류: {ex.Message}");
            }
            return null;
        }

        public string? DecodeQrWithZXing(Mat img, out Point2f[]? points)
        {
            points = null;
            try
            {
                using var bitmap = img.ToBitmap();
                var zxResult = _barcodeReader.Decode(bitmap);
                if (zxResult != null && zxResult.ResultPoints != null)
                {
                    points = zxResult.ResultPoints.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
                }
                return zxResult?.Text;
            }
            catch { return null; }
        }

        public string? DecodeQrWithOpenCV(Mat img, out Point2f[]? points)
        {
            points = null;
            try
            {
                return _qrDetector.DetectAndDecode(img, out points);
            }
            catch { return null; }
        }

        /// <summary>
        /// 딥러닝 친화적 전처리 (이진화 없이 그레이스케일 음영 + 대비 극대화)
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

                // 2. 2배 확대 (작은 QR 모듈 픽셀 확보)
                if (processed.Width < 1500)
                {
                    Cv2.Resize(processed, processed, new OpenCvSharp.Size(0, 0), 2.0, 2.0, InterpolationFlags.Cubic);
                }


                // 3. CLAHE 적응형 대비 향상
                using (var clahe = Cv2.CreateCLAHE(clipLimit: 3.5, tileGridSize: new OpenCvSharp.Size(8, 8)))
                {
                    clahe.Apply(processed, processed);
                }

                // 4. 모듈 경계선을 날카롭게 세우는 Unsharp Masking
                using Mat blurred = new Mat();
                Cv2.GaussianBlur(processed, blurred, new Size(0, 0), 1.2);
                Cv2.AddWeighted(processed, 1.3, blurred, -0.3, 0, processed);
            }
            catch
            {
                if (processed.Empty()) src.CopyTo(processed);
            }
            return processed;
        }

        /// <summary>
        /// Perspective Warping을 이용한 왜곡 복원 디코딩
        /// </summary>
        private string? RetryWithWarping(Mat src, Point2f[] pts)
        {
            try
            {
                Size warpSize = new Size(512, 512);
                Point2f[] destPts = {
                    new Point2f(0, 0), new Point2f(512, 0),
                    new Point2f(512, 512), new Point2f(0, 512)
                };

                using Mat matrix = Cv2.GetPerspectiveTransform(pts, destPts);
                using Mat warped = new Mat();
                Cv2.WarpPerspective(src, warped, matrix, warpSize);

                using var clahe = Cv2.CreateCLAHE(3.0, new Size(8, 8));
                using Mat gray = warped.Channels() == 3 ? warped.CvtColor(ColorConversionCodes.BGR2GRAY) : warped.Clone();
                clahe.Apply(gray, gray);

                return DecodeQrWithWeChat(gray, out _);
            }
            catch { return null; }
        }

        /// <summary>
        /// 결과 표시용 영역 크롭 (QR 크기의 5.5배)
        /// </summary>
        private Mat CropLabelArea(Mat source, Point[] points)
        {
            if (points == null || points.Length == 0) return source.Clone();

            try
            {
                Rect qrRect = Cv2.BoundingRect(points);
                int centerX = qrRect.X + qrRect.Width / 2;
                int centerY = qrRect.Y + qrRect.Height / 2;

                int expandWidth = (int)(qrRect.Width * 5.5);
                int expandHeight = (int)(qrRect.Height * 5.5);

                int cropX = centerX - (expandWidth / 2);
                int cropY = centerY - (expandHeight / 2);

                int x1 = Math.Max(0, cropX);
                int y1 = Math.Max(0, cropY);
                int x2 = Math.Min(source.Width, cropX + expandWidth);
                int y2 = Math.Min(source.Height, cropY + expandHeight);

                int finalWidth = x2 - x1;
                int finalHeight = y2 - y1;

                if (finalWidth <= 0 || finalHeight <= 0 || x1 >= source.Width || y1 >= source.Height)
                {
                    return source.Clone();
                }

                Rect finalCropRect = new Rect(x1, y1, finalWidth, finalHeight);
                return new Mat(source, finalCropRect).Clone();
            }
            catch
            {
                return source.Clone();
            }
        }

        /// <summary>
        /// 폴리곤 꼭짓점 순서 정규화 (꼬임 방지)
        /// </summary>
        private Point[] NormalizeQrPoints(Point2f[] zxingPoints)
        {
            List<Point2f> pts = zxingPoints.ToList();

            if (pts.Count == 3)
            {
                Point2f bl = pts[0];
                Point2f tl = pts[1];
                Point2f tr = pts[2];
                pts.Add(new Point2f(bl.X + (tr.X - tl.X), bl.Y + (tr.Y - tl.Y)));
            }

            float cx = pts.Average(p => p.X);
            float cy = pts.Average(p => p.Y);

            var sortedPts = pts
                .Select(p => new { Point = p, Angle = Math.Atan2(p.Y - cy, p.X - cx) })
                .OrderBy(a => a.Angle)
                .Select(a => new Point((int)Math.Round(a.Point.X), (int)Math.Round(a.Point.Y)))
                .ToArray();

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

            Point[] finalPoints = new Point[sortedPts.Length];
            for (int i = 0; i < sortedPts.Length; i++)
            {
                finalPoints[i] = sortedPts[(startIndex + i) % sortedPts.Length];
            }

            return finalPoints;
        }

        public void Dispose()
        {
            _yoloDetector?.Dispose();
            _qrDetector?.Dispose();
            _weChatQrDetector?.Dispose();
        }

    }
}



