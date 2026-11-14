# NotchTasks for Windows: Now Playing through the System Media Transport Controls (SMTC).
# Started by src/core/media.js as one long-lived child. Port of NowPlaying.swift, public WinRT API only.
# stdout: one JSON line when the state changed: {title, artist, album, app, playing, position, duration}.
#         artBase64 + artMime are added when the track changed (thumbnail up to 150 KB). {"none":true} = no session.
# stdin : one command per line: toggle | next | previous | seek <seconds> | slow | fast.
#         End of stdin means the app quit: the script exits.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName System.Runtime.WindowsRuntime
[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,Windows.Media.Control,ContentType=WindowsRuntime] | Out-Null
[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties,Windows.Media.Control,ContentType=WindowsRuntime] | Out-Null
[Windows.Storage.Streams.DataReader,Windows.Storage.Streams,ContentType=WindowsRuntime] | Out-Null

# WinRT IAsyncOperation<T> -> T, blocking. The AsTask extension turns it into a .NET task.
$script:AsTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1

function Invoke-WinRt($operation, [Type]$resultType) {
    $task = $script:AsTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
    $task.Wait(-1) | Out-Null
    return $task.Result
}

# stdin is read on a background thread, so the poll loop never blocks waiting for a command.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;
public static class NotchStdin {
    static readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
    static volatile bool closed;
    public static void Start() {
        var reader = new Thread(() => {
            var input = new StreamReader(Console.OpenStandardInput());
            string line;
            while ((line = input.ReadLine()) != null) Lines.Enqueue(line);
            closed = true;
        });
        reader.IsBackground = true;
        reader.Start();
    }
    public static string Next() { string line; return Lines.TryDequeue(out line) ? line : null; }
    public static bool Closed { get { return closed; } }
}
'@

$manager = Invoke-WinRt ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
$script:lastLine = $null
$script:lastKey = $null
$script:slow = $false

function Send-Line([string]$line) {
    if ($line -ne $script:lastLine) {
        [Console]::Out.WriteLine($line)
        [Console]::Out.Flush()
        $script:lastLine = $line
    }
}

function Get-Session {
    $session = $manager.GetCurrentSession()
    if ($null -eq $session) { $session = $manager.GetSessions() | Select-Object -First 1 }
    return $session
}

# Thumbnail as base64, or $null when it is missing or larger than 150 KB (keeps the line under ~200 KB).
function Get-Art($thumbnail) {
    if ($null -eq $thumbnail) { return $null }
    $stream = Invoke-WinRt ($thumbnail.OpenReadAsync()) ([Windows.Storage.Streams.IRandomAccessStreamWithContentType])
    try {
        $size = [int64]$stream.Size
        if ($size -le 0 -or $size -gt 150KB) { return $null }
        $reader = [Windows.Storage.Streams.DataReader]::new($stream.GetInputStreamAt(0))
        try {
            $null = Invoke-WinRt ($reader.LoadAsync([uint32]$size)) ([uint32])
            $bytes = [byte[]]::new([int]$size)
            $reader.ReadBytes($bytes)
            return @{ b64 = [Convert]::ToBase64String($bytes); mime = [string]$stream.ContentType }
        } finally {
            $reader.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Publish-State {
    $session = Get-Session
    if ($null -eq $session) { Send-Line '{"none":true}'; return }

    $info = $session.GetPlaybackInfo()
    $playing = ([int]$info.PlaybackStatus) -eq 4   # GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
    $timeline = $session.GetTimelineProperties()
    $position = $timeline.Position.TotalSeconds
    # the timeline is stamped when Windows last read it: move it on while playing
    if ($playing) { $position += ([DateTimeOffset]::UtcNow - $timeline.LastUpdatedTime).TotalSeconds }
    $duration = [Math]::Max(0, ($timeline.EndTime - $timeline.StartTime).TotalSeconds)
    if ($duration -gt 0) { $position = [Math]::Min($position, $duration) }
    $position = [Math]::Max(0, $position)

    $props = Invoke-WinRt ($session.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
    $app = [string]$session.SourceAppUserModelId
    $obj = [ordered]@{
        title    = $props.Title
        artist   = $props.Artist
        album    = $props.AlbumTitle
        app      = $app
        playing  = $playing
        position = [Math]::Floor($position)
        duration = [Math]::Floor($duration)
    }
    $key = "$app|$($props.Title)|$($props.Artist)|$($props.AlbumTitle)"
    if ($key -ne $script:lastKey) {
        $script:lastKey = $key
        $art = Get-Art $props.Thumbnail
        if ($null -ne $art) { $obj['artBase64'] = $art.b64; $obj['artMime'] = $art.mime }
    }
    Send-Line ($obj | ConvertTo-Json -Compress)
}

function Run-Command([string]$line) {
    $parts = $line.Trim() -split ' ', 2
    switch ($parts[0]) {
        'slow' { $script:slow = $true; return }
        'fast' { $script:slow = $false; return }
    }
    $session = Get-Session
    if ($null -eq $session) { return }
    switch ($parts[0]) {
        'toggle'   { $null = Invoke-WinRt ($session.TryTogglePlayPauseAsync()) ([bool]) }
        'next'     { $null = Invoke-WinRt ($session.TrySkipNextAsync()) ([bool]) }
        'previous' { $null = Invoke-WinRt ($session.TrySkipPreviousAsync()) ([bool]) }
        'seek'     { $null = Invoke-WinRt ($session.TryChangePlaybackPositionAsync([long]([double]$parts[1] * 10000000))) ([bool]) }
    }
}

[NotchStdin]::Start()
$nextPoll = [DateTime]::MinValue
while ($true) {
    $cmd = [NotchStdin]::Next()
    while ($null -ne $cmd) {
        try { Run-Command $cmd } catch { }
        $nextPoll = [DateTime]::MinValue   # show the result of the command at once
        $cmd = [NotchStdin]::Next()
    }
    if ([NotchStdin]::Closed) { break }
    if ([DateTime]::UtcNow -ge $nextPoll) {
        try { Publish-State } catch { }   # one failed poll must not end the child
        $nextPoll = [DateTime]::UtcNow.AddMilliseconds($(if ($script:slow) { 2000 } else { 1000 }))
    }
    Start-Sleep -Milliseconds 100
}
