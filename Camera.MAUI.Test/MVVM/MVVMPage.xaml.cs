using static Camera.MAUI.CameraView;

namespace Camera.MAUI.Test;

public partial class MVVMPage : ContentPage
{
    public MVVMPage()
    {
        InitializeComponent();
    }

    private void cameraView_FrameReceived(object sender, FrameEventArgs e)
    {
        frameImage.Source = ImageSource.FromStream(() => new MemoryStream(e.Bytes));
    }
}