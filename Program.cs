// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace HoloLensSample
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Ports;
    using System.Linq;
    using System.Net.WebSockets;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using MathNet.Numerics.LinearAlgebra.Double;
    using MathNet.Spatial.Euclidean;
    using MathNet.Spatial.Units;
    using Microsoft.Azure.SpatialAnchors;
    using Microsoft.MixedReality.WebRTC;
    using Microsoft.Psi;
    using Microsoft.Psi.Audio;
    using Microsoft.Psi.Calibration;
    using Microsoft.Psi.Data;
    using Microsoft.Psi.Data.Json;
    using Microsoft.Psi.Imaging;
    using Microsoft.Psi.Interop;
    using Microsoft.Psi.Interop.Serialization;
    using Microsoft.Psi.Media;
    using Microsoft.Psi.MixedReality;
    using Microsoft.Psi.MixedReality.MediaCapture;
    using Microsoft.Psi.MixedReality.ResearchMode;
    using Microsoft.Psi.MixedReality.StereoKit;
    using Microsoft.Psi.Remoting;
    using Microsoft.Psi.Spatial.Euclidean;
    using OpenCvSharp;
    using StereoKit;
    using Windows.Devices.Enumeration;
    using Windows.Devices.SerialCommunication;
    using Windows.Graphics.Imaging;
    using Windows.Networking.Sockets;
    using Windows.Perception.Spatial;
    using Windows.Storage;
    using Windows.Storage.Streams;
    using static Microsoft.Psi.Interop.Rendezvous.Rendezvous;
    using Color = System.Drawing.Color;
    using Microphone = Microsoft.Psi.MixedReality.StereoKit.Microphone;

    /// <summary>
    /// HoloLens samples.
    /// </summary>
    public static class Program
    {
        // private static PeerConnection peerconnection;
        // private static DataChannel imageDataChannel;
        private static SerialDevice serialdevice;
        private static RemoteExporter remoteExporter = null;

        /// <summary>
        /// Main entry point.
        /// </summary>
        ///
        public static void Main()
        {
            // Initialize StereoKit
            if (!SK.Initialize(
                new SKSettings
                {
                    appName = "HoloLensSample",
                    assetsFolder = "Assets",
                }))
            {
                throw new Exception("StereoKit failed to initialize.");
            }

            // Initialize MixedReality statics
            MixedReality.Initialize();

            var demos = new (string Name, Func<bool, Pipeline> Run)[]
            {
                ("Movable Marker Demo", MovableMarkerDemo),
                ("Bees Demo!", BeesDemo),
                ("Scene Understanding Demo", SceneUnderstandingDemo),
            };

            bool persistStreamsToStore = true;
            Pipeline pipeline = null;
            var starting = false;
            var stopping = false;
            var demo = string.Empty;
            Exception exception = null;
            var windowCoordinateSystem = default(CoordinateSystem);
            var windowPose = default(Pose);

            while (SK.Step(() =>
            {
                try
                {
                    // Position the window near the head at the start
                    var headPose = Input.Head.ToCoordinateSystem();

                    if (windowCoordinateSystem == null)
                    {
                        // Project forward 0.7 meters from the initial head pose (in the XY plane).
                        var forwardDirection = headPose.XAxis.ProjectOn(new MathNet.Spatial.Euclidean.Plane(headPose.Origin, UnitVector3D.ZAxis)).Direction;
                        var windowOrigin = headPose.Origin + forwardDirection.ScaleBy(0.7);
                        windowCoordinateSystem = LookAtPoint(windowOrigin, headPose.Origin);
                    }
                    else
                    {
                        // Update to point toward the head
                        windowCoordinateSystem = windowPose.ToCoordinateSystem();
                        windowCoordinateSystem = LookAtPoint(windowCoordinateSystem.Origin, headPose.Origin);
                    }

                    windowPose = windowCoordinateSystem.ToStereoKitPose();
                    UI.WindowBegin("Psi Demos", ref windowPose, new Vec2(30 * U.cm, 0));

                    if (exception != null)
                    {
                        UI.Label($"Exception: {exception.Message}");
                    }
                    else if (starting)
                    {
                        UI.Label($"Starting {demo}...");
                    }
                    else if (stopping)
                    {
                        UI.Label($"Stopping {demo}...");
                    }
                    else
                    {
                        if (pipeline == null)
                        {
                            UI.Label("Choose a demo to run: Test5");
                            foreach (var (name, run) in demos)
                            {
                                if (UI.Button(name))
                                {
                                    demo = name;
                                    starting = true;
                                    Task.Run(() =>
                                    {
                                        pipeline = run(persistStreamsToStore);
                                        pipeline.RunAsync();
                                        starting = false;
                                    });
                                }

                                UI.SameLine();
                            }

                            UI.NextLine();
                        }
                        else
                        {
                            UI.Label($"Running {demo}");
                            if (UI.Button($"Stop"))
                            {
                                stopping = true;
                                Task.Run(() =>
                                {
                                    pipeline.Dispose();
                                    pipeline = null;
                                    stopping = false;
                                });
                            }

                            UI.SameLine();
                        }
                    }

                    if (UI.Button("Exit"))
                    {
                        SK.Quit();
                    }

                    UI.WindowEnd();
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
            }))
            {
            }

            SK.Shutdown();
        }

        /// <summary>
        /// Scene understanding demo.
        /// </summary>
        /// <param name="persistStreamsToStore">A value indicating whether to persist a store with several sensor streams.</param>
        /// <returns>Pipeline.</returns>
        public static Pipeline SceneUnderstandingDemo(bool persistStreamsToStore)
        {
            var pipeline = Pipeline.Create(nameof(SceneUnderstandingDemo));

            var sceneUnderstanding = new SceneUnderstanding(pipeline, new SceneUnderstandingConfiguration()
            {
                ComputePlacementRectangles = true,
                InitialPlacementRectangleSize = (0.5, 1.0),
            });

            var worldMeshes = sceneUnderstanding.Select(s => s.World.Meshes.ToArray());
            var wallPlacementRectangles = sceneUnderstanding
                .Select(s => s.Wall.PlacementRectangles)
                .Select(rects => rects.Where(r => r.HasValue).Select(r => r.Value).ToArray());

            worldMeshes.Parallel(
                s => s.PipeTo(new Mesh3DRenderer(s.Out.Pipeline, Color.White, true)),
                name: "RenderWorldMeshes");

            wallPlacementRectangles.Parallel(
                s => s.PipeTo(new Rectangle3DRenderer(s.Out.Pipeline, Color.Red)),
                name: "RenderPlacementRectangles");

            if (persistStreamsToStore)
            {
                // Optionally persist sensor streams to visualize in PsiStudio, along with scene understanding info.
                /*var store = CreateStoreWithSourceStreams(pipeline, nameof(SceneUnderstandingDemo));*/

                CreateStoreWithSourceStreams(pipeline, nameof(SceneUnderstandingDemo));

                // sceneUnderstanding.Write("SceneUnderstanding", store, true);
            }

            return pipeline;
        }

        /// <summary>
        /// Movable marker demo.
        /// </summary>
        /// <param name="persistStreamsToStore">A value indicating whether to persist a store with several sensor streams.</param>
        /// <returns>Pipeline.</returns>
        public static Pipeline MovableMarkerDemo(bool persistStreamsToStore)
        {
            var pipeline = Pipeline.Create(nameof(MovableMarkerDemo));

            try
            {
                remoteExporter = new RemoteExporter(pipeline, 12345, TransportKind.Udp, 999999999999999999, 5);
            }
            catch (Exception ex)
            {
                _ = WriteLogToFile("Failed to establish remoteExporter: " + ex.Message);
            }

            _ = WriteLogToFile("RemoteExporter opened");

            // Instantiate the marker renderer (starting pose of 1 meter forward, 30cm down).
            // var markerScale = 0.4f;
            // var initialMarkerPose = CoordinateSystem.Translation(new Vector3D(1, 0, -0.3));
            // var markerMesh = MeshRenderer.CreateMeshFromEmbeddedResource("HoloLensSample.Assets.Marker.Marker.glb");
            // var markerRenderer = new MeshRenderer(pipeline, markerMesh, initialMarkerPose, new Vector3D(markerScale, markerScale, markerScale), Color.LightBlue);

            // handle to move marker
            /*
            var handleBounds = new Vector3D(
                markerScale * markerMesh.Bounds.dimensions.x,
                markerScale * markerMesh.Bounds.dimensions.y,
                markerScale * markerMesh.Bounds.dimensions.z);
            var handle = new Handle(pipeline, initialMarkerPose, handleBounds);
            */

            // slowly spin the marker
            /*var spin = Generators
                .Range(pipeline, 0, int.MaxValue, TimeSpan.FromMilliseconds(10))
                .Select(i => CoordinateSystem.Yaw(Angle.FromDegrees(i * 0.5)));*/

            // combine spinning with user-driven movement
            /*
            var markerPose = spin.Join(handle, RelativeTimeInterval.Infinite)
                .Select(m => m.Item1.TransformBy(m.Item2));

            markerPose.PipeTo(markerRenderer.Pose);*/

            /*
            if (true)
            {
                // Optionally persist sensor streams to visualize in PsiStudio, along with the marker pose.
                 var store = CreateStoreWithSourceStreams(pipeline, nameof(MovableMarkerDemo));

                CreateStoreWithSourceStreams(pipeline, nameof(MovableMarkerDemo));

                // markerPose.Write("MarkerPose", store);
            }
            */

            var camera = new PhotoVideoCamera(
               pipeline,
               new PhotoVideoCameraConfiguration
               {
                   VideoStreamSettings = new () { FrameRate = 15, ImageWidth = 896, ImageHeight = 504 },
               });

            camera.VideoEncodedImage.Write("PhotoCameraStream", remoteExporter.Exporter, true, DeliveryPolicy.LatestMessage);

            // camera.VideoIntrinsics.Write("CameraCallibration", remoteExporter.Exporter, true, DeliveryPolicy.LatestMessage);
            // Microsoft.Psi.Imaging.ImageFromNV12StreamDecoder streamDecoder = new ImageFromNV12StreamDecoder();
            // Microsoft.Psi.Imaging.ImageDecoder decoder = new ImageDecoder(pipeline, streamDecoder);
            // camera.VideoEncodedImage.PipeTo(decoder.In);

            // await SaveImageAsync(image, filename);
            /*
            decoder.Out.Do(image =>
            {
                try
                {
                    _ = WriteLogToFile("Write decode image: " + image.Resource.Size);
                    StorageFolder localFolder = ApplicationData.Current.LocalFolder;

                    Random rnd = new Random();
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + rnd.Next(1, 1000).ToString();
                    string filename = $"captured_{timestamp}.jpg";
                }
                catch (Exception ex)
                {
                    // Log or handle the exception appropriately
                    Debug.WriteLine($"Error processing image: {ex.Message}");
                }
            });
            */

            /*
            decoder.Out.Do(frame =>
            {
                var image = frame.Resource;
                var position = TrackObjectInImage(image);
                if (position.X != -1 && position.Y != -1)
                {
                    _ = WriteLogToFile($"Object Position: {position}");
                }
            }); */

            // Depth camera (long throw)
            DepthCameraConfiguration depthCameraConfiguration = new DepthCameraConfiguration();
            depthCameraConfiguration.OutputInfraredImage = true;
            var depthCamera = new DepthCamera(pipeline, depthCameraConfiguration);
            depthCamera.DepthImage.Write("DepthCameraStream", remoteExporter.Exporter, true, DeliveryPolicy.LatestMessage);

            return pipeline;
        }

        /// <summary>
        /// Bees circling your head demo.
        /// </summary>
        /// <param name="persistStreamsToStore">A value indicating whether to persist a store with several sensor streams.</param>
        /// <returns>Pipeline.</returns>
        public static Pipeline BeesDemo(bool persistStreamsToStore)
        {
            var pipeline = Pipeline.Create(nameof(BeesDemo));

            try
            {
                remoteExporter = new RemoteExporter(pipeline, 12345, TransportKind.Udp);
            }
            catch (Exception ex)
            {
                _ = WriteLogToFile("Failed to establish remoteExporter: " + ex.Message);
            }

            _ = WriteLogToFile("RemoteExporter opened");

            // Load bee audio from a wav file, triggered to play every two seconds.
            using var beeWave = Assembly.GetCallingAssembly().GetManifestResourceStream("HoloLensSample.Assets.Sounds.Bees.wav");
            var beeAudio = new WaveStreamSampleSource(pipeline, beeWave);
            var repeat = Generators.Repeat(pipeline, true, TimeSpan.FromSeconds(2));
            repeat.PipeTo(beeAudio);

            // Send the audio to a spatial sound rendering component.
            var beeSpatialSound = new SpatialSound(pipeline, default, 2);
            beeAudio.PipeTo(beeSpatialSound);

            // Calculate the pose of the bee that flies in a 1 meter radius circle around the user's head.
            var oneMeterForward = CoordinateSystem.Translation(new Vector3D(1, 0, 0));
            var zeroRotation = DenseMatrix.CreateIdentity(3);
            var headPose = new HeadSensor(pipeline);
            var beePose = headPose.Select((head, env) =>
            {
                // Fly 1 degree around the user's head every 20 ms.
                var timeElapsed = (env.OriginatingTime - pipeline.StartTime).TotalMilliseconds;
                var degrees = Angle.FromDegrees(timeElapsed / 20.0);

                // Ignore the user's head rotation.
                head = head.SetRotationSubMatrix(zeroRotation);
                return oneMeterForward.RotateCoordSysAroundVector(UnitVector3D.ZAxis, degrees).TransformBy(head);
            });

            // Render the bee as a sphere.
            var sphere = new MeshRenderer(pipeline, Mesh.GenerateSphere(0.1f), Color.Yellow);
            beePose.PipeTo(sphere.Pose);

            // Finally, pass the position (Point3D) of the bee to the spatial audio component.
            var beePosition = beePose.Select(b => b.Origin);
            beePosition.PipeTo(beeSpatialSound.PositionInput);

            if (true)
            {
                // Optionally persist sensor streams to visualize in PsiStudio, along with the bee streams.
                /*var store = CreateStoreWithSourceStreams(pipeline, nameof(BeesDemo), headPose);*/

                CreateStoreWithSourceStreams(pipeline, nameof(BeesDemo), headPose);

                // beePosition.Write("Bee.Position", store);
                // beeAudio.Write("Bee.Audio", store);
            }

            return pipeline;
        }

        private static void CreateStoreWithSourceStreams(Pipeline pipeline, string storeName, HeadSensor head = null)
        {
            // Create a Psi store in \LocalAppData\HoloLensSample\LocalState
            // To visualize in PsiStudio, the store can be copied to another machine via the device portal.
            // var store = PsiStore.Create(pipeline, storeName, ApplicationData.Current.LocalFolder.Path);

            // Head, hands, and eyes
            // head ??= new HeadSensor(pipeline);
            // var eyes = new EyesSensor(pipeline);
            // var hands = new HandsSensor(pipeline);

            // head.Write("Head", store);
            // eyes.Write("Eyes", store);
            // hands.Left.Write("Hands.Left", store);
            // hands.Right.Write("Hands.Right", store);

            // Microphone audio
            // var audio = new Microphone(pipeline);
            // audio.Write("Audio", store);

            // PhotoVideo camera (video and mixed reality preview)
            var camera = new PhotoVideoCamera(
                pipeline,
                new PhotoVideoCameraConfiguration
                {
                    VideoStreamSettings = new () { FrameRate = 15, ImageWidth = 896, ImageHeight = 504 },
                });

            // PreviewStreamSettings = new () { FrameRate = 15, ImageWidth = 896, ImageHeight = 504, MixedRealityCapture = new () },
            // Microsoft.Psi.Imaging.ImageFromNV12StreamDecoder streamDecoder = new ImageFromNV12StreamDecoder();
            // Microsoft.Psi.Imaging.ImageDecoder decoder = new ImageDecoder(pipeline, streamDecoder);
            // camera.VideoEncodedImage.PipeTo(decoder.In);
            camera.VideoEncodedImage.Write("PhotoCameraStream", remoteExporter.Exporter, true, DeliveryPolicy.LatestMessage);

            // decoder.Out.Write("PhotoCameraStreamDecode", remoteExporter.Exporter, true, DeliveryPolicy.LatestMessage);
            camera.VideoEncodedImage.Do(image =>
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _ = WriteLogToFile(timestamp + " | image Size : " + image.Resource.Size.ToString());
            });

            // Microsoft.Psi.Imaging.DepthImageDecoder depthImageDecoder = new DepthImageDecoder(pipeline, depthstreamDecoder);
            // camera.VideoEncodedImage.PipeTo(decoder.In);

            /*
            decoder.Out.Do(async image =>
            {
                try
                {
                    // await SaveImageAsync(image, filename);
                    StorageFolder localFolder = ApplicationData.Current.LocalFolder;

                    Random rnd = new Random();
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + rnd.Next(1, 1000).ToString();
                    string filename = $"captured_{timestamp}.jpg";
                    await SaveImageAsync(image, filename);
                }
                catch (Exception ex)
                {
                    // Log or handle the exception appropriately
                    Debug.WriteLine($"Error processing image: {ex.Message}");
                }
            });*/

            // camera.VideoEncodedImageCameraView.Write("VideoEncodedImageCameraView", store, true);
            // camera.PreviewEncodedImageCameraView.Write("PreviewEncodedImageCameraView", store, true);

            // Depth camera (long throw)
            DepthCameraConfiguration depthCameraConfiguration = new DepthCameraConfiguration();
            depthCameraConfiguration.OutputInfraredImage = true;
            var depthCamera = new DepthCamera(pipeline, depthCameraConfiguration);

            // depthCamera.DepthImageCameraView.Write("DepthImageCameraView", store, true);

            /*
            depthCamera.DepthImage.Do(async depthImage =>
            {
                Random rnd = new Random();
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + rnd.Next(1, 1000).ToString();
                string filename = $"captured_depth_{timestamp}.jpg";
                byte[] imagedata = new byte[depthImage.Resource.Size];
                depthImage.Resource.CopyTo(imagedata);

                // await SaveImageAsync(imagedata, filename);
                await DecodeDepthImage(depthImage, filename);
            });
            */

            depthCamera.DepthImage.Write("DepthCameraStream", remoteExporter.Exporter, true, DeliveryPolicy.LatestMessage);

            // return store;
        }

        private static CoordinateSystem LookAtPoint(Point3D sourcePoint, Point3D targetPoint)
        {
            var forward = (targetPoint - sourcePoint).Normalize();
            var left = UnitVector3D.ZAxis.CrossProduct(forward);
            var up = forward.CrossProduct(left);
            return new CoordinateSystem(sourcePoint, forward, left, up);
        }

        private static async Task SaveImageAsync(byte[] imageBytes, string fileName)
        {
            // Get the local folder
            var localFolder = ApplicationData.Current.LocalFolder;

            // Create a new file or replace an existing one
            var file = await localFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

            // Write the image bytes to the file
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(imageBytes);
                    await writer.StoreAsync();
                }
            }
        }

        private static void WriteEncodedImageCameraView(EncodedImageCameraView encodedImageCameraView, BinaryWriter writer)
        {
            WriteEncodedImage(encodedImageCameraView.ViewedObject, writer);
        }

        private static void WriteEncodedImage(Shared<EncodedImage> sharedEncodedImage, BinaryWriter writer)
        {
            InteropSerialization.WriteBool(sharedEncodedImage != null, writer);
            if (sharedEncodedImage == null)
            {
                return;
            }

            var image = sharedEncodedImage.Resource;
            var data = image.GetBuffer();
            writer.Write(image.Width);
            writer.Write(image.Height);
            writer.Write((int)image.PixelFormat);
            writer.Write(image.Size);
            writer.Write(data, 0, image.Size);
        }

        private static async Task DecodeDepthImage(Shared<DepthImage> encodedDepth, string fileName)
        {
            // Assuming EncodedDepthImage is a wrapper around a depth image encoded as 16-bit integers
            var width = encodedDepth.Resource.Width;
            var height = encodedDepth.Resource.Height;
            var depthImage = new Image(width, height, encodedDepth.Resource.PixelFormat);
            var buffer = depthImage.ImageData;

            byte[] pixelData = new byte[encodedDepth.Resource.Size]; // 4 bytes per pixel for BGRA
            System.Runtime.InteropServices.Marshal.Copy(encodedDepth.Resource.ImageData, pixelData, 0, pixelData.Length);

            // Get the local folder
            var localFolder = ApplicationData.Current.LocalFolder;

            // Create a new file or replace an existing one
            var file = await localFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

            // Write the image bytes to the file
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(pixelData);
                    await writer.StoreAsync();
                }
            }
        }

        private static async Task SaveImageAsync(Shared<Image> image, string filename)
        {
            try
            {
                _ = WriteLogToFile("image.Resource.Size : " + image.Resource.Size.ToString());
                if (image.Resource.Size <= 0)
                {
                    return;
                }

                StorageFolder localFolder = ApplicationData.Current.LocalFolder;
                StorageFile file = await localFolder.CreateFileAsync(filename, CreationCollisionOption.ReplaceExisting);
                _ = WriteLogToFile("image empty file created ");
                using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    // Create a BitmapEncoder
                    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
                    _ = WriteLogToFile("BitmapEncoder created ");

                    // Assuming image format is Bgra8 and the image class provides direct access to ImageData
                    byte[] pixelData = new byte[image.Resource.Size]; // 4 bytes per pixel for BGRA
                    _ = WriteLogToFile("pixelData created ");
                    System.Runtime.InteropServices.Marshal.Copy(image.Resource.ImageData, pixelData, 0, pixelData.Length);
                    _ = WriteLogToFile("pixelData copied ");

                    encoder.SetPixelData(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        (uint)image.Resource.Width,
                        (uint)image.Resource.Height,
                        96,  // DPI X
                        96,  // DPI Y
                        pixelData);

                    // imageDataChannel.SendMessage(pixelData);
                    await encoder.FlushAsync();
                }
            }
            catch (Exception e)
            {
                _ = WriteLogToFile("SaveImageAsync err : " + e.Message);
            }
        }

        private static async Task WriteLogToFile(string logMessage)
        {
            StorageFolder localFolder = ApplicationData.Current.LocalFolder;
            StorageFile logFile = await localFolder.CreateFileAsync("applog.txt", CreationCollisionOption.OpenIfExists);
            await FileIO.AppendTextAsync(logFile, logMessage + "\n");
        }

        private static Point TrackObjectInImage(Image image)
        {
            Mat cvimage = ImageToMat(image);

            // Define the color range for the object
            Scalar lowerColorBound = new Scalar(0, 0, 150); // Red lower bound
            Scalar upperColorBound = new Scalar(80, 80, 255); // Red upper bound

            // Threshold the image to get only the red areas
            cvimage = cvimage.InRange(lowerColorBound, upperColorBound);

            // Perform morphological operations to clean up the image
            cvimage = cvimage.Erode(new Mat(), null, 3);
            cvimage = cvimage.Dilate(new Mat(), null, 5);

            // Find contours
            cvimage.FindContours(out Point[][] contours, out HierarchyIndex[] hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            // Find the largest contour
            double maxArea = 0;
            int chosenContour = -1;
            for (int i = 0; i < contours.Length; i++)
            {
                double area = Cv2.ContourArea(contours[i]);
                if (area > maxArea)
                {
                    maxArea = area;
                    chosenContour = i;
                }
            }

            if (chosenContour != -1)
            {
                Moments moments = Cv2.Moments(contours[chosenContour]);
                int x = (int)(moments.M10 / moments.M00);
                int y = (int)(moments.M01 / moments.M00);
                return new Point(x, y);
            }

            return new Point(-1, -1); // No object found
        }

        private static Mat ImageToMat(Image image)
        {
            return Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC3, image.ImageData);
        }
    }
}
