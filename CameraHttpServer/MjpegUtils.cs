using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CameraHttpServer
{
    public class MjpegUtils
    {
        public static void ExtractJpegsFromMjpegStream(Stream stream, Func<byte[], int, int, bool> callBackFoundJpeg)
        {
            byte[] buffer = new byte[1024 * 512];
            var bufferPos = 0; // Length in buffer
            var startPosJpeg = -1;

            while (stream.CanRead)
            {
                if (buffer.Length == bufferPos)
                {
                    if (buffer.Length < 1024 * 1024 * 20) // Expand if needed
                    {
                        var newBuffer = new byte[buffer.Length * 2];
                        Buffer.BlockCopy(buffer, 0, newBuffer, 0, bufferPos);
                        buffer = newBuffer;
                    }
                    else
                    {
                        Console.WriteLine("Buffer full. Clearing buffer and potentially skipping frames.");
                        bufferPos = 0;
                    }
                }

                var len = stream.Read(buffer, bufferPos, buffer.Length - bufferPos);
                if (len == 0) // File ended
                    break;
                bufferPos += len;

                var i = bufferPos - (len + 1);  // Read the new incoming data + one byte of the old data to make sure we didn't miss the special bytes in the last read
                if (i < 0)
                    i = 0;

                for (; i < bufferPos - 1; i++)
                {
                    if (startPosJpeg < 0 && buffer[i] == 0xff && buffer[i + 1] == 0xd8)
                    {
                        startPosJpeg = i;
                        i++; // Skip the next byte, we've already checked it
                    }
                    else if (startPosJpeg >= 0 && buffer[i] == 0xff && buffer[i + 1] == 0xd9)
                    {
                        // We got a full jpeg! 
                        i += 2;
                        var jpegSize = i - startPosJpeg;

                        if (!callBackFoundJpeg(buffer, startPosJpeg, jpegSize)) // Return false to stop parsing this stream
                            return;

                        startPosJpeg = -1;

                        // Move rest of the buffer to the front
                        var lengthLeft = bufferPos - i;
                        Buffer.BlockCopy(buffer, i, buffer, 0, lengthLeft);
                        bufferPos = lengthLeft;

                        i = -1; // Start the next find at the start                                                        
                    }
                }
            }
        }

        static Dictionary<string, List<Func<byte[], int, int, bool>>> ActiveProcesses = new Dictionary<string, List<Func<byte[], int, int, bool>>>();
        public static void BeginJpegsFromProcessWithMjpegOutput(string processName, string arguments, Func<byte[], int, int, bool> callBackFoundJpeg)
        {
            var listKey = processName + " " + arguments;

            List<Func<byte[], int, int, bool>> list = null;
            lock (ActiveProcesses)
            {
                if (ActiveProcesses.ContainsKey(listKey))
                {
                    ActiveProcesses[listKey].Add(callBackFoundJpeg);
                    return;
                }
                ActiveProcesses.Add(listKey, new List<Func<byte[], int, int, bool>>());
                ActiveProcesses[listKey].Add(callBackFoundJpeg);
                list = ActiveProcesses[listKey];
            }

            Thread t = new Thread(new ThreadStart(() =>
            {
                Console.WriteLine("Starting process thread " + listKey);

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        RedirectStandardInput = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        Arguments = arguments,
                        FileName = processName
                    },
                    EnableRaisingEvents = true
                };

                process.ErrorDataReceived += (sender, eventArgs) =>
                {
                    //Console.WriteLine(eventArgs.Data);
                };
                process.Start();
                process.BeginErrorReadLine();

                var stream = process.StandardOutput.BaseStream;

                var callBacks = list.ToList();
                ExtractJpegsFromMjpegStream(stream, (buffer, offset, count) =>
                {
                    foreach (var callBack in callBacks)
                    {
                        var keepGoing = callBack(buffer, offset, count);
                        if (!keepGoing)
                            list.Remove(callBack);
                    }

                    callBacks = list.ToList();
                    return callBacks.Count > 0; // Keep going
                });

                // Exiting thread, remove us from the active processes
                lock (ActiveProcesses)
                {
                    callBacks = list.ToList();
                    stream.Close();
                    
                    try
                    {                        
                        process.Kill();
                    }
                    catch { }
                    ActiveProcesses.Remove(listKey);
                }

                foreach (var callBack in callBacks)
                    callBack(null, 0, 0); // Send a signal to the potentially remaining callback that our stream ended

                Console.WriteLine("Ending process thread " + listKey);
            }));
            t.Start();
        }

        public static void BeginPipeMjpegIntoProcessAndSendOutputToStream(string processName, string arguments, string resultProcessName, string resultProcessArguments, Stream resultStream, Func<bool> shouldKeepGoing=null)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,                    
                    Arguments = resultProcessArguments,
                    FileName = resultProcessName
                },
                EnableRaisingEvents = true                
            };

            process.ErrorDataReceived += (sender, eventArgs) =>
            {
                Console.WriteLine(eventArgs.Data);
            };

            process.Start();
            process.BeginErrorReadLine();
            var outputStream = process.StandardOutput.BaseStream;
            var inputStream = process.StandardInput.BaseStream;
            
            
            var isClosing = false;
            Action closeProcess = () =>
            {
                if (isClosing)
                    return;
                isClosing = true;
                Console.WriteLine("Starting to close process");
                // We'll try to close the process by ending the input pipe first, so ffmpeg can free up any gpu memory allocations.
                inputStream.Flush();
                inputStream.Close();

                var start = DateTime.UtcNow;
                byte[] temp = new byte[1000];
                try
                {
                    while ((DateTime.UtcNow - start).TotalSeconds < 5)
                    {
                        if (outputStream.Read(temp, 0, temp.Length) == 0) // Make sure there is no data waiting, otherwise defunct processes might appear
                            break;
                    }
                }
                catch (Exception e) {
                    Console.WriteLine("closeProcess() exception 1: " + e.Message);
                }

                outputStream.Close();
                resultStream.Close();
                try
                {
                    process.CloseMainWindow();
                    process.WaitForExit(1000);
                }
                catch (Exception e)
                {
                    Console.WriteLine("closeProcess() exception 2: " + e.Message);
                }

                try
                {
                    process.Kill();
                }
                catch (Exception e)
                {
                    Console.WriteLine("closeProcess() exception 3: " + e.Message);
                }

                try
                {
                    process.Dispose();
                }
                catch (Exception e)
                {
                    Console.WriteLine("closeProcess() exception 4: " + e.Message);
                }
                Console.WriteLine("Closed process");
            };

            MjpegUtils.BeginJpegsFromProcessWithMjpegOutput(processName, arguments, (buffer, offset, count) =>
            {
                if (buffer == null || !outputStream.CanRead || !inputStream.CanWrite || !resultStream.CanWrite)
                {
                    // Process/stream ended
                    closeProcess();
                    return false;
                }

                try
                {
                    inputStream.Write(buffer, offset, count);
                }
                catch
                {
                    closeProcess();
                    return false;
                }

                var shouldRequestNextJpeg = inputStream.CanWrite && resultStream.CanWrite && (shouldKeepGoing == null || shouldKeepGoing());
                if (!shouldRequestNextJpeg)
                    closeProcess(); 
                return shouldRequestNextJpeg;
            });

            var outputSendThread = new Thread(new ThreadStart(() =>
            {
                byte[] buffer = new byte[1024 * 1024 * 1];
                while (inputStream.CanWrite && resultStream.CanWrite && outputStream.CanRead && (shouldKeepGoing == null || shouldKeepGoing()))
                {
                    var len = outputStream.Read(buffer);
                    try
                    {
                        resultStream.Write(buffer, 0, len);
                    }
                    catch
                    {
                        break;
                    }
                }

                closeProcess();
            }));
            outputSendThread.Start();
        }
    }
}
