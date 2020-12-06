using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CameraHttpServer
{
    public class Configuration
    {
        public int Port { get; set; }
        public List<ConfigurationFeed> Feeds { get; set; }
        // Optional HTTP authentication
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public class ConfigurationFeed
    {
        public string Name { get; set; }
        public string InputProcessName { get; set; }
        public string InputProcessArguments { get; set; }

        public string OutputProcessName { get; set; }
        public string OutputProcessArguments { get; set; }
        public string OutputContentType { get; set; }

        // Motion detection
        public double MotionDetectionPercentage { get; set; } // If % of the pixels are changed, we'll detect it as motion, 0 to disable
        public int MotionDetectionFrameCount { get; set; }
        public double MotionDetectionSecondsBetweenFrames { get; set; }         
        public double MotionColorIgnorePercentage { get; set; } // If a pixel changes less than x %, we won't mark it as changed 
        public string MotionProcessName { get; set; }
        public string MotionProcessArguments { get; set; }
        public double MotionSecondsLinger { get; set; }
        public string MotionRecordingFileName { get; set; } // Anything between [ ] will be replaced by DateTime.Now.ToString("...") 


        // Keep a snapshot of the camera every x seconds, 0 to disable
        public double SnapshotSecondsInterval { get; set; }


        // Used internally, not actual configuration settings
        internal byte[] SnapshotBytes { get; set; }
        internal int SnapshotBytesLength { get; set; }
        internal Stream MotionResultStream { get; set; }
    }
}
