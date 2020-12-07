using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace CameraHttpServer
{
    class Program
    {        
        static bool IsRunning = true;
        static Configuration Configuration = null;
        static Dictionary<string, ConfigurationFeed> ConfigurationFeeds = null;
        static TcpListener Server = null;
        static void Main(string[] args)
        {
            if (!File.Exists("config.json"))
            {
                Console.WriteLine("config.json missing.");
                return;
            }

            Configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllText("config.json"));
            ConfigurationFeeds = Configuration.Feeds.ToDictionary(a => a.Name, a => a);

            // Start capture/motion detection threads, these require an always-on camera feed instead of on-demand.
            foreach (var feed in Configuration.Feeds.Where(a=>a.SnapshotSecondsInterval > 0 || a.MotionDetectionPercentage > 0))
                StartCaptureAndMotionDetection(feed);

            Server = new TcpListener(IPAddress.Any, Configuration.Port == 0 ? 8082 : Configuration.Port);
            Server.Start();

            while (IsRunning)
            {
                var client = Server.AcceptSocket();
                new Thread(new ThreadStart(() =>
                {
                    var keepAlive = false;
                    try
                    {
                        keepAlive = HandleClient(client);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Exception: " + e.Message);
                        client.Close();
                    }

                    if (!keepAlive)
                    {
                        try
                        {
                            client.Shutdown(SocketShutdown.Both);
                        }
                        catch { }

                        client.Close();
                    }
                })).Start();
            }

            Server.Stop();

            foreach (var feed in ConfigurationFeeds)
            {
                if (feed.Value.MotionResultStream != null && feed.Value.MotionResultStream.CanWrite)
                    feed.Value.MotionResultStream.Close();
            }
        }
        static bool BufferContainsRequest(byte[] buffer)
        {
            return ASCIIEncoding.ASCII.GetString(buffer).Contains("\r\n\r\n");
        }

        static bool HandleClient(Socket socket)
        {
            var buffer = new byte[1024 * 10];
            var requestBufferPos = 0;
            while (!BufferContainsRequest(buffer))
            {
                var newBytes = socket.Receive(buffer, requestBufferPos, buffer.Length - requestBufferPos, SocketFlags.None);
                if (newBytes == 0 || !socket.Connected)
                    return false;

                requestBufferPos += newBytes;
                if (requestBufferPos == buffer.Length)
                    throw new Exception("Receive buffer filled");
            }

            var fullRequest = ASCIIEncoding.ASCII.GetString(buffer);
            var request = fullRequest.Substring(0, fullRequest.IndexOf("\r"));
            var requestParts = request.Split(' ');
            if (requestParts.Length < 2 || !requestParts[1].StartsWith("/"))
                throw new Exception("Invalid request");

            if (!string.IsNullOrEmpty(Configuration.Username) && !string.IsNullOrEmpty(Configuration.Password))
            {
                // Require HTTP basic auth
                var hasAccess = false;                

                var posBasicAuth = fullRequest.IndexOf("Authorization: Basic ");
                if (posBasicAuth >= 0)
                {
                    var basicAuthDetails = fullRequest.Substring(posBasicAuth + 21);
                    basicAuthDetails = basicAuthDetails.Substring(0, basicAuthDetails.IndexOf("\r"));
                    var usernameAndPass = ASCIIEncoding.UTF8.GetString(Convert.FromBase64String(basicAuthDetails));
                    if (usernameAndPass.Contains(":") && 
                        Configuration.Username == usernameAndPass.Substring(0, usernameAndPass.IndexOf(":")) &&
                        Configuration.Password == usernameAndPass.Substring(usernameAndPass.IndexOf(":") + 1))
                        hasAccess = true;
                }

                if (!hasAccess)
                {
                    socket.Send(ASCIIEncoding.ASCII.GetBytes("HTTP/1.1 401 Unauthorized\r\nConnection: Close\r\nWWW-Authenticate: Basic realm=\"Camera HTTP Server\"\r\nContent-Type: text/html\r\n\r\nInvalid username or password."));
                    return false;
                }
            }

            Console.WriteLine(socket.RemoteEndPoint.ToString() + " is requesting " + requestParts[1]);

            var requestUrlParts = requestParts[1].Split('/');
            var requestFeed = requestUrlParts[1];

            var templateFolder = Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "HtmlTemplates");
            if (requestFeed == "")
            {
                // Generic main page
                var page = File.ReadAllText(Path.Combine(templateFolder, "Index.html"));
                page = page.Replace("[feeds]", JsonSerializer.Serialize(ConfigurationFeeds.Select(a => new
                {
                    a.Value.Name,
                    a.Value.SnapshotSecondsInterval                   
                })));
                socket.Send(ASCIIEncoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: Close\r\nContent-Type: text/html\r\n\r\n" + page));
                return false;
            } else if (requestFeed == "shutdown") {                
                socket.Send(ASCIIEncoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: Close\r\nContent-Type: text/html\r\n\r\nServer shut down"));                
                IsRunning = false;
                return false;
            }
            else if (!ConfigurationFeeds.ContainsKey(requestFeed))
            {
                socket.Send(ASCIIEncoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nConnection: Close\r\nContent-Type: text/html\r\n\r\nFeed not found"));
                return false;
            }
            else if (requestUrlParts.Length < 3 || requestUrlParts[2] == "")
            {
                var page = File.ReadAllText(Path.Combine(templateFolder, "Feed.html")).Replace("[name]", requestFeed);
                socket.Send(ASCIIEncoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: Close\r\nContent-Type: text/html\r\n\r\n"+ page));
                return false;
            }
            else if (requestUrlParts[2] == "mjpeg")
                if (requestUrlParts.Length > 3)
                    return HandleHttpRequestMjpegStream(socket, ConfigurationFeeds[requestFeed], false, double.Parse(requestUrlParts[3]));
                else
                    return HandleHttpRequestMjpegStream(socket, ConfigurationFeeds[requestFeed]);

            else if (requestUrlParts[2] == "stream" && !string.IsNullOrEmpty(ConfigurationFeeds[requestFeed].OutputProcessName))
                return HandleHttpRequestOutputStream(socket, ConfigurationFeeds[requestFeed]);

            else if (requestUrlParts[2] == "snapshot" && ConfigurationFeeds[requestFeed].SnapshotBytes != null)
                return HandleHttpRequestSnapshot(socket, ConfigurationFeeds[requestFeed]);

            socket.Send(ASCIIEncoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nConnection: Close\r\nContent-Type: text/html\r\n\r\nUnknown action"));
            return false;
        }
        static bool HandleHttpRequestSnapshot(Socket socket, ConfigurationFeed requestedFeed, bool asRawMjpegStream = false, double? maxFps = null)
        {
            socket.Send(ASCIIEncoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" +
                "Connection: Close\r\n" +
                "Access-Control-Allow-Headers: DNT,User-Agent,X-Requested-With,If-Modified-Since,Cache-Control,Content-Type,Range\r\n" +
                "Access-Control-Allow-Headers: GET, POST, OPTIONS\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Expose-Headers: *\r\n" +
                "Content-Type: image/jpeg\r\n\r\n"));
            lock (requestedFeed)
            {
                socket.Send(requestedFeed.SnapshotBytes, 0, requestedFeed.SnapshotBytesLength, SocketFlags.None);
            }
            return false;
        }
        static bool HandleHttpRequestMjpegStream(Socket socket, ConfigurationFeed requestedFeed, bool asRawMjpegStream = false, double? maxFps = null)
        {
            socket.Send(ASCIIEncoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" +
                "Connection: Close\r\n" +
                "Access-Control-Allow-Headers: DNT,User-Agent,X-Requested-With,If-Modified-Since,Cache-Control,Content-Type,Range\r\n" +
                "Access-Control-Allow-Headers: GET, POST, OPTIONS\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Expose-Headers: *\r\n" +
                (asRawMjpegStream ? "Content-Type: video/x-motion-jpeg\r\n\r\n" : "Content-Type: multipart/x-mixed-replace;boundary=derpyderpderp\r\n")
            ));

            byte[] headerToSend = asRawMjpegStream ? (byte[])null : ASCIIEncoding.ASCII.GetBytes("\r\n--derpyderpderp\r\nContent-type: image/jpeg\r\n\r\n");

            bool isCurrentlySending = false;
            DateTime lastSentDate = DateTime.MinValue; // for timeouts            
            double maxFpsSec = 0;
            if (maxFps.HasValue)
                maxFpsSec = (double)1 / maxFps.Value; 

            MjpegUtils.BeginJpegsFromProcessWithMjpegOutput(requestedFeed.InputProcessName, requestedFeed.InputProcessArguments, (buffer, offset, count) =>
            {
                if (isCurrentlySending)
                {
                    if (lastSentDate.AddSeconds(15) < DateTime.UtcNow)
                    {
                        // Timeout
                        Console.WriteLine("Sending data to client timeout. Closing connection\r\n");
                        socket.Close(); 
                        return false; 
                    }
                    return IsRunning; // We'll skip this frame
                }
                if (maxFps.HasValue && (DateTime.UtcNow - lastSentDate).TotalSeconds < maxFpsSec)
                    return IsRunning; // Skip this frame to limit fps

                if (buffer == null)
                {
                    // Process/stream ended
                    socket.Close();
                    return false;
                }
                try
                {
                    isCurrentlySending = true;
                    lastSentDate = DateTime.UtcNow;

                    if (headerToSend != null) // mjpeg over http
                    {                        
                        socket.BeginSend(headerToSend, 0, headerToSend.Length, SocketFlags.None, (a) =>
                        {
                            socket.EndSend(a);
                            socket.Send(buffer, offset, count, SocketFlags.None);
                            isCurrentlySending = false;
                        }, null);
                    }
                    else // raw
                    {
                        socket.BeginSend(buffer, offset, count, SocketFlags.None, (a) =>
                        {
                            socket.EndSend(a);

                            // We send some padding for raw streams. This is from the test mjpeg stream we've captured
                            var extraBytesNeeded = 8 - (count % 8);
                            if (extraBytesNeeded > 0 && extraBytesNeeded < 8)
                            {
                                var zeroBytes = new byte[extraBytesNeeded];
                                socket.Send(zeroBytes, 0, extraBytesNeeded, SocketFlags.None);
                            }
                            isCurrentlySending = false;
                        }, null);

                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Could not send data: " + e.Message);
                    socket.Close();
                    return false;
                }
                return socket.Connected && IsRunning; // Keep going
            });
            return true;
        }


        static bool HandleHttpRequestOutputStream(Socket socket, ConfigurationFeed requestedFeed)
        {
            // The CORS headers are needed to support Google Nest Hub and Chromecast
            socket.Send(ASCIIEncoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" +
                "Connection: Close\r\n" +
                "Access-Control-Allow-Headers: DNT,User-Agent,X-Requested-With,If-Modified-Since,Cache-Control,Content-Type,Range\r\n" +
                "Access-Control-Allow-Headers: GET, POST, OPTIONS\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Expose-Headers: *\r\n" +
                "Content-Type: " + requestedFeed.OutputContentType + "\r\n\r\n"));

            var ns = new NetworkStream(socket, true);
            MjpegUtils.BeginPipeMjpegIntoProcessAndSendOutputToStream(requestedFeed.InputProcessName, requestedFeed.InputProcessArguments, requestedFeed.OutputProcessName, requestedFeed.OutputProcessArguments, ns, () => IsRunning && socket.Connected);
            return true;
        }

        static void StartRecording(ConfigurationFeed feed)
        {
            if (feed.MotionResultStream != null && feed.MotionResultStream.CanWrite)
                return;

            Console.WriteLine("Start recording");

            // Get filename for recording and replace [name] and [yyyyMMdd] parts
            var outputFileName = string.IsNullOrEmpty(feed.MotionRecordingFileName) ? "[yyyyMMdd]" + Path.DirectorySeparatorChar + "[name]_[yyyyMMdd HHmmss].mp4" : feed.MotionRecordingFileName;
            outputFileName = outputFileName.Replace("[name]", feed.Name);
            outputFileName = new Regex("\\[(.*?)\\]").Replace(outputFileName, match => DateTime.Now.ToString(match.Groups[1].Value));
            
            if (outputFileName.Contains(Path.DirectorySeparatorChar))
            {
                var directory = outputFileName.Substring(0, outputFileName.LastIndexOf(Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
            }
            
            feed.MotionResultStream = File.OpenWrite(outputFileName);
            MjpegUtils.BeginPipeMjpegIntoProcessAndSendOutputToStream(feed.InputProcessName, feed.InputProcessArguments, feed.MotionProcessName, feed.MotionProcessArguments, feed.MotionResultStream, () => IsRunning && feed.MotionResultStream != null);
        }
        static void StopRecording(ConfigurationFeed feed)
        {
            feed.MotionResultStream.Close();
            feed.MotionResultStream = null;
            Console.WriteLine("Stop recording");
        }

        static double CompareColors(SKColor a, SKColor b)
        {
            return 100 * ((double)(Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue)) / (256.0 * 3.0));
        }
        static void StartCaptureAndMotionDetection(ConfigurationFeed feed)
        {
            DateTime lastSnapshot = DateTime.MinValue;
            DateTime lastMotionDectionFrame = DateTime.MinValue;
            DateTime lastMotionDetected = DateTime.MinValue;
            SKBitmap motionDetectionLastFrame = null;
            bool isCurrentlyRecording = false;
            var motionDetectionChangeDetectedFrames = new List<bool>();            

            byte[] motionDetectionCurrentFrame = null;
            int motionDetectionCurrentFrameLength = 0;
            
            Thread motionDetectionThread = null;
            var motionDetectionThreadIsRunning = true;
            
            if (feed.MotionDetectionPercentage > 0)
            {
                motionDetectionThread = new Thread(new ThreadStart(() =>
                {
                    Console.WriteLine("Starting motion detection thread");
                    while (IsRunning && motionDetectionThreadIsRunning)
                    {

                        if (motionDetectionCurrentFrameLength == 0)
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        SKBitmap newFrame = null;

                        using (var stream = new MemoryStream(motionDetectionCurrentFrame))
                        using (SKCodec codec = SKCodec.Create(stream))
                        {
                            SKImageInfo info = codec.Info;
                            SKSizeI supportedScale = codec.GetScaledDimensions((float)200 / info.Width);
                            SKImageInfo nearest = new SKImageInfo(supportedScale.Width, supportedScale.Height);
                            newFrame = SKBitmap.Decode(codec, nearest);
                        }
                    
                        motionDetectionCurrentFrameLength = 0; // Mark as read

                        if (motionDetectionLastFrame != null)
                        {
                            // analyse last x captures, if at least n % is different in all of them (using a grid, not compare all pixels), start recording process, stop if there is no movement for linger-seconds
                            var step = newFrame.Height / 10;
                            var pixelsChanged = 0;
                            var pixelsTotal = 0;
                            for (var y = (int)(step / 2); y < newFrame.Height; y += step)
                            {
                                for (var x = (int)(step / 2); x < newFrame.Width; x += step)
                                {
                                    if (CompareColors(newFrame.GetPixel(x, y), motionDetectionLastFrame.GetPixel(x, y)) > feed.MotionColorIgnorePercentage)
                                        pixelsChanged++;
                                    pixelsTotal++;
                                }
                            }
                            motionDetectionLastFrame.Dispose();

                            var percentageDifference = (((double)pixelsChanged / (double)pixelsTotal) * 100);
                            motionDetectionChangeDetectedFrames.Add((percentageDifference > feed.MotionDetectionPercentage));

                            if (motionDetectionChangeDetectedFrames.Count > feed.MotionDetectionFrameCount)
                                motionDetectionChangeDetectedFrames.RemoveAt(0);

                            var totalDetectedFrames = motionDetectionChangeDetectedFrames.Where(a => a == true).Count();
                            if ((totalDetectedFrames == feed.MotionDetectionFrameCount) || (isCurrentlyRecording && totalDetectedFrames > 0))
                            {
                                // Start or keep continuing recording
                                Console.WriteLine("Detection! " + Math.Round(percentageDifference, 1) + " %");
                                lastMotionDetected = DateTime.UtcNow;
                                if (!isCurrentlyRecording)
                                {
                                    StartRecording(feed);
                                    isCurrentlyRecording = true;
                                }
                            }
                            else
                            {
                                Console.WriteLine("No detection " + Math.Round(percentageDifference, 1) + " %");
                                if (isCurrentlyRecording && (DateTime.UtcNow - lastMotionDetected).TotalSeconds > feed.MotionSecondsLinger)
                                {
                                    StopRecording(feed);
                                    isCurrentlyRecording = false;
                                }
                            }
                        }
                        motionDetectionLastFrame = newFrame;
                    }
                    Console.WriteLine("Ending motion detection thread");
                }));
                motionDetectionThread.Start();
            }

            MjpegUtils.BeginJpegsFromProcessWithMjpegOutput(feed.InputProcessName, feed.InputProcessArguments, (buffer, offset, count) =>
            {
                if (buffer == null)
                {
                    // process ended, todo: restart
                    motionDetectionThreadIsRunning = false;
                    return false;
                }
                    
                if (feed.SnapshotSecondsInterval > 0 && (DateTime.UtcNow - lastSnapshot).TotalSeconds >= feed.SnapshotSecondsInterval)
                {
                    lastSnapshot = DateTime.UtcNow;
                    lock (feed)
                    {
                        if (feed.SnapshotBytes == null || feed.SnapshotBytes.Length < count)
                            feed.SnapshotBytes = new byte[count * 2]; // Give some extra space to prevent resizing too many times at the start

                        feed.SnapshotBytesLength = count;
                        Buffer.BlockCopy(buffer, offset, feed.SnapshotBytes, 0, count);
                    }
                }

                if (feed.MotionDetectionPercentage > 0 && (DateTime.UtcNow - lastMotionDectionFrame).TotalSeconds >= feed.MotionDetectionSecondsBetweenFrames)
                {    
                    lastMotionDectionFrame = DateTime.UtcNow;
                    
                    if (motionDetectionCurrentFrameLength == 0) // Only update the buffer when the image code isn't still busy with this byte buffer
                    {
                        if (motionDetectionCurrentFrame == null || motionDetectionCurrentFrame.Length < count)
                            motionDetectionCurrentFrame = new byte[count * 2]; // Give some extra space to prevent resizing too many times at the start
                        Buffer.BlockCopy(buffer, offset, motionDetectionCurrentFrame, 0, count);
                        motionDetectionCurrentFrameLength = count;
                    }
                }                
                return IsRunning; // Keep going
            });
        }        
    }
}
