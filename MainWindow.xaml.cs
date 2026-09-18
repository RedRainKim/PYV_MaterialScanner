using NLog;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;


namespace PYV_MaterialScanner
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : System.Windows.Window
    {
        private static readonly Logger log = LogManager.GetCurrentClassLogger();

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

        private void ZoomButton_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (this.DataContext is MainViewModel vm)
            {
                vm.ZoomStopCommand.Execute(null);
            }
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