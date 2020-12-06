# Comgenie's Awesome Camera Server

This is a very basic but flexible cross platform Camera server written in C# .NET Core with support for multiple (on-demand) feeds, motion detection and re-encoded streams (tested on Google Nest Hub).

This application works by starting other processes when required to supply an MJPEG stream. Currently this is tested with ffmpeg ( and v4l2-ctl on linux ). 

For cross platform motion detection this application requires the nuget/library: SkiaSharp. There are no other external dependencies.

## Configuration

A couple of example config.json files are included but these should be adjusted to match your situation. 

Here the most important settings are listed for each feed:

### Feed Input

Use the settings *InputProcessName* and *InputProcessArguments* to define what process to use to get a mjpeg stream from your camera. Make sure the process will output all mjpeg data as this application will capture the process output.

### Feed Output

Here you can find the settings *OutputProcessName* and *OutputProcessArguments*. This is used to re-encode into a new stream ( http://url/feedname/stream ) using the following steps:

- A new feed-output-process is started for each new client requesting the stream.
- The mjpeg data from the feed-input-process is send to the input of this feed-output-process.
- The output is taken from this feed-output-process and send to the client.
- When the client disconnects the feed-output-process will be terminated. 

### Motion

Here are all the motion detection parameters and another name+arguments set to configure what process is used. This process will be started when motion is detected, and terminated when there is no motion detected for at least *MotionSecondsLinger* seconds. The output is saved under the name configured in *MotionRecordingFileName* (directories are created automatically).

Note: If this setting is used (more than 0, and snapshots are enabled: *SnapshotSecondsInterval* more than 0): The input process will be active at all times. When having multiple feeds active, it might use up all usb bandwidth.

### Snapshot

Use the setting *SnapshotSecondsInterval* to automatically keep a jpeg from the mjpeg stream in memory every x seconds. This is displayed on the main page of the http server.

Note: If this setting is used (more than 0, and motion detection is enabled: *MotionDetectionPercentage* more than 0): The input process will be active at all times. When having multiple feeds active, it might use up all usb bandwidth.
