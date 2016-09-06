# RollingWavReader
A C#/.NET WAV reader that supports reading from a stream while it is being updated.

RollingWavReader was particularly designed to be used to read samples and extract audio features from smartphone microphone input in near-real-time while using the UWP MediaCapture API to capture audio for speech and speaker recognition systems.

# Usage
Once you've created a ``RollingWavReader`` instance, simply call the ``Update()`` function at regular intervals and ``FinalizeData()`` once the stream has been closed.

In an (edited for clarity) UWP application this would look something like:

```
protected async override void OnNavigatedTo(NavigationEventArgs e)
{
    stream = new InMemoryRandomAccessStream();
    await mediaCapture.StartRecordToStreamAsync(MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto), stream);

    reader = new RollingWavReader(stream);

    readTimer.Tick += ReadTimer_Tick;
    readTimer.Start();
}

private async void ReadTimer_Tick(object sender, object e)
{
    await reader.Update();
}

private async void end_Click(object sender, RoutedEventArgs e)
{
    await mediaCapture.StopRecordAsync();

    readTimer.Stop();

    await reader.FinalizeData(); //Samples are in reader.Samples

    mediaCapture.Dispose();
}
```

# Real-time feature extraction
RollingWavReader also supports extracting audio features in real-time. Assuming the previous example, you could incorporate a feature extraction ``Func`` that takes a ``double[]`` of samples and returns a ``double[]`` feature vector like so:

```
protected async override void OnNavigatedTo(NavigationEventArgs e)
{
    ...

    extractionTimer.Tick += ExtractionTimer_Tick;
    extractionTimer.Start();
}

private async void ExtractionTimer_Tick(object sender, object e)
{
    //20 ms window and a 10 ms window offset
    await reader.FilterAndExtractRollingSamples(20, 10, featureExtractor);
}

private async void end_Click(object sender, RoutedEventArgs e)
{
    ...

    var features = reader.FinishMFCCSamples(20, 10, featureExtractor);
}
```