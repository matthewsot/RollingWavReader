using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace VoicePrint
{
    class RollingWavReader
    {
        /// <summary>
        /// The number of channels in the source audio
        /// </summary>
        public int Channels { get; set; }
        /// <summary>
        /// The sample rate (in samples/second) of the source audio
        /// </summary>
        public int SampleRate { get; set; }
        /// <summary>
        /// The number of bits per channel per sample of the source audio
        /// </summary>
        public int SampleBitWidth { get; set; }
        /// <summary>
        /// The samples extracted from the audio source. Note that this array will be longer than the actual number of samples extracted.
        /// </summary>
        public long[][] Samples { get; set; }
        /// <summary>
        /// The total number of samples that have been extracted
        /// </summary>
        public int TotalSamples { get; set; }
        /// <summary>
        /// The intermediate output of FilterAndExtractRollingSamples
        /// </summary>
        public List<double[]> Features { get; set; }

        private IRandomAccessStream _Source { get; set; }
        private ulong Position { get; set; }
        private ulong DataChunkPosition { get; set; }
        private bool ReadingData { get; set; }
        private List<Tuple<int, int, int, int>> ExtractionReservedIndexes { get; set; }
        private List<bool> AreExtractionsOngoing { get; set; }

        /// <summary>
        /// Initialize a new RollingWavReader from an IRandomAccessStream source
        /// </summary>
        /// <param name="source">The IRandomAccessStream to read WAV data from</param>
        public RollingWavReader(IRandomAccessStream source)
        {
            _Source = source;
            Position = 0;
            ExtractionReservedIndexes = new List<Tuple<int, int, int, int>>();
            Features = new List<double[]>();
            AreExtractionsOngoing = new List<bool>();
            ReadingData = false;
        }

        /// <summary>
        /// Update the RollingWavReader with new samples from the source stream
        /// </summary>
        public async Task Update()
        {
            var cloned = _Source.CloneStream();
            if (!ReadingData)
            {
                ParseHeaders(cloned);
                if (ReadingData)
                {
                    Position = DataChunkPosition + 8; //set position to the first sample
                    Samples = new long[Channels][];
                    for (var c = 0; c < Samples.Length; c++)
                    {
                        Samples[c] = new long[4800];
                    }
                    TotalSamples = 0;
                }
            }
            if (ReadingData)
            {
                await ParseRollingData(cloned);
            }
        }

        /// <summary>
        /// Parses the "fmt" chunk
        /// </summary>
        /// <param name="rawData">The raw byte array that contains the format chunk</param>
        /// <param name="offset">The offset of the format chunk in the byte array</param>
        private void ParseFormatChunk(byte[] rawData, int offset)
        {
            Channels = BitConverter.ToInt16(rawData, offset + 10);
            SampleRate = BitConverter.ToInt32(rawData, offset + 12);
            SampleBitWidth = BitConverter.ToInt16(rawData, offset + 22);
        }

        /// <summary>
        /// Updates the Samples array with new data from the source stream. Assumes that data fills up to the end of the stream.
        /// </summary>
        /// <param name="source">The source stream to read the data from</param>
        /// <returns></returns>
        private async Task ParseRollingData(IRandomAccessStream source)
        {
            var bytesLeft = source.Size - Position;
            var offsetSample = TotalSamples;

            var bytesPerSample = (ulong)SampleBitWidth / 8;
            var samplesToParse = bytesLeft / (bytesPerSample * (ulong)Channels);

            TotalSamples += (int)samplesToParse;

            if (Samples[0].Length < TotalSamples + (int)samplesToParse)
            {
                for (var c = 0; c < Channels; c++)
                {
                    Array.Resize(ref Samples[c], Samples[0].Length + ((int)samplesToParse) + 4800); //Throw in an extra second for kicks
                }
            }

            var rawData = new byte[(int)samplesToParse * Channels * (int)bytesPerSample];
            source.Seek(Position);
            await source.ReadAsync(rawData.AsBuffer(), (uint)rawData.Length, InputStreamOptions.None);
            for (ulong s = 0; s < samplesToParse; s++)
            {
                for (var c = 0; c < Channels; c++)
                {
                    var sourceOffset = (int)((s * bytesPerSample * (ulong)Channels) + ((ulong)c * bytesPerSample));
                    if (sourceOffset >= rawData.Length) break;
                    var sampleIndex = offsetSample + (int)s;

                    switch (SampleBitWidth)
                    {
                        case 8:
                            Samples[c][sampleIndex] = (long)rawData[sourceOffset];
                            continue;
                        case 16:
                            Samples[c][sampleIndex] = BitConverter.ToInt16(rawData, sourceOffset);
                            continue;
                        case 32:
                            Samples[c][sampleIndex] = BitConverter.ToInt32(rawData, sourceOffset);
                            continue;
                        case 64:
                            Samples[c][sampleIndex] = BitConverter.ToInt64(rawData, sourceOffset);
                            continue;
                        default:
                            throw new NotImplementedException();
                    }
                }
            }
            Position += samplesToParse * (ulong)Channels * bytesPerSample;
        }


        /// <summary>
        /// Updates the Samples array with any remaining data in the source stream and resizes the Samples array to fit the number of samples.
        /// </summary>
        /// <returns></returns>
        public async Task FinalizeData()
        {
            await Update();
            for (var c = 0; c < Channels; c++)
            {
                Array.Resize(ref Samples[c], TotalSamples); //Throw in an extra second for kicks
            }
        }

        /// <summary>
        /// Parses the "fmt" and "data" headers
        /// </summary>
        /// <param name="source">The IRandomAccessStream source to parse from</param>
        private async void ParseHeaders(IRandomAccessStream source)
        {
            source.Seek(0);
            var streamContent = new byte[Math.Min(1000, source.Size)];
            await source.ReadAsync(streamContent.AsBuffer(), (uint)Math.Min(1000, source.Size), InputStreamOptions.None);

            var riffText = System.Text.Encoding.ASCII.GetString(streamContent, 0, 4);
            var waveText = System.Text.Encoding.ASCII.GetString(streamContent, 8, 4);

            var offset = 12;
            while (offset < streamContent.Length)
            {
                try
                {
                    var chunkName = System.Text.Encoding.ASCII.GetString(streamContent, offset, 4).ToLower();
                    if (chunkName.StartsWith("fmt"))
                    {
                        ParseFormatChunk(streamContent, offset);
                    }

                    if (chunkName.StartsWith("data"))
                    {
                        DataChunkPosition = (ulong)offset;
                        ReadingData = true;
                        break;
                    }

                    offset += 8 + BitConverter.ToInt32(streamContent, offset + 4);
                }
                catch { break; }
            }
        }

        /* Optional portion of the library for applying feature extraction to the samples */

        /// <summary>
        /// Determines the number of windows that will fit in a given number of samples
        /// </summary>
        /// <param name="startSample">The sample index to start at</param>
        /// <param name="maxNumSamples">The total number of samples that may be used</param>
        /// <param name="windowWidth">The width of a single window (in samples)</param>
        /// <param name="windowOffset">The offset between windows (in samples)</param>
        /// <returns>The greatest whole number of windows that will fit between startSample and startSample + maxNumSamples</returns>
        private int NumWindows(int startSample, int maxNumSamples, int windowWidth, int windowOffset)
        {
            var numWindows = (int)Math.Floor((double)(TotalSamples - startSample) / (double)windowOffset);
            var endSample = startSample + (numWindows * windowOffset) + (windowWidth - windowOffset); //TODO: Ensure endSample < UsedSampleLength
            if ((endSample - startSample) < maxNumSamples)
            {
                while ((endSample - startSample) < maxNumSamples)
                {
                    endSample += windowOffset;
                    numWindows++;
                }
                endSample -= windowOffset;
                numWindows--;
            }
            else if ((endSample - startSample) > maxNumSamples)
            {
                while ((endSample - startSample) > maxNumSamples)
                {
                    endSample -= windowOffset;
                    numWindows--;
                }
            }
            if (numWindows > 0)
            {
                return numWindows;
            }
            else
            {
                return 0;
            }
        }

        /// <summary>
        /// Filters and applies a feature extraction function to a batch of rolling samples using the given parameters.
        /// </summary>
        /// <param name="windowWidthMs">The width of a single window (in milliseconds)</param>
        /// <param name="windowOffsetMs">The offset between windows (in milliseconds)</param>
        /// <param name="featureExtractor">The feature extraction function</param>
        /// <param name="thresholdAmplitude">The threshold amplitude used to filter out noise</param>
        /// <param name="thresholdOfSample">The proportion of samples in a single window that must be below thresholdAmplitude to cause the window to be discarded</param>
        /// <param name="channel">The channel of audio to be sent to the feature extraction function</param>
        public void FilterAndExtractRollingSamples(int windowWidthMs, int windowOffsetMs,
            Func<double[], double[]> featureExtractor, int thresholdAmplitude = 250, double thresholdOfSample = 0.3,
            int channel = 0)
        {
            var windowWidth = windowWidthMs * (SampleRate / 1000); //(in samples)
            var windowOffset = windowOffsetMs * (SampleRate / 1000); //(in samples)

            if (!ReadingData || TotalSamples < windowWidth) return;

            var startSample = ExtractionReservedIndexes.Any() ? ExtractionReservedIndexes.Max(a => a.Item2) : 0; //Get the highest sample that another one has ended on
            var startFeatureIndex = ExtractionReservedIndexes.Any() ? ExtractionReservedIndexes.Max(a => a.Item4) : 0;
            var maxNumSamples = TotalSamples - startSample;
            var numWindows = NumWindows(startSample, maxNumSamples, windowWidth, windowOffset);
            if (numWindows == 0) return;
            var endSample = startSample + (numWindows * windowOffset) + (windowWidth - windowOffset);
            var endFeatureIndex = startFeatureIndex + numWindows;

            var thisTuple = new Tuple<int, int, int, int>(startSample, endSample, startFeatureIndex, endFeatureIndex);
            ExtractionReservedIndexes.Add(thisTuple);
            AreExtractionsOngoing.Insert(ExtractionReservedIndexes.IndexOf(thisTuple), true);

            var samples = Samples[0];

            var features = new double[numWindows][];
            var didBreak = false;

            var parallelResult = Parallel.For(0, numWindows, (window, state) =>
            {
                try
                {
                    var windowSamples = samples.Skip(startSample + ((int)window * windowOffset)).Take(windowWidth).Select(amplitude => (double)amplitude).ToArray();
                    if (windowSamples.Count(sample => Math.Abs(sample) < thresholdAmplitude) > (windowSamples.Length * thresholdOfSample)) return;
                    features[window] = featureExtractor.Invoke(windowSamples);
                }
                catch
                {
                    didBreak = true;
                    state.Break();
                }
            });

            if (!didBreak && !parallelResult.IsCompleted)
            {
                throw new Exception("Parallel For loop didn't complete.");
            }

            Features.InsertRange(startFeatureIndex, features);
            AreExtractionsOngoing[ExtractionReservedIndexes.IndexOf(thisTuple)] = false;
        }

        /// <summary>
        /// Extracts all remaining features and ensures that feature extraction is complete before returning.
        /// </summary>
        /// <param name="windowWidthMs">The width of a single window (in milliseconds)</param>
        /// <param name="windowOffsetMs">The offset between two windows (in milliseconds)</param>
        /// <param name="featureExtractor">The feature extraction function to use</param>
        /// <param name="checkingDelayMs">The delay to use while checking if feature extraction is complete</param>
        /// <returns>The extracted feature array</returns>
        public async Task<double[][]> FinishMFCCSamples(int windowWidthMs, int windowOffsetMs,
            Func<double[], double[]> featureExtractor, int checkingDelayMs = 250)
        {
            FilterAndExtractRollingSamples(windowWidthMs, windowOffsetMs, featureExtractor);
            while (AreExtractionsOngoing.Any(b => b))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(checkingDelayMs));
            }
            Features = Features.Where(feature => feature != null).ToList();
            return Features.ToArray();
        }
    }
}
