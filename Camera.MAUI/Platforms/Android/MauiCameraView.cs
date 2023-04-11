﻿using Android.Content;
using Android.Widget;
using Java.Util.Concurrent;
using Android.Graphics;
using CameraCharacteristics = Android.Hardware.Camera2.CameraCharacteristics;
using Android.Hardware.Camera2;
using Android.Media;
using Android.Views;
using Android.Util;
using Android.OS;
using Android.Hardware.Camera2.Params;
using Size = Android.Util.Size;
using Class = Java.Lang.Class;
using Point = Android.Graphics.Point;


namespace Camera.MAUI.Platforms.Android;

internal class MauiCameraView: GridLayout
{
    private CameraInfo Camera { get; set; }
    //private MicrophoneInfo Microphone { get; set; }
    private readonly List<CameraInfo> Cameras = new();
    private readonly List<MicrophoneInfo> Micros = new();

    private readonly CameraView cameraView;
    private IExecutorService executorService;
    private bool started = false;
    private int frames = 0;
    private bool initiated = false;
    private bool snapping = false;
    private bool recording = false;
    private readonly Context context;

    private readonly AutoFitTextureView textureView;
    public CameraCaptureSession previewSession;
    public MediaRecorder mediaRecorder;
    private CaptureRequest.Builder previewBuilder;
    private CameraDevice cameraDevice;
    private readonly MyCameraStateCallback stateListener;
    private Size videoSize;
    private CameraManager cameraManager;
    private AudioManager audioManager;
    private HandlerThread backgroundThread;
    private Handler backgroundHandler;
    private readonly System.Timers.Timer timer;
    private readonly SparseIntArray ORIENTATIONS = new();


    public MauiCameraView(Context context, CameraView cameraView) : base(context)
    {
        this.context = context;
        this.cameraView = cameraView;

        textureView = new(context);
        timer = new(33.3);
        timer.Elapsed += Timer_Elapsed;
        stateListener = new MyCameraStateCallback(this);
        AddView(textureView);
        ORIENTATIONS.Append((int)SurfaceOrientation.Rotation0, 90);
        ORIENTATIONS.Append((int)SurfaceOrientation.Rotation90, 0);
        ORIENTATIONS.Append((int)SurfaceOrientation.Rotation180, 270);
        ORIENTATIONS.Append((int)SurfaceOrientation.Rotation270, 180);
        InitDevices();
    }

    private void InitDevices()
    {
        if (!initiated)
        {
            cameraManager = (CameraManager)context.GetSystemService(Context.CameraService);
            audioManager = (AudioManager)context.GetSystemService(Context.AudioService);
            foreach (var id in cameraManager.GetCameraIdList())
            {
                var cameraInfo = new CameraInfo { DeviceId = id, MinZoomFactor = 1 };
                var chars = cameraManager.GetCameraCharacteristics(id);
                if ((int)(chars.Get(CameraCharacteristics.LensFacing) as Java.Lang.Number) == (int)LensFacing.Back)
                {
                    cameraInfo.Name = "Back Camera";
                    cameraInfo.Position = CameraPosition.Back;
                }
                else if ((int)(chars.Get(CameraCharacteristics.LensFacing) as Java.Lang.Number) == (int)LensFacing.Front)
                {
                    cameraInfo.Name = "Front Camera";
                    cameraInfo.Position = CameraPosition.Front;
                }
                else
                {
                    cameraInfo.Name = "Camera " + id;
                    cameraInfo.Position = CameraPosition.Unknow;
                }
                cameraInfo.MaxZoomFactor = (float)(chars.Get(CameraCharacteristics.ScalerAvailableMaxDigitalZoom) as Java.Lang.Number) * 10;
                cameraInfo.HasFlashUnit = (bool)(chars.Get(CameraCharacteristics.FlashInfoAvailable) as Java.Lang.Boolean);
                Cameras.Add(cameraInfo);
            }
            foreach (var device in audioManager.Microphones)
            {
                Micros.Add(new MicrophoneInfo { Name = "Microphone " + device.Type.ToString() + " " + device.Address, DeviceId = device.Id.ToString() });
            }
            //Microphone = Micros.FirstOrDefault();
            executorService = Executors.NewSingleThreadExecutor();

            initiated = true;
            if (cameraView != null)
            {
                cameraView.Cameras.Clear();
                foreach (var micro in Micros) cameraView.Microphones.Add(micro);
                foreach (var cam in Cameras) cameraView.Cameras.Add(cam);
                cameraView.RefreshDevices();
            }
        }
    }

