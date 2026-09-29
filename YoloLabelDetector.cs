using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using NLog;

namespace PYV_MaterialScanner
{
    public class YoloLabelDetector : IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private readonly InferenceSession? _session;
        private readonly int _inputSize = 640;
        private readonly float _confidenceThreshold = 0.45f;

        public bool IsLoaded => _session != null;

        public YoloLabelDetector(string modelPath)
        {
            try
            {
                if (!File.Exists(modelPath))
                {
                    Log.Warn($"[YOLO] 모델 파일을 찾을 수 없습니다: {modelPath}");
                    return;
                }

                var sessionOptions = new SessionOptions();
                sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                // ★ [중요!] ONNX가 CPU 전체를 독점하지 않도록 스레드 수 제한 (2~3개 추천)
                sessionOptions.IntraOpNumThreads = 4; // 모델 연산 내부 병렬 스레드 수
                sessionOptions.InterOpNumThreads = 1; // 모델 그래프 간 병렬 스레드 수

                _session = new InferenceSession(modelPath, sessionOptions);
                Log.Info($"[YOLO] 라벨 검출 모델 로드 성공: {modelPath}");
            }
            catch (Exception ex)
            {
                Log.Error($"[YOLO] 모델 로드 실패: {ex.Message}");
            }
        }
        /// <summary>
        /// 4K 원본 프레임에서 딥러닝으로 라벨 영역 좌표(Rect)를 찾아 반환
        /// </summary>
        public Rect? DetectLabel(Mat sourceFrame)
        {
            if (_session == null || sourceFrame == null || sourceFrame.Empty()) return null;

            int origW = sourceFrame.Width;
            int origH = sourceFrame.Height;

            try
            {
                // 1. 모델 입력 크기(640x640)로 변환
                using Mat resized = new Mat();
                Cv2.Resize(sourceFrame, resized, new OpenCvSharp.Size(_inputSize, _inputSize));

                // 2. BGR -> RGB 변환 및 0~1 정규화 Float Tensor 생성 [1, 3, 640, 640]
                var inputTensor = new DenseTensor<float>(new[] { 1, 3, _inputSize, _inputSize });
                Parallel.For(0, _inputSize, y =>
                {
                    for (int x = 0; x < _inputSize; x++)
                    {
                        Vec3b color = resized.At<Vec3b>(y, x);
                        inputTensor[0, 0, y, x] = color.Item2 / 255.0f; // R
                        inputTensor[0, 1, y, x] = color.Item1 / 255.0f; // G
                        inputTensor[0, 2, y, x] = color.Item0 / 255.0f; // B
                    }
                });

                // 3. ONNX 추론 실행
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), inputTensor)
                };

                using var results = _session.Run(inputs);
                var output = results.First().AsTensor<float>();

                // 4. YOLOv8 출력 해석 [1, 5, 8400] -> (cx, cy, w, h, score)
                float maxScore = 0f;
                Rect? bestRect = null;
                float scaleX = (float)origW / _inputSize;
                float scaleY = (float)origH / _inputSize;

                int numAnchors = output.Dimensions[2]; // 8400
                for (int i = 0; i < numAnchors; i++)
                {
                    float conf = output[0, 4, i]; // label 클래스 확률
                    if (conf > _confidenceThreshold && conf > maxScore)
                    {
                        maxScore = conf;
                        float cx = output[0, 0, i] * scaleX;
                        float cy = output[0, 1, i] * scaleY;
                        float w = output[0, 2, i] * scaleX;
                        float h = output[0, 3, i] * scaleY;

                        int x = (int)(cx - (w / 2f));
                        int y = (int)(cy - (h / 2f));

                        // 화면 경계선 보정
                        x = Math.Max(0, x);
                        y = Math.Max(0, y);
                        int finalW = Math.Min(origW - x, (int)w);
                        int finalH = Math.Min(origH - y, (int)h);

                        bestRect = new Rect(x, y, finalW, finalH);
                    }
                }

                // 5. 검출된 영역에 20% 마진을 부여하여 반환 (QR 테두리 여백 보호)
                if (bestRect.HasValue)
                {
                    Rect r = bestRect.Value;
                    r.Inflate((int)(r.Width * 0.05), (int)(r.Height * 0.05));
                    r.X = Math.Max(0, r.X);
                    r.Y = Math.Max(0, r.Y);
                    r.Width = Math.Min(origW - r.X, r.Width);
                    r.Height = Math.Min(origH - r.Y, r.Height);
                    return r;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[YOLO Detect Error] {ex.Message}");
            }

            return null;
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}
