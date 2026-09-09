using NLog;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PYV_MaterialScanner
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : System.Windows.Window
    {
        private static readonly Logger log = LogManager.GetCurrentClassLogger();

        // ROI 드래그 상태 변수
        private bool _isDragging = false;
        private System.Windows.Point _startPoint;
        private System.Windows.Point _endPoint;

        public MainWindow()
        {
            InitializeComponent();

            // MainWindow의 DataContext를 MainViewModel 인스턴스로 설정
            this.DataContext = new MainViewModel();

        }

        // 윈도우가 닫힐 때 리소스 정리
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (this.DataContext is MainViewModel viewModel)
            {
                // 캡처 루프를 종료하는 로직을 ViewModel에 추가하여 호출할 수 있습니다.
                viewModel.StopStreaming();
                viewModel.Dispose();
            }
            // OpenCV 관련 리소스가 정리될 시간을 약간 벌어주는 것이 좋을 때가 있음
            System.Threading.Thread.Sleep(200);
        }

        private void CropRatioTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // 눌린 키가 Enter 키인지 확인합니다.
            if (e.Key == Key.Enter)
            {
                // sender를 TextBox로 캐스팅합니다.
                if (sender is TextBox textBox)
                {
                    // TextBox의 TextProperty에 대한 바인딩 표현식을 가져옵니다.
                    BindingExpression bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);

                    if (bindingExpression != null)
                    {
                        // 바인딩 소스(ViewModel의 프로퍼티)를 강제로 업데이트합니다.
                        // LostFocus 이벤트가 발생한 것과 동일한 효과를 줍니다.
                        bindingExpression.UpdateSource();
                    }

                    // Enter 키를 누른 후 TextBox에서 포커스를 제거하여
                    // 입력이 완료되었음을 시각적으로 보여줍니다.
                    Keyboard.ClearFocus();
                }
            }
        }

        // ROI 드래그 시작
        private void EditRoiButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.DataContext is MainViewModel viewModel)
            {
                viewModel.IsRoiEditMode = !viewModel.IsRoiEditMode;

                if (!viewModel.IsRoiEditMode)
                {
                    // ROI 편집 모드 종료 시 선택 영역 초기화
                    SelectionRectangle.Visibility = Visibility.Collapsed;
                    HideHandles();
                }
                else
                {
                    log.Info("ROI Edit Mode activated. Drag on video to select area.");
                }

            }
        }

        // ROI 드래그 시작 이벤트 핸들러
        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (this.DataContext is MainViewModel viewModel && viewModel.IsRoiEditMode)
            {
                _isDragging = true;
                _startPoint = e.GetPosition(ROICanvas);

                // Rectangle 초기화
                Canvas.SetLeft(SelectionRectangle, _startPoint.X);
                Canvas.SetTop(SelectionRectangle, _startPoint.Y);
                SelectionRectangle.Width = 0;
                SelectionRectangle.Height = 0;
                SelectionRectangle.Visibility = Visibility.Visible;

                ROICanvas.CaptureMouse();
            }
        }

        // ROI 드래그 중 사각형 그리기
        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging && this.DataContext is MainViewModel viewModel && viewModel.IsRoiEditMode)
            {
                _endPoint = e.GetPosition(ROICanvas);

                double x = Math.Min(_startPoint.X, _endPoint.X);
                double y = Math.Min(_startPoint.Y, _endPoint.Y);
                double width = Math.Abs(_endPoint.X - _startPoint.X);
                double height = Math.Abs(_endPoint.Y - _startPoint.Y);

                Canvas.SetLeft(SelectionRectangle, x);
                Canvas.SetTop(SelectionRectangle, y);
                SelectionRectangle.Width = width;
                SelectionRectangle.Height = height;

                // 핸들 위치 업데이트
                UpdateHandles(x, y, width, height);
            }
        }

        // ROI 드래그 종료
        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging && this.DataContext is MainViewModel viewModel)
            {
                _isDragging = false;
                ROICanvas.ReleaseMouseCapture();

                // 최종 위치 계산
                _endPoint = e.GetPosition(ROICanvas);

                double x = Math.Min(_startPoint.X, _endPoint.X);
                double y = Math.Min(_startPoint.Y, _endPoint.Y);
                double width = Math.Abs(_endPoint.X - _startPoint.X);
                double height = Math.Abs(_endPoint.Y - _startPoint.Y);

                // 최소 크기 체크 (너무 작은 영역 무시)
                if (width < 20 || height < 20)
                {
                    SelectionRectangle.Visibility = Visibility.Collapsed;
                    HideHandles();
                    log.Info("ROI selection too small, ignored.");
                    return;
                }

                // Canvas 크기 가져오기 (실제 비디오 표시 영역)
                double canvasWidth = ROICanvas.ActualWidth;
                double canvasHeight = ROICanvas.ActualHeight;

                if (canvasWidth <= 0 || canvasHeight <= 0)
                {
                    log.Warn("Canvas size is zero, cannot calculate ROI.");
                    return;
                }

                // 픽셀 좌표를 비율(%)로 변환
                int topPercent = (int)Math.Round((y / canvasHeight) * 100);
                int leftPercent = (int)Math.Round((x / canvasWidth) * 100);
                int bottomPercent = (int)Math.Round(((canvasHeight - (y + height)) / canvasHeight) * 100);
                int rightPercent = (int)Math.Round(((canvasWidth - (x + width)) / canvasWidth) * 100);

                // 범위 제한 (0~70%)
                topPercent = Math.Max(0, Math.Min(70, topPercent));
                bottomPercent = Math.Max(0, Math.Min(70, bottomPercent));
                leftPercent = Math.Max(0, Math.Min(70, leftPercent));
                rightPercent = Math.Max(0, Math.Min(70, rightPercent));

                // ViewModel에 적용
                viewModel.CropRatioTop = topPercent;
                viewModel.CropRatioBtm = bottomPercent;
                viewModel.CropRatioLeft = leftPercent;
                viewModel.CropRatioRight = rightPercent;

                log.Info($"ROI applied - Top:{topPercent}%, Bottom:{bottomPercent}%, Left:{leftPercent}%, Right:{rightPercent}%");

                // 편집 모드 자동 종료 (원하면 주석 처리)
                viewModel.IsRoiEditMode = false;
                SelectionRectangle.Visibility = Visibility.Collapsed;
                HideHandles();
            }
        }

        // 코너 핸들 위치 업데이트
        private void UpdateHandles(double x, double y, double width, double height)
        {
            const double handleOffset = 6; // 핸들 중심 오프셋

            Canvas.SetLeft(HandleTopLeft, x - handleOffset);
            Canvas.SetTop(HandleTopLeft, y - handleOffset);

            Canvas.SetLeft(HandleTopRight, x + width - handleOffset);
            Canvas.SetTop(HandleTopRight, y - handleOffset);

            Canvas.SetLeft(HandleBottomLeft, x - handleOffset);
            Canvas.SetTop(HandleBottomLeft, y + height - handleOffset);

            Canvas.SetLeft(HandleBottomRight, x + width - handleOffset);
            Canvas.SetTop(HandleBottomRight, y + height - handleOffset);

            HandleTopLeft.Visibility = Visibility.Visible;
            HandleTopRight.Visibility = Visibility.Visible;
            HandleBottomLeft.Visibility = Visibility.Visible;
            HandleBottomRight.Visibility = Visibility.Visible;
        }

        private void HideHandles()
        {
            HandleTopLeft.Visibility = Visibility.Collapsed;
            HandleTopRight.Visibility = Visibility.Collapsed;
            HandleBottomLeft.Visibility = Visibility.Collapsed;
            HandleBottomRight.Visibility = Visibility.Collapsed;
        }

        /// Bool to Color Converter for Edit ROI button
        public class BoolToColorConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is bool isEditMode && isEditMode)
                {
                    return new SolidColorBrush(Colors.LightGreen);
                }
                return new SolidColorBrush(Colors.LightGray);
            }

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                throw new NotImplementedException();
            }
        }
    }
}