    public async Task<CameraResult> StartRecordingAsync(string file)
    {
        var result = CameraResult.Success;
        if (initiated && !recording)
        {
            if (await CameraView.RequestPermissions(true, true))
            {
                if (started) StopCamera();
                if (Camera != null)
                {
                    CameraCharacteristics characteristics = cameraManager.GetCameraCharacteristics(Camera.DeviceId);

                    StreamConfigurationMap map = (StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                    videoSize = ChooseVideoSize(map.GetOutputSizes(Class.FromType(typeof(ImageReader))));
                    AdjustAspectRatio(videoSize.Width, videoSize.Height);

                    recording = true;

                    if (OperatingSystem.IsAndroidVersionAtLeast(31))
                        mediaRecorder = new MediaRecorder(context);
                    else
                        mediaRecorder = new MediaRecorder();
                    audioManager.Mode = Mode.Normal;
                    mediaRecorder.SetAudioSource(AudioSource.Mic);
                    mediaRecorder.SetVideoSource(VideoSource.Surface);
                    mediaRecorder.SetOutputFormat(OutputFormat.Mpeg4);
                    mediaRecorder.SetOutputFile(file);
                    mediaRecorder.SetVideoEncodingBitRate(10000000);
                    mediaRecorder.SetVideoFrameRate(30);
                    mediaRecorder.SetVideoSize(videoSize.Width, videoSize.Height);
                    mediaRecorder.SetVideoEncoder(VideoEncoder.H264);
                    mediaRecorder.SetAudioEncoder(AudioEncoder.Aac);
                    int rotation = (int)((IWindowManager)context.GetSystemService(Context.WindowService)).DefaultDisplay.Rotation;
                    int orientation = ORIENTATIONS.Get(rotation);
                    mediaRecorder.SetOrientationHint(orientation);
                    mediaRecorder.Prepare();

                    cameraManager.OpenCamera(Camera.DeviceId, executorService, stateListener);
                    started = true;
                }
                else
                    result = CameraResult.NoCameraSelected;
            }
            else
                result = CameraResult.AccessDenied;
        }
        else
            result = CameraResult.NotInitiated;

        return result;
    }

    private void StartPreview()
    {
        while (textureView.SurfaceTexture == null) Thread.Sleep(100);
        SurfaceTexture texture = textureView.SurfaceTexture;
        texture.SetDefaultBufferSize(videoSize.Width, videoSize.Height);
        /*
        bool isSwappedDimension = IsDimensionSwapped();
        var previewSize = SetupPreviewSize(isSwappedDimension);
        texture.SetDefaultBufferSize(previewSize.Width, previewSize.Height);
        if (isSwappedDimension)
            MainThread.InvokeOnMainThreadAsync(() => textureView.SetAspectRatio(previewSize.Height, previewSize.Width)).Wait();
        else
            MainThread.InvokeOnMainThreadAsync(() => textureView.SetAspectRatio(previewSize.Width, previewSize.Height)).Wait();
        */
        previewBuilder = cameraDevice.CreateCaptureRequest(recording ? CameraTemplate.Record : CameraTemplate.Preview);
        var surfaces = new List<OutputConfiguration>();
        var previewSurface = new Surface(texture);
        surfaces.Add(new OutputConfiguration(previewSurface));
        previewBuilder.AddTarget(previewSurface);
        if (mediaRecorder != null)
        {
            surfaces.Add(new OutputConfiguration(mediaRecorder.Surface));
            previewBuilder.AddTarget(mediaRecorder.Surface);
        }

        SessionConfiguration config = new((int)SessionType.Regular,surfaces,executorService, new PreviewCaptureStateCallback(this));
        cameraDevice.CreateCaptureSession(config);
    }
    public void UpdatePreview()
    {
        if (null == cameraDevice)
            return;

        try
        {
            previewBuilder.Set(CaptureRequest.ControlMode, Java.Lang.Integer.ValueOf((int)ControlMode.Auto));
            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
            if (recording) mediaRecorder?.Start();
        }
        catch (CameraAccessException e)
        {
            e.PrintStackTrace();
        }
    }
    public async Task<CameraResult> StartCameraAsync()
    {
        var result = CameraResult.Success;
        if (initiated)
        {
            if (await CameraView.RequestPermissions())
            {
                if (started) StopCamera();
                if (Camera != null)
                {
                    CameraCharacteristics characteristics = cameraManager.GetCameraCharacteristics(Camera.DeviceId);

                    StreamConfigurationMap map = (StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                    //videoSize = ChooseVideoSize(map.GetOutputSizes(Class.FromType(typeof(ImageReader))));
                    videoSize = new Size(1920, 1080);
                    AdjustAspectRatio(videoSize.Width, videoSize.Height);
                    cameraManager.OpenCamera(Camera.DeviceId, executorService, stateListener);
                    timer.Start();
                    started = true;
                }
                else
                    result = CameraResult.NoCameraSelected;
            }
            else
                result = CameraResult.AccessDenied;
        }
        else
            result = CameraResult.NotInitiated;

        return result;
    }
    public Task<CameraResult> StopRecordingAsync()
    {
        recording = false;
        return StartCameraAsync();
    }

    public CameraResult StopCamera()
    {
        CameraResult result = CameraResult.Success;
        if (initiated)
        {
            timer.Stop();
            try
            {
                mediaRecorder?.Stop();
                mediaRecorder?.Dispose();
            } catch { }
            try
            {
                previewSession?.StopRepeating();
                previewSession?.Dispose();
            } catch { }
            try
            {
                cameraDevice?.Close();
                cameraDevice?.Dispose();
            } catch { }
            try
            {
                backgroundThread?.Quit();
                backgroundHandler?.Dispose();
                backgroundThread?.Dispose();
            } catch { }
            backgroundThread = null;
            backgroundHandler = null;
            previewSession = null;
            cameraDevice = null;
            previewBuilder = null;
            mediaRecorder = null;
            started = false;
            recording = false;
        }
        else
            result = CameraResult.NotInitiated;
        return result;
    }
    public void DisposeControl()
    {
        if (started) StopCamera();
        executorService?.Shutdown();
        executorService?.Dispose();
        RemoveAllViews();
        textureView?.Dispose();
        timer.Dispose();
        Dispose();
    }
    private void ProccessQR()
    {
        Task.Run(() =>
        {
            Bitmap bitmap = TakeSnap();
            if (bitmap != null)
            {
                cameraView.DecodeBarcode(bitmap);
                bitmap.Dispose();
            }
        });
    }
    private void RefreshSnapShot()
    {
        cameraView.RefreshSnapshot(GetSnapShot(cameraView.AutoSnapShotFormat, true));
    }

    private void Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
    {
        if (!snapping && cameraView != null && cameraView.AutoSnapShotSeconds > 0 && (DateTime.Now - cameraView.lastSnapshot).TotalSeconds >= cameraView.AutoSnapShotSeconds)
        {
            Task.Run(() => RefreshSnapShot());
        }
        else if (cameraView.BarCodeDetectionEnabled)
        {
            frames++;
            if (frames >= cameraView.BarCodeDetectionFrameRate)
            {
                ProccessQR();
                frames = 0;
            }
        }

    }

    private Bitmap TakeSnap()
    {
        Bitmap bitmap = null;
        try
        {
            MainThread.InvokeOnMainThreadAsync(() => { bitmap = textureView.GetBitmap(null); bitmap = textureView.Bitmap; }).Wait();
            if (bitmap != null)
            {
                int oriWidth = bitmap.Width;
                int oriHeight = bitmap.Height;

                bitmap = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, textureView.GetTransform(null), false);
                float xscale = (float)oriWidth / bitmap.Width;
                float yscale = (float)oriHeight / bitmap.Height;
                bitmap = Bitmap.CreateBitmap(bitmap, Math.Abs(bitmap.Width - (int)((float)Width*xscale)) / 2, Math.Abs(bitmap.Height - (int)((float)Height * yscale)) / 2, Width, Height);
                if (textureView.ScaleX == -1)
                {
                    Matrix matrix = new();
                    matrix.PreScale(-1, 1);
                    bitmap = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, false);
                }
            }
        }
        catch { }
        return bitmap;
    }
    internal System.IO.Stream TakePhotoAsync(ImageFormat imageFormat)
    {
        if (started && !snapping)
        {
            snapping = true;
            Bitmap bitmap = null;
            try
            {
                MainThread.InvokeOnMainThreadAsync(() => { bitmap = textureView.GetBitmap(null); bitmap = textureView.Bitmap; }).Wait();
                if (bitmap != null)
                {
                    bitmap = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, textureView.GetTransform(null), false);
                    if (textureView.ScaleX == -1)
                    {
                        Matrix matrix = new();
                        matrix.PreScale(-1, 1);
                        bitmap = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, false);
                    }
                    var iformat = imageFormat switch
                    {
                        ImageFormat.JPEG => Bitmap.CompressFormat.Jpeg,
                        _ => Bitmap.CompressFormat.Png
                    };
                    MemoryStream stream = new();
                    bitmap.Compress(iformat, 100, stream);
                    stream.Position = 0;
                    snapping = false;
                    return stream;
                }
            }
            catch { }
        }
        snapping = false;
        return null;
    }

    internal ImageSource GetSnapShot(ImageFormat imageFormat, bool auto = false)
    {
        ImageSource result = null;

        if (started && !snapping)
        {
            snapping = true;
            Bitmap bitmap = TakeSnap();

            if (bitmap != null)
            {
                var iformat = imageFormat switch
                {
                    ImageFormat.JPEG => Bitmap.CompressFormat.Jpeg,
                    _ => Bitmap.CompressFormat.Png
                };
                MemoryStream stream = new();
                bitmap.Compress(iformat, 100, stream);
                stream.Position = 0;
                if (auto)
                {
                    if (cameraView.AutoSnapShotAsImageSource)
                        result = ImageSource.FromStream(() => stream);
                    cameraView.SnapShotStream?.Dispose();
                    cameraView.SnapShotStream = stream;
                }
                else
                    result = ImageSource.FromStream(() => stream);
                bitmap.Dispose();
            }
            snapping = false;
        }
        return result;
    }

    public bool SaveSnapShot(ImageFormat imageFormat, string SnapFilePath)
    {
        bool result = true;

        if (started && !snapping)
        {
            snapping = true;
            Bitmap bitmap = TakeSnap();
            if (bitmap != null)
            {
                if (File.Exists(SnapFilePath)) File.Delete(SnapFilePath);
                var iformat = imageFormat switch
                {
                    ImageFormat.JPEG => Bitmap.CompressFormat.Jpeg,
                    _ => Bitmap.CompressFormat.Png
                };
                using FileStream stream = new(SnapFilePath, FileMode.OpenOrCreate);
                bitmap.Compress(iformat, 80, stream);
                stream.Close();
            }
            snapping = false;
        }
        else
            result = false;

        return result;
    }
    public async void UpdateCamera()
    {
        if (cameraView != null && cameraView.Camera != null)
        {
            if (started)
            {
                StopCamera();
                Camera = cameraView.Camera;
                await StartCameraAsync();
            }else
                Camera = cameraView.Camera;
        }
    }
    public void UpdateMirroredImage()
    {
        if (cameraView != null && textureView != null)
        {
            if (cameraView.MirroredImage) 
                textureView.ScaleX = -1;
            else
                textureView.ScaleX = 1;
        }
    }
    public void UpdateTorch()
    {
        if (Camera != null && Camera.HasFlashUnit)
        {
            if (started)
            {
                previewBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
                previewBuilder.Set(CaptureRequest.FlashMode, cameraView.TorchEnabled ? (int)ControlAEMode.OnAutoFlash : (int)ControlAEMode.Off);
                previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
            }
            else if (initiated)
                cameraManager.SetTorchMode(Camera.DeviceId, cameraView.TorchEnabled);
        }
    }
    public void UpdateFlashMode()
    {
        if (previewSession != null && previewBuilder != null && Camera != null && cameraView != null)
        {
            try
            {
                if (Camera.HasFlashUnit)
                {
                    switch (cameraView.FlashMode)
                    {
                        case FlashMode.Auto:
                            previewBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.OnAutoFlash);
                            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
                            break;
                        case FlashMode.Enabled:
                            previewBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
                            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
                            break;
                        case FlashMode.Disabled:
                            previewBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.Off);
                            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
                            break;
                    }
                }
            }
            catch (System.Exception)
            {
            }
        }
    }
    public void SetZoomFactor(float zoom)
    {
        if (previewSession != null && previewBuilder != null && Camera != null)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                previewBuilder.Set(CaptureRequest.ControlZoomRatio, Math.Max(Camera.MinZoomFactor, Math.Min(zoom, Camera.MaxZoomFactor)));
                previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
            }
        }
    }

    private Size ChooseVideoSize(Size[] choices)
    {
        Size result = choices[0];
        int diference = 0;

        foreach (Size size in choices)
        {
            if (size.Width == size.Height * 4 / 3 && size.Width * size.Height > diference)
            {
                result = size;
                diference = size.Width * size.Height;
            }
        }
        /*
        if (Width >= Height)
        {
            foreach (Size size in choices)
            {
                if (size.Height >= 1080 && size.Width >= Width && size.Width == size.Height * 4 / 3)
                {
                    int dif = Math.Abs(size.Width - Width) + Math.Abs(size.Height - Height);
                    if (dif < diference) result = size;
                    diference = dif;
                }
                else if (size.Width >= result.Width && result.Width < Width)
                    result = size;
            }
        }
        else
        {
            foreach (Size size in choices)
            {
                if (size.Height >= 1080 && size.Height >= Height && size.Width == size.Height * 4 / 3)
                {
                    int dif = Math.Abs(size.Width - Width) + Math.Abs(size.Height - Height);
                    if (dif < diference) result = size;
                    diference = dif;
                }
                else if (size.Height >= result.Height && result.Height < Height)
                    result = size;
            }
        }
        */
        return result;
    }
    
    private void AdjustAspectRatio(int videoWidth, int videoHeight)
    {
        Matrix txform = new();
        float centerX = Math.Max((videoWidth - Width) / 2, 0);
        float centerY = Math.Max((videoHeight - Height) / 2, 0);
        //float scaleX = Width >= Height ? 1 : (float)videoHeight / Height;
        //float scaleY = Width >= Height ? (float)videoWidth / Width : 1;

        float scaleX = (float)videoWidth / Width;
        float scaleY = (float)videoHeight / Height;
        if (IsDimensionSwapped())
        {
            scaleX = (float)videoHeight / Width;
            scaleY = (float)videoWidth / Height;
            centerX = Math.Max((videoHeight - Width) / 2, 0);
            centerY = Math.Max((videoWidth - Height) / 2, 0);
        }
        //var minzoom = (float)Math.Min(Math.Truncate(scaleX), Math.Truncate(scaleY)) - 1f;
        if (ScaleX <= scaleY)
        {
            scaleY /= scaleX;
            scaleX = 1;
        }
        else
        {
            scaleX /= scaleY;
            scaleY = 1;
        }
        txform.PostScale(scaleX, scaleY, centerX, centerY);
        //txform.PostScale(scaleX, scaleY, 0, 0);
        textureView.SetTransform(txform);
    }

    private bool IsDimensionSwapped()
    {
        var displayRotation = ((IWindowManager)context.GetSystemService(Context.WindowService)).DefaultDisplay.Rotation;
        var chars = cameraManager.GetCameraCharacteristics(Camera.DeviceId);
        var sensorOrientation = (int)(chars.Get(CameraCharacteristics.SensorOrientation) as Java.Lang.Number);
        bool swappedDimensions = false;
        switch(displayRotation)
        {
            case SurfaceOrientation.Rotation0:
            case SurfaceOrientation.Rotation180:
                if (sensorOrientation == 90 || sensorOrientation == 270)
                {
                    swappedDimensions = true;
                }
                break;
            case SurfaceOrientation.Rotation90:
            case SurfaceOrientation.Rotation270:
                if (sensorOrientation == 0 || sensorOrientation == 180)
                {
                    swappedDimensions = true;
                }
                break;
        }

        return swappedDimensions;
    }

    private Size SetupPreviewSize(bool isDimensionSwapped)
    {
        var largest = GetCaptureSize();

        var displaySize = new Point();
        ((IWindowManager)context.GetSystemService(Context.WindowService)).DefaultDisplay.GetRealSize(displaySize);
        if (isDimensionSwapped)
            return GetOptimalSize(Height, Width, displaySize.Y, displaySize.X, largest);
        else
            return GetOptimalSize(Width, Height, displaySize.X, displaySize.Y, largest);
    }

    private Size GetCaptureSize()
    {
        var chars = cameraManager.GetCameraCharacteristics(Camera.DeviceId);
        StreamConfigurationMap map = (StreamConfigurationMap)chars.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
        var supportedSizes = map.GetOutputSizes((int)ImageFormatType.Jpeg);

        return supportedSizes.First();
    }

    private Size GetOptimalSize(int textureViewWidth, int textureViewHeight, int maxWidth, int maxHeight, Size aspectRatio)
    {
        var chars = cameraManager.GetCameraCharacteristics(Camera.DeviceId);
        StreamConfigurationMap map = (StreamConfigurationMap)chars.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
        var supportedPreviewSizes = map.GetOutputSizes(Class.FromType(typeof(SurfaceTexture)));
        var bigEnough = new List<Size>();
        Size minSize = new Size(10000, 10000);
        foreach (var option in supportedPreviewSizes)
        {
            if (option.Width <= maxWidth && option.Height <= maxHeight && option.Height == option.Width * aspectRatio.Height / aspectRatio.Width)
            {
                bigEnough.Add(option);
                if (option.Height * option.Width >= minSize.Height * minSize.Width)
                    minSize = option;
            }
        }
        return minSize;
    }
    private class AutoFitTextureView : TextureView
    {
        private int mRatioWidth = 0;
        private int mRatioHeight = 0;

        public AutoFitTextureView(Context context)
            : this(context, null)
        {

        }
        public AutoFitTextureView(Context context, IAttributeSet attrs)
            : this(context, attrs, 0)
        {

        }
        public AutoFitTextureView(Context context, IAttributeSet attrs, int defStyle)
            : base(context, attrs, defStyle)
        {

        }

        public void SetAspectRatio(int width, int height)
        {
            mRatioWidth = width;
            mRatioHeight = height;
            RequestLayout();
        }

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
            int width = MeasureSpec.GetSize(widthMeasureSpec);
            int height = MeasureSpec.GetSize(heightMeasureSpec);
            if (0 == mRatioWidth || 0 == mRatioHeight)
            {
                SetMeasuredDimension(width, height);
            }
            else
            {
                if (width < (float)height * mRatioWidth / (float)mRatioHeight)
                {
                    SetMeasuredDimension(width, width * mRatioHeight / mRatioWidth);
                }
                else
                {
                    SetMeasuredDimension(height * mRatioWidth / mRatioHeight, height);
                }
            }
        }
    }
    private class MyCameraStateCallback : CameraDevice.StateCallback
    {
        private readonly MauiCameraView cameraView;
        public MyCameraStateCallback(MauiCameraView camView)
        {
            cameraView = camView;
        }
        public override void OnOpened(CameraDevice camera)
        {
            cameraView.cameraDevice = camera;
            cameraView.StartPreview();
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            cameraView.cameraDevice = null;
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            camera?.Close();
            cameraView.cameraDevice = null;
        }
    }
    private class PreviewCaptureStateCallback : CameraCaptureSession.StateCallback
    {
        private readonly MauiCameraView cameraView;
        public PreviewCaptureStateCallback(MauiCameraView camView)
        {
            cameraView = camView;
        }
        public override void OnConfigured(CameraCaptureSession session)
        {
            cameraView.previewSession = session;
            cameraView.UpdatePreview();

        }
        public override void OnConfigureFailed(CameraCaptureSession session)
        {
        }
    }
}

