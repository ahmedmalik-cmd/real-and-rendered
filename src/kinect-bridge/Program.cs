
namespace ThereminKinectBridge
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Web.Script.Serialization;
    using Microsoft.Kinect;
    using Microsoft.Kinect.Toolkit.FaceTracking;

    internal static class Program
    {
        private const string Prefix = "http://localhost:8182/";
        private const int DefaultSampleStep = 1;
        private const bool EnableBridgeDepthSmoothing = true;
        private static readonly bool EnableBridgeSpatialDepthSmoothing = false;
        private const int BridgeDepthTemporalResetMillimeters = 45;
        private const int BridgeDepthSpatialNeighborDeltaMillimeters = 45;
        private const double BridgeDepthTemporalCurrentWeight = 0.86;
        private static readonly object SnapshotGate = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
        private static readonly string[] CalibrationJointNames =
        {
            JointType.Head.ToString(),
            JointType.ShoulderCenter.ToString(),
            JointType.ShoulderLeft.ToString(),
            JointType.ShoulderRight.ToString(),
            JointType.Spine.ToString(),
            JointType.HipCenter.ToString(),
            JointType.HandLeft.ToString(),
            JointType.HandRight.ToString(),
        };
        private const uint MaxMissedFaceFrames = 90;

        private static TcpListener listener;
        private static List<SensorContext> sensors = new List<SensorContext>();
        private static int sampleStep = DefaultSampleStep;
        private static string colorMode = "rgb";
        private static FrameSnapshot latestSnapshot = FrameSnapshot.Disconnected("Starting Kinect bridge...");

        private static void Main(string[] args)
        {
            ParseArgs(args);

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                Cancellation.Cancel();
            };

            AppDomain.CurrentDomain.ProcessExit += (_, __) => Shutdown();

            TryStartSensors();
            StartServer();

            Console.WriteLine("Theremin Kinect bridge listening on {0}", Prefix);
            Console.WriteLine("Sensors started: {0}", sensors.Count);
            Console.WriteLine("Color mode: {0}", colorMode);
            Console.WriteLine("Press Ctrl+C to stop.");

            Cancellation.Token.WaitHandle.WaitOne();
            Shutdown();
        }

        private static void ParseArgs(IEnumerable<string> args)
        {
            var list = args.ToList();
            for (var index = 0; index < list.Count; index++)
            {
                var arg = list[index];
                if (string.Equals(arg, "--mode", StringComparison.OrdinalIgnoreCase) && index + 1 < list.Count)
                {
                    var candidate = (list[++index] ?? string.Empty).Trim().ToLowerInvariant();
                    colorMode = candidate == "infrared" ? "infrared" : "rgb";
                    continue;
                }

                if (string.Equals(arg, "--sample-step", StringComparison.OrdinalIgnoreCase) && index + 1 < list.Count)
                {
                    int parsed;
                    if (int.TryParse(list[++index], out parsed) && parsed > 0)
                    {
                        sampleStep = parsed;
                    }
                }
            }
        }

        private static int ClampSampleStep(int value)
        {
            return Math.Max(1, Math.Min(12, value));
        }

        private static double ClampTiltAngle(double value)
        {
            return Math.Max(-27.0, Math.Min(27.0, value));
        }

        private static string NormalizeColorMode(string requestedMode)
        {
            return string.Equals(requestedMode, "infrared", StringComparison.OrdinalIgnoreCase)
                ? "infrared"
                : "rgb";
        }

        private static void TryStartSensors()
        {
            var started = new List<SensorContext>();

            var ordinal = 0;
            foreach (var candidate in KinectSensor.KinectSensors)
            {
                var label = string.IsNullOrWhiteSpace(candidate.DeviceConnectionId) ? candidate.UniqueKinectId : candidate.DeviceConnectionId;

                if (candidate.Status != KinectStatus.Connected)
                {
                    started.Add(new SensorContext
                    {
                        Ordinal = ordinal++,
                        Sensor = candidate,
                        SensorId = candidate.UniqueKinectId,
                        Label = label,
                        LatestFrame = SensorSnapshot.Disconnected(
                            candidate.UniqueKinectId,
                            "Kinect unavailable: " + candidate.Status + ".",
                            colorMode),
                    });
                    continue;
                }

                try
                {
                    candidate.DepthStream.Enable(DepthImageFormat.Resolution320x240Fps30);
                    candidate.SkeletonStream.Enable();

                    if (string.Equals(colorMode, "rgb", StringComparison.OrdinalIgnoreCase))
                    {
                        candidate.ColorStream.Enable(ColorImageFormat.RgbResolution640x480Fps30);
                    }
                    else if (string.Equals(colorMode, "infrared", StringComparison.OrdinalIgnoreCase))
                    {
                        candidate.ColorStream.Enable(ColorImageFormat.InfraredResolution640x480Fps30);
                    }

                    candidate.AllFramesReady += SensorOnAllFramesReady;
                    candidate.Start();

                    var context = new SensorContext
                    {
                        Ordinal = ordinal++,
                        Sensor = candidate,
                        SensorId = candidate.UniqueKinectId,
                        Label = label,
                        LatestFrame = SensorSnapshot.Disconnected(candidate.UniqueKinectId, "Kinect connected. Waiting for depth frames...", colorMode),
                    };

                    started.Add(context);
                    Console.WriteLine("Connected to sensor: {0}", candidate.UniqueKinectId);
                }
                catch (Exception exception)
                {
                    Console.WriteLine("Failed to start sensor {0}: {1}", candidate.UniqueKinectId, exception.Message);

                    try
                    {
                        candidate.AllFramesReady -= SensorOnAllFramesReady;
                        if (candidate.IsRunning)
                        {
                            candidate.Stop();
                        }
                    }
                    catch
                    {
                    }
                }
            }

            lock (SnapshotGate)
            {
                sensors = started;
                UpdateAggregateSnapshotLocked();
            }

            if (started.Count == 0)
            {
                UpdateSnapshot(FrameSnapshot.Disconnected("No Kinect v1 sensors detected. Connect the devices and restart the bridge."));
                Console.WriteLine("No Kinect sensors found.");
            }
        }

        private static void SensorOnAllFramesReady(object sender, AllFramesReadyEventArgs eventArgs)
        {
            var sensor = sender as KinectSensor;
            if (sensor == null)
            {
                return;
            }

            SensorContext context;
            lock (SnapshotGate)
            {
                context = sensors.FirstOrDefault(candidate => ReferenceEquals(candidate.Sensor, sensor));
            }

            if (context == null)
            {
                return;
            }

            try
            {
                using (var depthFrame = eventArgs.OpenDepthImageFrame())
                using (var colorFrame =
                    (string.Equals(colorMode, "rgb", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(colorMode, "infrared", StringComparison.OrdinalIgnoreCase))
                        ? eventArgs.OpenColorImageFrame()
                        : null)
                using (var skeletonFrame = eventArgs.OpenSkeletonFrame())
                {
                    if (depthFrame == null)
                    {
                        return;
                    }

                    EnsureBuffers(context, depthFrame);
                    depthFrame.CopyDepthImagePixelDataTo(context.DepthPixels);
                    depthFrame.CopyPixelDataTo(context.RawDepthPixels);
                    var depthPixelsForMapping = ResolveDepthPixelsForMapping(context, depthFrame.Width, depthFrame.Height);
                    context.ActiveDepthPixels = depthPixelsForMapping;
                    sensor.CoordinateMapper.MapDepthFrameToSkeletonFrame(depthFrame.Format, depthPixelsForMapping, context.SkeletonPoints);
                    context.CurrentDepthFormat = depthFrame.Format;

                    if (string.Equals(colorMode, "rgb", StringComparison.OrdinalIgnoreCase))
                    {
                        sensor.CoordinateMapper.MapDepthFrameToColorFrame(
                            depthFrame.Format,
                            depthPixelsForMapping,
                            ColorImageFormat.RgbResolution640x480Fps30,
                            context.ColorPoints);
                        UpdateColorFrame(context, colorFrame);
                        if (colorFrame != null)
                        {
                            context.CurrentColorFormat = colorFrame.Format;
                        }
                    }
                    else if (string.Equals(colorMode, "infrared", StringComparison.OrdinalIgnoreCase))
                    {
                        sensor.CoordinateMapper.MapDepthFrameToColorFrame(
                            depthFrame.Format,
                            depthPixelsForMapping,
                            ColorImageFormat.InfraredResolution640x480Fps30,
                            context.ColorPoints);
                        UpdateColorFrame(context, colorFrame);
                        if (colorFrame != null)
                        {
                            context.CurrentColorFormat = colorFrame.Format;
                        }
                    }

                    if (skeletonFrame != null)
                    {
                        if (context.Skeletons == null || context.Skeletons.Length != skeletonFrame.SkeletonArrayLength)
                        {
                            context.Skeletons = new Skeleton[skeletonFrame.SkeletonArrayLength];
                        }

                        skeletonFrame.CopySkeletonDataTo(context.Skeletons);
                    }

                    var snapshot = BuildSensorSnapshot(context, depthFrame.Width, depthFrame.Height);

                    lock (SnapshotGate)
                    {
                        context.LatestFrame = snapshot;
                        UpdateAggregateSnapshotLocked();
                    }
                }
            }
            catch (Exception exception)
            {
                lock (SnapshotGate)
                {
                    context.LatestFrame = SensorSnapshot.Disconnected(context.SensorId, "Kinect frame error: " + exception.Message, colorMode);
                    UpdateAggregateSnapshotLocked();
                }
            }
        }

        private static void EnsureBuffers(SensorContext context, DepthImageFrame depthFrame)
        {
            if (context.DepthPixels == null || context.DepthPixels.Length != depthFrame.PixelDataLength)
            {
                context.DepthPixels = new DepthImagePixel[depthFrame.PixelDataLength];
            }

            if (context.RawDepthPixels == null || context.RawDepthPixels.Length != depthFrame.PixelDataLength)
            {
                context.RawDepthPixels = new short[depthFrame.PixelDataLength];
            }

            if (context.SkeletonPoints == null || context.SkeletonPoints.Length != depthFrame.PixelDataLength)
            {
                context.SkeletonPoints = new SkeletonPoint[depthFrame.PixelDataLength];
            }

            if (context.ColorPoints == null || context.ColorPoints.Length != depthFrame.PixelDataLength)
            {
                context.ColorPoints = new ColorImagePoint[depthFrame.PixelDataLength];
            }

            if (context.SmoothedDepthPixels == null || context.SmoothedDepthPixels.Length != depthFrame.PixelDataLength)
            {
                context.SmoothedDepthPixels = new DepthImagePixel[depthFrame.PixelDataLength];
            }

            if (context.SmoothedDepthValid == null || context.SmoothedDepthValid.Length != depthFrame.PixelDataLength)
            {
                context.SmoothedDepthValid = new bool[depthFrame.PixelDataLength];
            }
        }

        private static DepthImagePixel[] ResolveDepthPixelsForMapping(SensorContext context, int width, int height)
        {
            if (!EnableBridgeDepthSmoothing ||
                context.DepthPixels == null ||
                context.SmoothedDepthPixels == null ||
                context.SmoothedDepthValid == null)
            {
                return context.DepthPixels;
            }

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = x + (y * width);
                    var rawPixel = context.DepthPixels[index];
                    var currentDepth = rawPixel.Depth;
                    if (currentDepth <= 0)
                    {
                        context.SmoothedDepthPixels[index] = rawPixel;
                        context.SmoothedDepthValid[index] = false;
                        continue;
                    }

                    var spatialDepth = EnableBridgeSpatialDepthSmoothing
                        ? ResolveEdgePreservingSpatialDepth(context.DepthPixels, width, height, x, y, currentDepth)
                        : currentDepth;
                    var previousDepth = context.SmoothedDepthPixels[index].Depth;
                    var hasStablePrevious = context.SmoothedDepthValid[index] &&
                                            previousDepth > 0 &&
                                            Math.Abs(previousDepth - spatialDepth) <= BridgeDepthTemporalResetMillimeters;
                    var smoothedDepth = hasStablePrevious
                        ? (short)Math.Round((previousDepth * (1.0 - BridgeDepthTemporalCurrentWeight)) + (spatialDepth * BridgeDepthTemporalCurrentWeight))
                        : spatialDepth;

                    context.SmoothedDepthPixels[index] = new DepthImagePixel
                    {
                        Depth = smoothedDepth,
                        PlayerIndex = rawPixel.PlayerIndex,
                    };
                    context.SmoothedDepthValid[index] = true;
                }
            }

            return context.SmoothedDepthPixels;
        }

        private static short ResolveEdgePreservingSpatialDepth(DepthImagePixel[] pixels, int width, int height, int x, int y, short centerDepth)
        {
            var sum = (int)centerDepth;
            var count = 1;

            AccumulateDepthNeighbor(pixels, width, height, x - 1, y, centerDepth, ref sum, ref count);
            AccumulateDepthNeighbor(pixels, width, height, x + 1, y, centerDepth, ref sum, ref count);
            AccumulateDepthNeighbor(pixels, width, height, x, y - 1, centerDepth, ref sum, ref count);
            AccumulateDepthNeighbor(pixels, width, height, x, y + 1, centerDepth, ref sum, ref count);

            return (short)Math.Round(sum / (double)count);
        }

        private static void AccumulateDepthNeighbor(DepthImagePixel[] pixels, int width, int height, int x, int y, short centerDepth, ref int sum, ref int count)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return;
            }

            var neighborDepth = pixels[x + (y * width)].Depth;
            if (neighborDepth <= 0 ||
                Math.Abs(neighborDepth - centerDepth) > BridgeDepthSpatialNeighborDeltaMillimeters)
            {
                return;
            }

            sum += neighborDepth;
            count++;
        }

        private static void UpdateColorFrame(SensorContext context, ColorImageFrame colorFrame)
        {
            if (colorFrame == null)
            {
                return;
            }

            if (context.ColorPixels == null || context.ColorPixels.Length != colorFrame.PixelDataLength)
            {
                context.ColorPixels = new byte[colorFrame.PixelDataLength];
            }

            colorFrame.CopyPixelDataTo(context.ColorPixels);
            context.CurrentColorWidth = colorFrame.Width;
            context.CurrentColorHeight = colorFrame.Height;

            using (var bitmap = new Bitmap(colorFrame.Width, colorFrame.Height, PixelFormat.Format32bppRgb))
            {
                var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                var bitmapData = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, bitmap.PixelFormat);

                try
                {
                    if (colorFrame.Format == ColorImageFormat.InfraredResolution640x480Fps30)
                    {
                        WriteInfraredPixelsToBitmap(context.ColorPixels, bitmapData, colorFrame.Width, colorFrame.Height);
                    }
                    else
                    {
                        Marshal.Copy(context.ColorPixels, 0, bitmapData.Scan0, context.ColorPixels.Length);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }

                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Jpeg);
                    context.LatestColorJpeg = stream.ToArray();
                    context.LatestColorTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
        }

        private static void WriteInfraredPixelsToBitmap(byte[] infraredPixels, BitmapData bitmapData, int width, int height)
        {
            if (infraredPixels == null || infraredPixels.Length < width * height * 2)
            {
                return;
            }

            var outputPixels = new byte[width * height * 4];
            for (var index = 0; index < width * height; index++)
            {
                var sample = BitConverter.ToUInt16(infraredPixels, index * 2);
                var normalized = Math.Max(0.0, Math.Min(1.0, sample / 65535.0));
                var intensity = (byte)Math.Round(Math.Sqrt(normalized) * 255.0);
                var outputIndex = index * 4;
                outputPixels[outputIndex + 0] = intensity;
                outputPixels[outputIndex + 1] = intensity;
                outputPixels[outputIndex + 2] = intensity;
                outputPixels[outputIndex + 3] = 255;
            }

            Marshal.Copy(outputPixels, 0, bitmapData.Scan0, outputPixels.Length);
        }

        private static SensorSnapshot BuildSensorSnapshot(SensorContext context, int width, int height)
        {
            var pointCloud = new List<object>(width * height / (sampleStep * sampleStep));
            var colorSamples = new List<object>(width * height / (sampleStep * sampleStep));
            var depthPixels = context.ActiveDepthPixels ?? context.DepthPixels;
            var trackedSkeletons = (context.Skeletons ?? Array.Empty<Skeleton>())
                .Where(skeleton => skeleton != null && skeleton.TrackingState == SkeletonTrackingState.Tracked)
                .OrderBy(skeleton => skeleton.Position.Z)
                .ToList();
            var faces = BuildFacePayloads(context, trackedSkeletons);
            var accelerometer = context.Sensor.AccelerometerGetCurrentReading();

            for (var y = 0; y < height; y += sampleStep)
            {
                for (var x = 0; x < width; x += sampleStep)
                {
                    var index = x + (y * width);
                    var depthPixel = depthPixels[index];

                    if (depthPixel.Depth <= 0)
                    {
                        continue;
                    }

                    var mapped = context.SkeletonPoints[index];
                    if (mapped.Z < 0.45f || mapped.Z > 4.2f)
                    {
                        continue;
                    }

                    byte red;
                    byte green;
                    byte blue;
                    ResolvePointColor(context, index, mapped.Z, out red, out green, out blue);
                    var colorPoint = context.ColorPoints != null && index < context.ColorPoints.Length
                        ? context.ColorPoints[index]
                        : new ColorImagePoint { X = -1, Y = -1 };

                    pointCloud.Add(new object[]
                    {
                        Round(mapped.X * 1.08f),
                        Round(mapped.Y * 1.1f),
                        Round(mapped.Z),
                        depthPixel.PlayerIndex,
                        red,
                        green,
                        blue,
                        x,
                        y,
                    });

                    if (colorPoint.X >= 0 && colorPoint.Y >= 0)
                    {
                        colorSamples.Add(new object[]
                        {
                            colorPoint.X,
                            colorPoint.Y,
                            Round(mapped.X * 1.08f),
                            Round(mapped.Y * 1.1f),
                            Round(mapped.Z),
                        });
                    }
                }
            }

            var players = trackedSkeletons.Select(BuildPlayerPayload).ToList();
            var bodies = trackedSkeletons.Select(BuildBodyPayload).ToList();

            return new SensorSnapshot
            {
                id = context.SensorId,
                ordinal = context.Ordinal,
                label = context.Label,
                bridgeOk = true,
                connected = true,
                message = pointCloud.Count > 0 ? "Kinect streaming depth." : "Kinect connected, but no usable depth points yet.",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                colorMode = colorMode,
                colorPath = string.Format("/sensor/{0}/color.jpg", context.Ordinal),
                depthWidth = width,
                depthHeight = height,
                sampleStep = sampleStep,
                pointCloud = pointCloud,
                players = players.Cast<object>().ToList(),
                bodies = bodies.Cast<object>().ToList(),
                faces = faces.Cast<object>().ToList(),
                controls = BuildControls(trackedSkeletons),
                orientation = new
                {
                    elevationAngle = context.Sensor.ElevationAngle,
                    accelerometer = new object[]
                    {
                        Round(accelerometer.X),
                        Round(accelerometer.Y),
                        Round(accelerometer.Z),
                        Round(accelerometer.W),
                    },
                },
                colorUpdatedAt = context.LatestColorTimestamp,
                colorSamples = colorSamples,
            };
        }

        private static IList<object> BuildFacePayloads(SensorContext context, IReadOnlyList<Skeleton> trackedSkeletons)
        {
            var faces = new List<object>();
            if (!string.Equals(colorMode, "rgb", StringComparison.OrdinalIgnoreCase) ||
                context.ColorPixels == null ||
                context.RawDepthPixels == null ||
                context.CurrentColorFormat == ColorImageFormat.Undefined ||
                context.CurrentDepthFormat == DepthImageFormat.Undefined ||
                trackedSkeletons == null ||
                trackedSkeletons.Count == 0)
            {
                CleanupOldFaceTrackers(context, 0);
                return faces;
            }

            var currentFrameNumber = context.FaceFrameNumber + 1;
            context.FaceFrameNumber = currentFrameNumber;

            foreach (var skeleton in trackedSkeletons)
            {
                var trackerState = GetOrCreateFaceTracker(context, skeleton.TrackingId);
                if (trackerState == null || trackerState.Tracker == null)
                {
                    continue;
                }

                try
                {
                    using (var frame = trackerState.Tracker.Track(
                        context.CurrentColorFormat,
                        context.ColorPixels,
                        context.CurrentDepthFormat,
                        context.RawDepthPixels,
                        skeleton))
                    {
                        if (frame == null || !frame.TrackSuccessful)
                        {
                            continue;
                        }

                        trackerState.LastTrackedFrame = currentFrameNumber;
                        var rotation = frame.Rotation;
                        var translation = frame.Translation;
                        var shape3d = frame.Get3DShape();
                        var projected = frame.GetProjected3DShape();
                        var shape3dRows = new List<object>(shape3d.Count);
                        for (var index = 0; index < shape3d.Count; index++)
                        {
                            var point = shape3d[index];
                            shape3dRows.Add(new object[]
                            {
                                Round(point.X * 1.08f),
                                Round(point.Y * 1.1f),
                                Round(point.Z),
                            });
                        }

                        var projectedRows = new List<object>(projected.Count);
                        for (var index = 0; index < projected.Count; index++)
                        {
                            var point = projected[index];
                            projectedRows.Add(new object[]
                            {
                                Round(point.X),
                                Round(point.Y),
                            });
                        }

                        faces.Add(new
                        {
                            trackingId = skeleton.TrackingId,
                            rotation = new object[]
                            {
                                Round(rotation.X),
                                Round(rotation.Y),
                                Round(rotation.Z),
                            },
                            translation = new object[]
                            {
                                Round(translation.X * 1.08f),
                                Round(translation.Y * 1.1f),
                                Round(translation.Z),
                            },
                            shape3d = shape3dRows,
                            projectedShape = projectedRows,
                        });
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch
                {
                }
            }

            CleanupOldFaceTrackers(context, currentFrameNumber);
            return faces;
        }

        private static FaceTrackerState GetOrCreateFaceTracker(SensorContext context, int trackingId)
        {
            FaceTrackerState trackerState;
            if (!context.FaceTrackers.TryGetValue(trackingId, out trackerState))
            {
                try
                {
                    trackerState = new FaceTrackerState
                    {
                        Tracker = new FaceTracker(context.Sensor),
                        LastTrackedFrame = 0,
                    };
                    context.FaceTrackers[trackingId] = trackerState;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }

            return trackerState;
        }

        private static void CleanupOldFaceTrackers(SensorContext context, int currentFrameNumber)
        {
            var toRemove = new List<int>();
            foreach (var entry in context.FaceTrackers)
            {
                if (currentFrameNumber == 0 || (uint)(currentFrameNumber - entry.Value.LastTrackedFrame) > MaxMissedFaceFrames)
                {
                    toRemove.Add(entry.Key);
                }
            }

            foreach (var trackingId in toRemove)
            {
                try
                {
                    if (context.FaceTrackers[trackingId].Tracker != null)
                    {
                        context.FaceTrackers[trackingId].Tracker.Dispose();
                    }
                }
                catch
                {
                }

                context.FaceTrackers.Remove(trackingId);
            }
        }

        private static object BuildPlayerPayload(Skeleton skeleton)
        {
            var head = skeleton.Joints[JointType.Head].Position;
            return new
            {
                trackingId = skeleton.TrackingId,
                x = Round(head.X * 1.08f),
                y = Round(head.Y * 1.1f),
                z = Round(head.Z),
            };
        }

        private static object BuildBodyPayload(Skeleton skeleton)
        {
            var joints = new Dictionary<string, object>();
            foreach (var jointName in CalibrationJointNames)
            {
                JointType jointType;
                if (!Enum.TryParse(jointName, out jointType))
                {
                    continue;
                }

                var joint = skeleton.Joints[jointType];
                if (joint.TrackingState == JointTrackingState.NotTracked)
                {
                    continue;
                }

                joints[jointName] = new object[]
                {
                    Round(joint.Position.X * 1.08f),
                    Round(joint.Position.Y * 1.1f),
                    Round(joint.Position.Z),
                };
            }

            return new
            {
                trackingId = skeleton.TrackingId,
                joints = joints,
            };
        }

        private static void ResolvePointColor(SensorContext context, int index, float depthMeters, out byte red, out byte green, out byte blue)
        {
            if (string.Equals(colorMode, "rgb", StringComparison.OrdinalIgnoreCase) &&
                context.ColorPixels != null &&
                context.ColorPoints != null &&
                index >= 0 &&
                index < context.ColorPoints.Length &&
                context.CurrentColorWidth > 0 &&
                context.CurrentColorHeight > 0)
            {
                var colorPoint = context.ColorPoints[index];
                if (colorPoint.X >= 0 &&
                    colorPoint.Y >= 0 &&
                    colorPoint.X < context.CurrentColorWidth &&
                    colorPoint.Y < context.CurrentColorHeight)
                {
                    var colorIndex = ((colorPoint.Y * context.CurrentColorWidth) + colorPoint.X) * 4;

                    if (colorIndex >= 0 && colorIndex + 2 < context.ColorPixels.Length)
                    {
                        blue = context.ColorPixels[colorIndex];
                        green = context.ColorPixels[colorIndex + 1];
                        red = context.ColorPixels[colorIndex + 2];
                        return;
                    }
                }
            }

            var normalized = Clamp01((depthMeters - 0.45f) / 3.75f);
            var intensity = (byte)Math.Round((1.0 - normalized) * 255.0);
            red = intensity;
            green = intensity;
            blue = intensity;
        }

        private static object BuildControls(IReadOnlyList<Skeleton> trackedSkeletons)
        {
            if (trackedSkeletons.Count == 0)
            {
                return CreateEmptyControls();
            }

            var primary = trackedSkeletons[0];
            var head = primary.Joints[JointType.Head].Position;
            var shoulder = primary.Joints[JointType.ShoulderCenter].Position;
            var bodyDepth = Clamp01((3.4 - primary.Position.Z) / 2.6);
            var faceFocus = Clamp01((3.0 - head.Z) / 2.2);
            var personSeparation = trackedSkeletons.Count > 1
                ? Clamp01(Math.Abs(trackedSkeletons[0].Position.X - trackedSkeletons[1].Position.X) / 1.2)
                : 0.0;
            var silhouetteHeight = Clamp01((head.Y - shoulder.Y + 0.22) / 0.35);
            var energy = Clamp01((bodyDepth * 0.42) + (faceFocus * 0.26) + (personSeparation * 0.18));
            var nearEnergy = Clamp01(bodyDepth * 0.62);

            return new
            {
                bodyDepth = Round(bodyDepth),
                personSeparation = Round(personSeparation),
                faceFocus = Round((faceFocus * 0.7) + (silhouetteHeight * 0.3)),
                foilCoupling = 0.0,
                shielding = 0.0,
                ultrasonicDistance = Round(1.0 - bodyDepth),
                presence = Round(Clamp01(trackedSkeletons.Count / 2.0)),
                energy = Round(energy),
                nearEnergy = Round(nearEnergy),
                headX = Round(head.X),
                headY = Round(head.Y),
            };
        }

        private static object CreateEmptyControls()
        {
            return new
            {
                bodyDepth = 0.0,
                personSeparation = 0.0,
                faceFocus = 0.0,
                foilCoupling = 0.0,
                shielding = 0.0,
                ultrasonicDistance = 1.0,
                presence = 0.0,
                energy = 0.0,
                nearEnergy = 0.0,
                headX = 0.0,
                headY = 0.0,
            };
        }
        private static void UpdateAggregateSnapshotLocked()
        {
            var sensorSnapshots = sensors
                .Select(sensor => sensor.LatestFrame ?? SensorSnapshot.Disconnected(sensor.SensorId, "Waiting for frames...", colorMode))
                .OrderBy(sensor => sensor.id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sensorSnapshots.Count == 0)
            {
                latestSnapshot = FrameSnapshot.Disconnected("No Kinect v1 sensors detected. Connect the devices and restart the bridge.");
                return;
            }

            var primary = sensorSnapshots.FirstOrDefault(sensor => sensor.connected) ?? sensorSnapshots[0];
            var unavailable = sensorSnapshots
                .Where(sensor => !sensor.connected)
                .Select(sensor => string.Format("{0}: {1}", sensor.label, sensor.message))
                .ToList();
            latestSnapshot = new FrameSnapshot
            {
                bridgeOk = true,
                connected = sensorSnapshots.Any(sensor => sensor.connected),
                message = unavailable.Count > 0
                    ? "Some sensors unavailable. " + string.Join(" | ", unavailable)
                    : sensorSnapshots.Count > 1
                        ? "Multi-sensor frames ready. Use calibration in the viewer to fuse them."
                        : primary.message,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                colorMode = colorMode,
                sampleStep = Program.sampleStep,
                sensorCount = sensorSnapshots.Count,
                activeSensorIds = sensorSnapshots.Where(sensor => sensor.connected).Select(sensor => sensor.id).Cast<object>().ToList(),
                cameraPaths = sensorSnapshots.Select(sensor => sensor.colorPath).Where(path => !string.IsNullOrWhiteSpace(path)).Cast<object>().ToList(),
                pointCloud = primary.pointCloud ?? new List<object>(),
                players = primary.players ?? new List<object>(),
                bodies = primary.bodies ?? new List<object>(),
                controls = primary.controls ?? CreateEmptyControls(),
                sensors = sensorSnapshots.Cast<object>().ToList(),
                colorUpdatedAt = primary.colorUpdatedAt,
            };
        }

        private static void StartServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 8182);
            listener.Start();

            Task.Run(() =>
            {
                while (!Cancellation.IsCancellationRequested)
                {
                    try
                    {
                        var client = listener.AcceptTcpClient();
                        Task.Run(() => HandleClient(client), Cancellation.Token);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        if (Cancellation.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine(exception);
                    }
                }
            }, Cancellation.Token);
        }

        private static void HandleClient(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
            {
                var requestLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                string headerLine;
                do
                {
                    headerLine = reader.ReadLine();
                }
                while (!string.IsNullOrEmpty(headerLine));

                var parts = requestLine.Split(' ');
                var method = parts.Length > 0 ? parts[0] : "GET";
                var rawPath = parts.Length > 1 ? parts[1] : "/";
                var path = rawPath.Split('?')[0].TrimEnd('/').ToLowerInvariant();

                if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    WriteHttpResponse(stream, 204, string.Empty);
                    return;
                }
                if (path.StartsWith("/sensor/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/color.jpg", StringComparison.OrdinalIgnoreCase))
                {
                    var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    int ordinal;
                    if (segments.Length == 3 && int.TryParse(segments[1], out ordinal))
                    {
                        WriteBinaryResponse(stream, 200, "image/jpeg", GetColorJpegForSensor(ordinal));
                        return;
                    }
                }

                if (path.StartsWith("/sensor/", StringComparison.OrdinalIgnoreCase) && path.Contains("/tilt/"))
                {
                    var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    int ordinal;
                    double angle;
                    if (segments.Length == 4 &&
                        int.TryParse(segments[1], out ordinal) &&
                        double.TryParse(segments[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out angle))
                    {
                        string message;
                        double requestedAngle;
                        var ok = TrySetSensorTilt(ordinal, angle, out requestedAngle, out message);
                        WriteHttpResponse(stream, ok ? 200 : 400, Json.Serialize(new
                        {
                            ok = ok,
                            ordinal = ordinal,
                            requestedAngle = requestedAngle,
                            message = message,
                        }));
                        return;
                    }
                }

                if (path.StartsWith("/config/sample-step/", StringComparison.OrdinalIgnoreCase))
                {
                    var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    int requestedStep;
                    if (segments.Length == 3 && int.TryParse(segments[2], out requestedStep))
                    {
                        var applied = SetSampleStep(requestedStep);
                        WriteHttpResponse(stream, 200, Json.Serialize(new
                        {
                            ok = true,
                            sampleStep = applied,
                            message = "Sample step updated.",
                        }));
                        return;
                    }
                }

                if (path.StartsWith("/config/color-mode/", StringComparison.OrdinalIgnoreCase))
                {
                    var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length == 3)
                    {
                        string message;
                        var appliedMode = SetColorMode(segments[2], out message);
                        WriteHttpResponse(stream, 200, Json.Serialize(new
                        {
                            ok = true,
                            colorMode = appliedMode,
                            message = message,
                        }));
                        return;
                    }
                }

                switch (path)
                {
                    case "":
                    case "/":
                        WriteHttpResponse(stream, 200, Json.Serialize(new
                        {
                            bridgeOk = true,
                            connected = sensors.Count > 0,
                            sensorCount = sensors.Count,
                            colorMode = colorMode,
                            message = "Theremin Kinect bridge",
                        }));
                        return;
                    case "/state":
                        WriteHttpResponse(stream, 200, SerializeState());
                        return;
                    case "/frame":
                        WriteHttpResponse(stream, 200, SerializeSnapshot());
                        return;
                    case "/frame.bin":
                        WriteBinaryResponse(stream, 200, "application/octet-stream", SerializeSnapshotBinary());
                        return;
                    case "/sensors":
                        WriteHttpResponse(stream, 200, SerializeSensors());
                        return;
                    case "/color.jpg":
                        WriteBinaryResponse(stream, 200, "image/jpeg", GetPrimaryColorJpeg());
                        return;
                    default:
                        WriteHttpResponse(stream, 404, Json.Serialize(new
                        {
                            bridgeOk = false,
                            connected = false,
                            message = "Not found",
                        }));
                        return;
                }
            }
        }

        private static string SerializeState()
        {
            FrameSnapshot snapshot;
            lock (SnapshotGate)
            {
                snapshot = latestSnapshot;
            }

            return Json.Serialize(new
            {
                bridgeOk = snapshot.bridgeOk,
                connected = snapshot.connected,
                message = snapshot.message,
                updatedAt = snapshot.timestamp,
                colorUpdatedAt = snapshot.colorUpdatedAt,
                sensorCount = snapshot.sensorCount,
                activeSensorIds = snapshot.activeSensorIds,
                colorMode = snapshot.colorMode,
                sampleStep = Program.sampleStep,
            });
        }

        private static string SerializeSnapshot()
        {
            lock (SnapshotGate)
            {
                return Json.Serialize(latestSnapshot);
            }
        }

        private static byte[] SerializeSnapshotBinary()
        {
            lock (SnapshotGate)
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(new[] { (byte)'T', (byte)'K', (byte)'B', (byte)'3' });
                    writer.Write(latestSnapshot.timestamp);
                    writer.Write(latestSnapshot.sensorCount);

                    foreach (var sensorObject in latestSnapshot.sensors.Cast<SensorSnapshot>())
                    {
                        writer.Write(sensorObject.ordinal);
                        WriteBinaryString(writer, sensorObject.id ?? string.Empty);
                        WriteBinaryString(writer, sensorObject.label ?? string.Empty);
                        writer.Write(sensorObject.connected);
                        WriteBinaryString(writer, sensorObject.colorPath ?? string.Empty);

                        var orientation = sensorObject.orientation;
                        var elevationAngle = 0.0;
                        var accelerometer = new double[] { 0.0, -1.0, 0.0, 0.0 };
                        if (orientation != null)
                        {
                            var orientationJson = Json.Serialize(orientation);
                            var orientationMap = Json.DeserializeObject(orientationJson) as Dictionary<string, object>;
                            if (orientationMap != null)
                            {
                                if (orientationMap.ContainsKey("elevationAngle"))
                                {
                                    elevationAngle = Convert.ToDouble(orientationMap["elevationAngle"]);
                                }

                                if (orientationMap.ContainsKey("accelerometer"))
                                {
                                    var accelValues = orientationMap["accelerometer"] as object[];
                                    if (accelValues != null)
                                    {
                                        for (var index = 0; index < Math.Min(4, accelValues.Length); index++)
                                        {
                                            accelerometer[index] = Convert.ToDouble(accelValues[index]);
                                        }
                                    }
                                }
                            }
                        }

                          writer.Write((float)elevationAngle);
                          for (var index = 0; index < 4; index++)
                          {
                              writer.Write((float)accelerometer[index]);
                          }

                          writer.Write(sensorObject.depthWidth);
                          writer.Write(sensorObject.depthHeight);
                          writer.Write(Math.Max(1, sensorObject.sampleStep));

                          var bodies = sensorObject.bodies ?? new List<object>();
                          writer.Write(bodies.Count);
                          foreach (var bodyObject in bodies)
                          {
                              var trackingId = 0L;
                              var joints = new Dictionary<string, object>();
                              var bodyJson = Json.Serialize(bodyObject);
                              var bodyMap = Json.DeserializeObject(bodyJson) as Dictionary<string, object>;
                              if (bodyMap != null)
                              {
                                  if (bodyMap.ContainsKey("trackingId"))
                                  {
                                      trackingId = Convert.ToInt64(bodyMap["trackingId"]);
                                  }

                                  if (bodyMap.ContainsKey("joints"))
                                  {
                                      var jointMap = bodyMap["joints"] as Dictionary<string, object>;
                                      if (jointMap != null)
                                      {
                                          joints = jointMap;
                                      }
                                  }
                              }

                              writer.Write(trackingId);
                              writer.Write(joints.Count);
                              foreach (var jointEntry in joints)
                              {
                                  WriteBinaryString(writer, jointEntry.Key ?? string.Empty);
                                  var values = jointEntry.Value as object[];
                                  var jointX = 0.0;
                                  var jointY = 0.0;
                                  var jointZ = 0.0;
                                  if (values != null && values.Length >= 3)
                                  {
                                      jointX = Convert.ToDouble(values[0]);
                                      jointY = Convert.ToDouble(values[1]);
                                      jointZ = Convert.ToDouble(values[2]);
                                  }

                                  writer.Write((float)jointX);
                                  writer.Write((float)jointY);
                                  writer.Write((float)jointZ);
                              }
                          }

                          var points = sensorObject.pointCloud ?? new List<object>();
                          var pointRows = points
                              .Select(pointObject => pointObject as object[])
                              .Where(row => row != null && row.Length >= 7)
                              .ToList();
                        writer.Write(pointRows.Count);
                        foreach (var row in pointRows)
                        {
                            for (var index = 0; index < 7; index++)
                            {
                                writer.Write(Convert.ToSingle(row[index]));
                            }

                            var depthX = row.Length > 7 ? Convert.ToInt32(row[7]) : 0;
                            var depthY = row.Length > 8 ? Convert.ToInt32(row[8]) : 0;
                            writer.Write(depthX);
                            writer.Write(depthY);
                        }
                    }

                    writer.Flush();
                    return stream.ToArray();
                }
            }
        }

        private static string SerializeSensors()
        {
            lock (SnapshotGate)
            {
                return Json.Serialize(new
                {
                    bridgeOk = true,
                    colorMode = Program.colorMode,
                    sampleStep = Program.sampleStep,
                    sensors = sensors.Select(sensor => sensor.LatestFrame).Cast<object>().ToList(),
                });
            }
        }

        private static int SetSampleStep(int requestedStep)
        {
            lock (SnapshotGate)
            {
                sampleStep = ClampSampleStep(requestedStep);
                UpdateAggregateSnapshotLocked();
                return sampleStep;
            }
        }

        private static string SetColorMode(string requestedMode, out string message)
        {
            var normalizedMode = NormalizeColorMode(requestedMode);

            lock (SnapshotGate)
            {
                colorMode = normalizedMode;
                foreach (var context in sensors)
                {
                    if (context.LatestFrame != null)
                    {
                        context.LatestFrame.colorMode = normalizedMode;
                        context.LatestFrame.message = "Switching color mode...";
                    }
                }

                UpdateAggregateSnapshotLocked();
            }

            foreach (var context in sensors)
            {
                if (context == null || context.Sensor == null || context.Sensor.Status != KinectStatus.Connected)
                {
                    continue;
                }

                try
                {
                    if (context.Sensor.IsRunning)
                    {
                        context.Sensor.Stop();
                    }

                    context.Sensor.ColorStream.Enable(
                        string.Equals(normalizedMode, "infrared", StringComparison.OrdinalIgnoreCase)
                            ? ColorImageFormat.InfraredResolution640x480Fps30
                            : ColorImageFormat.RgbResolution640x480Fps30);
                    context.Sensor.Start();
                    context.LatestColorJpeg = null;
                    context.LatestColorTimestamp = 0;
                }
                catch (Exception exception)
                {
                    message = "Color mode switch encountered an error: " + exception.Message;
                    return normalizedMode;
                }
            }

            message = "Color mode switched to " + normalizedMode + ".";
            return normalizedMode;
        }

        private static bool TrySetSensorTilt(int ordinal, double requestedAngle, out double appliedAngle, out string message)
        {
            SensorContext context;
            lock (SnapshotGate)
            {
                context = sensors.FirstOrDefault(candidate => candidate != null && candidate.Ordinal == ordinal);
            }

            appliedAngle = ClampTiltAngle(requestedAngle);
            if (context == null || context.Sensor == null)
            {
                message = "Sensor not found.";
                return false;
            }

            if (context.Sensor.Status != KinectStatus.Connected)
            {
                message = "Sensor is not connected.";
                return false;
            }

            try
            {
                context.Sensor.ElevationAngle = (int)Math.Round(appliedAngle);
                message = "Tilt request sent.";
                return true;
            }
            catch (Exception exception)
            {
                message = "Tilt update failed: " + exception.Message;
                return false;
            }
        }

        private static void WriteHttpResponse(Stream stream, int statusCode, string body)
        {
            var buffer = Encoding.UTF8.GetBytes(body);
            var statusText = statusCode == 200 ? "OK" : statusCode == 204 ? "No Content" : statusCode == 404 ? "Not Found" : "Error";
            var header = string.Join("\r\n", new[]
            {
                "HTTP/1.1 " + statusCode + " " + statusText,
                "Content-Type: application/json; charset=utf-8",
                "Access-Control-Allow-Origin: *",
                "Access-Control-Allow-Methods: GET, OPTIONS",
                "Access-Control-Allow-Headers: Content-Type",
                "Cache-Control: no-store",
                "Connection: close",
                "Content-Length: " + buffer.Length,
                string.Empty,
                string.Empty,
            });
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);

            if (buffer.Length > 0)
            {
                stream.Write(buffer, 0, buffer.Length);
            }
        }
        private static void WriteBinaryResponse(Stream stream, int statusCode, string contentType, byte[] body)
        {
            var payload = body ?? new byte[0];
            var statusText = statusCode == 200 ? "OK" : statusCode == 204 ? "No Content" : statusCode == 404 ? "Not Found" : "Error";
            var header = string.Join("\r\n", new[]
            {
                "HTTP/1.1 " + statusCode + " " + statusText,
                "Content-Type: " + contentType,
                "Access-Control-Allow-Origin: *",
                "Access-Control-Allow-Methods: GET, OPTIONS",
                "Access-Control-Allow-Headers: Content-Type",
                "Cache-Control: no-store",
                "Connection: close",
                "Content-Length: " + payload.Length,
                string.Empty,
                string.Empty,
            });
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (payload.Length > 0)
            {
                stream.Write(payload, 0, payload.Length);
            }
        }

        private static void WriteBinaryString(BinaryWriter writer, string value)
        {
            var payload = Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write(payload.Length);
            if (payload.Length > 0)
            {
                writer.Write(payload);
            }
        }

        private static byte[] GetPrimaryColorJpeg()
        {
            lock (SnapshotGate)
            {
                var primary = sensors.FirstOrDefault(sensor => sensor.LatestColorJpeg != null && sensor.LatestColorJpeg.Length > 0);
                return primary != null ? primary.LatestColorJpeg : new byte[0];
            }
        }

        private static byte[] GetColorJpegForSensor(int ordinal)
        {
            lock (SnapshotGate)
            {
                var sensor = sensors.FirstOrDefault(candidate => candidate.Ordinal == ordinal);
                if (sensor == null || sensor.LatestColorJpeg == null || sensor.LatestColorJpeg.Length == 0)
                {
                    return new byte[0];
                }

                return sensor.LatestColorJpeg;
            }
        }

        private static void UpdateSnapshot(FrameSnapshot snapshot)
        {
            lock (SnapshotGate)
            {
                latestSnapshot = snapshot;
            }
        }

        private static void Shutdown()
        {
            try
            {
                if (listener != null)
                {
                    listener.Stop();
                }
            }
            catch
            {
            }

            List<SensorContext> toStop;
            lock (SnapshotGate)
            {
                toStop = sensors.ToList();
                sensors.Clear();
            }

            foreach (var context in toStop)
            {
                try
                {
                    context.Sensor.AllFramesReady -= SensorOnAllFramesReady;
                    foreach (var tracker in context.FaceTrackers.Values)
                    {
                        if (tracker.Tracker != null)
                        {
                            tracker.Tracker.Dispose();
                        }
                    }

                    if (context.Sensor.IsRunning)
                    {
                        context.Sensor.Stop();
                    }
                }
                catch
                {
                }
            }
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0)
            {
                return 0.0;
            }

            if (value > 1.0)
            {
                return 1.0;
            }

            return value;
        }

        private static double Round(double value)
        {
            return Math.Round(value, 3);
        }

        private sealed class SensorContext
        {
            public SensorContext()
            {
                FaceTrackers = new Dictionary<int, FaceTrackerState>();
            }

            public int Ordinal { get; set; }
            public KinectSensor Sensor { get; set; }
            public string SensorId { get; set; }
            public string Label { get; set; }
            public DepthImagePixel[] DepthPixels { get; set; }
            public DepthImagePixel[] ActiveDepthPixels { get; set; }
            public DepthImagePixel[] SmoothedDepthPixels { get; set; }
            public bool[] SmoothedDepthValid { get; set; }
            public short[] RawDepthPixels { get; set; }
            public SkeletonPoint[] SkeletonPoints { get; set; }
            public ColorImagePoint[] ColorPoints { get; set; }
            public Skeleton[] Skeletons { get; set; }
            public byte[] ColorPixels { get; set; }
            public int CurrentColorWidth { get; set; }
            public int CurrentColorHeight { get; set; }
            public byte[] LatestColorJpeg { get; set; }
            public long LatestColorTimestamp { get; set; }
            public SensorSnapshot LatestFrame { get; set; }
            public ColorImageFormat CurrentColorFormat { get; set; }
            public DepthImageFormat CurrentDepthFormat { get; set; }
            public int FaceFrameNumber { get; set; }
            public Dictionary<int, FaceTrackerState> FaceTrackers { get; private set; }
        }

        private sealed class FaceTrackerState
        {
            public FaceTracker Tracker { get; set; }
            public int LastTrackedFrame { get; set; }
        }

        private sealed class SensorSnapshot
        {
            public string id { get; set; }
            public int ordinal { get; set; }
            public string label { get; set; }
            public bool bridgeOk { get; set; }
            public bool connected { get; set; }
            public string message { get; set; }
            public long timestamp { get; set; }
            public string colorMode { get; set; }
            public string colorPath { get; set; }
            public int depthWidth { get; set; }
            public int depthHeight { get; set; }
            public int sampleStep { get; set; }
            public IList<object> pointCloud { get; set; }
            public IList<object> players { get; set; }
            public IList<object> bodies { get; set; }
            public IList<object> faces { get; set; }
            public object controls { get; set; }
            public object orientation { get; set; }
            public long colorUpdatedAt { get; set; }
            public IList<object> colorSamples { get; set; }

            public static SensorSnapshot Disconnected(string sensorId, string message, string mode)
            {
                return new SensorSnapshot
                {
                    id = sensorId,
                    ordinal = -1,
                    label = sensorId,
                    bridgeOk = true,
                    connected = false,
                    message = message,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    colorMode = mode,
                    colorPath = string.Empty,
                    depthWidth = 0,
                    depthHeight = 0,
                    sampleStep = Program.sampleStep,
                    pointCloud = new List<object>(),
                    players = new List<object>(),
                    bodies = new List<object>(),
                    faces = new List<object>(),
                    controls = CreateEmptyControls(),
                    orientation = new
                    {
                        elevationAngle = 0,
                        accelerometer = new object[] { 0.0, -1.0, 0.0, 0.0 },
                    },
                    colorUpdatedAt = 0,
                    colorSamples = new List<object>(),
                };
            }
        }

        private sealed class FrameSnapshot
        {
            public bool bridgeOk { get; set; }
            public bool connected { get; set; }
            public string message { get; set; }
            public long timestamp { get; set; }
            public string colorMode { get; set; }
            public int sampleStep { get; set; }
            public int sensorCount { get; set; }
            public IList<object> activeSensorIds { get; set; }
            public IList<object> cameraPaths { get; set; }
            public IList<object> pointCloud { get; set; }
            public IList<object> players { get; set; }
            public IList<object> bodies { get; set; }
            public object controls { get; set; }
            public IList<object> sensors { get; set; }
            public long colorUpdatedAt { get; set; }

            public static FrameSnapshot Disconnected(string message)
            {
                return new FrameSnapshot
                {
                    bridgeOk = true,
                    connected = false,
                    message = message,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    colorMode = Program.colorMode,
                    sampleStep = Program.sampleStep,
                    sensorCount = 0,
                    activeSensorIds = new List<object>(),
                    cameraPaths = new List<object>(),
                    pointCloud = new List<object>(),
                    players = new List<object>(),
                    bodies = new List<object>(),
                    controls = CreateEmptyControls(),
                    sensors = new List<object>(),
                    colorUpdatedAt = 0,
                };
            }
        }
    }
}
