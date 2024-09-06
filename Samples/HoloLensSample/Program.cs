// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace HoloLensSample
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.Contracts;
    using System.IO;
    using System.IO.Ports;
    using System.Linq;
    using System.Net.WebSockets;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using HoloLens2ResearchMode;
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
    using WebRTCtest;
    using Windows.Devices.Enumeration;
    using Windows.Devices.SerialCommunication;
    using Windows.Graphics.Imaging;
    using Windows.Networking.Sockets;
    using Windows.Perception.Spatial;
    using Windows.Storage;
    using Windows.Storage.Streams;
    using Emgu.CV;
    using Emgu.CV.Structure;
    using static Microsoft.Psi.Interop.Rendezvous.Rendezvous;
    using Color = System.Drawing.Color;
    using Microphone = Microsoft.Psi.MixedReality.StereoKit.Microphone;
    using Windows.UI.Xaml.Media.Imaging;
    using System.Runtime.InteropServices.WindowsRuntime;
    using Emgu.CV.Cuda;
    using System.Collections;
    using System.Drawing.Imaging;
    using System.Drawing;
    using System.Runtime.InteropServices;
    using static Microsoft.Psi.MixedReality.MediaCapture.PhotoVideoCameraConfiguration;
    using Windows.Media.Capture;

    /// <summary>
    /// HoloLens samples.
    /// </summary>
    public static class Program
    {
        // private static PeerConnection peerconnection;
        // private static DataChannel imageDataChannel;
        // private static SerialDevice serialdevice;
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
                ("Start Stream", BeesDemo),
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
                            UI.Label("Test5");
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


#pragma warning disable CS1591
        public static Pipeline MovableMarkerDemo(bool persistStreamsToStore)
        {
            var pipeline = Pipeline.Create(nameof(MovableMarkerDemo));

            //_ = WriteLogToFile("RemoteExporter opened");

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


            // Instantiate the marker renderer (starting pose of 1 meter forward, 30cm down).
            var markerScale = 1.0f;
            var initialMarkerPose = CoordinateSystem.Translation(new Vector3D(1, 0, -0.3));
            var markerMesh = MeshRenderer.CreateMeshFromEmbeddedResource("HoloLensSample.Assets.Marker.lumifyCvx.glb");
            var markerRenderer = new MeshRenderer(pipeline, markerMesh, initialMarkerPose, new Vector3D(markerScale, markerScale, markerScale), Color.LightBlue);
            // handle to move marker
            
            var handleBounds = new Vector3D(
                markerScale * markerMesh.Bounds.dimensions.x,
                markerScale * markerMesh.Bounds.dimensions.y,
                markerScale * markerMesh.Bounds.dimensions.z);
            var handle = new Handle(pipeline, initialMarkerPose, handleBounds);
            

            // slowly spin the marker
            var spin = Generators
                .Range(pipeline, 0, int.MaxValue, TimeSpan.FromMilliseconds(10))
                .Select(i => CoordinateSystem.Yaw(Angle.FromDegrees(i * 0.5)));

            // combine spinning with user-driven movement
            
            var markerPose = spin.Join(handle, RelativeTimeInterval.Infinite)
                .Select(m => m.Item1.TransformBy(m.Item2));

            markerPose.PipeTo(markerRenderer.Pose);


            webrtcclient client = new webrtcclient();

            Microsoft.Psi.Imaging.ImageFromNV12StreamDecoder streamDecoder = new ImageFromNV12StreamDecoder();
            Microsoft.Psi.Imaging.ImageDecoder decoder = new ImageDecoder(pipeline, streamDecoder);

            DepthCameraConfiguration depthCameraConfiguration = new DepthCameraConfiguration();
            depthCameraConfiguration.DepthSensorType = ResearchModeSensorType.DepthLongThrow;
            depthCameraConfiguration.OutputInfraredImage = false;
            depthCameraConfiguration.OutputDepthImage = true;
          
            var depthCamera = new DepthCamera(pipeline, depthCameraConfiguration);

            var camera = new PhotoVideoCamera(
                pipeline,
                new PhotoVideoCameraConfiguration
                {
                    VideoStreamSettings = new() { FrameRate = 15, ImageWidth = 896, ImageHeight = 504,
                        // OutputImage = true,
                        OutputEncodedImage = true,
                        MixedRealityCapture = new MixedRealityCaptureVideoEffect(MediaStreamType.Photo, 0.9f, MixedRealityCapturePerspective.PhotoVideoCamera)
                    },
            });;

            camera.VideoEncodedImage.PipeTo(decoder.In, DeliveryPolicy.LatestMessage);

            decoder.Out.Do(async image =>
            {
                try
                {
                    byte[] pixelData = new byte[image.Resource.Size];
                    System.Runtime.InteropServices.Marshal.Copy(image.Resource.ImageData, pixelData, 0, pixelData.Length);

                    
                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);

                        encoder.SetPixelData(
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Premultiplied,
                            (uint)image.Resource.Width,
                            (uint)image.Resource.Height,
                            96,
                            96,
                            pixelData);

                        //var propertySet = new BitmapPropertySet();
                        /*
                        var qualityValue = new BitmapTypedValue(
                            0.5,
                            Windows.Foundation.PropertyType.Single
                        );*/

                        await encoder.FlushAsync();

                        stream.Seek(0);

                        // Read the JPEG data back from the stream
                        byte[] jpegData = new byte[stream.Size];
                        using (var reader = new DataReader(stream))
                        {
                            await reader.LoadAsync((uint)stream.Size);
                            reader.ReadBytes(jpegData);
                        }
                        client.SendVido(jpegData);
                    }
                    

                   // _ = WriteLogToFile("pixelData == " + pixelData.Length);
                   //  client.SendVidoRGB(pixelData);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error processing image: {ex.Message}");
                }
            });


            depthCamera.DepthImageCameraView.Do(DepthCameraView =>
            {
                double focalX = DepthCameraView.CameraIntrinsics.FocalLengthXY.X;
                double focalY = DepthCameraView.CameraIntrinsics.FocalLengthXY.Y;

                byte fx = Convert.ToByte(focalX);
                byte fy = Convert.ToByte(focalY);

                byte[] focal = { fx, fy };

                //_ = WriteLogToFile("focal, " + focalX + ":" + focalY);
            });

            depthCamera.DepthImage.Do(depthimage =>
            {
                
                //_ = WriteLogToFile("Write depth image: " + depthimage.Resource.Size);
                StorageFolder localFolder = ApplicationData.Current.LocalFolder;

                Random rnd = new Random();
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + rnd.Next(1, 1000).ToString();
                string filename = $"captured_{timestamp}.jpg";

                ///////////
                // var depthImage = depthimage.Resource;
                // var decoder = BitmapDecoder.Create(depthImage, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.Default);
                // BitmapSource bitmapSource = decoder.Frames[0];
                // bitmapSource.CopyPixels(Int32Rect.Empty, depthImage.ImageData, depthImage.Stride * depthImage.Height, depthImage.Stride);
                ///////////

                try {
                    byte[] pixelData = new byte[depthimage.Resource.Size];
                   // _ = WriteLogToFile("1: depth image size " + depthimage.Resource.Size);
                    System.Runtime.InteropServices.Marshal.Copy(depthimage.Resource.ImageData, pixelData, 0, pixelData.Length);
                   // _ = WriteLogToFile("2");
                 


                    client.SendDepth(pixelData);

            //await SaveImageAsync(depthimage, filename);
            
                }
                catch (Exception ex)
                {
                    // Log or handle the exception appropriately
                    Console.WriteLine($"Error processing image: {ex.Message} \n StackTrace: {ex.StackTrace}");
                }


            });

            return pipeline;
        
        }

        private static CoordinateSystem LookAtPoint(Point3D sourcePoint, Point3D targetPoint)
        {
            var forward = (targetPoint - sourcePoint).Normalize();
            var left = UnitVector3D.ZAxis.CrossProduct(forward);
            var up = forward.CrossProduct(left);
            return new CoordinateSystem(sourcePoint, forward, left, up);
        }
    }
}
