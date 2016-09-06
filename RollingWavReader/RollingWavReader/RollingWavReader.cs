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
        public int Channels { get; set; }
        public int SampleRate { get; set; }
        public int SampleBitWidth { get; set; }
        public long[][] Samples { get; set; }
        public List<double[]> Features { get; set; }
        public List<Tuple<int, int, int, int>> ExtractionReservedIndexes { get; set; }
        public List<bool> AreExtractionsOngoing { get; set; }
        public int TotalSamples { get; set; }
        private IRandomAccessStream _Source { get; set; }
        private ulong Position { get; set; }
        private ulong DataChunkPosition { get; set; }
        private bool ReadingData { get; set; }

        public RollingWavReader(IRandomAccessStream source)
        {
            _Source = source;
            Position = 0;
            ExtractionReservedIndexes = new List<Tuple<int, int, int, int>>();
            Features = new List<double[]>();
            AreExtractionsOngoing = new List<bool>();
        }

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

        private void ParseFormatChunk(byte[] rawData, int offset)
        {
            Channels = BitConverter.ToInt16(rawData, offset + 10);
            SampleRate = BitConverter.ToInt32(rawData, offset + 12);
            SampleBitWidth = BitConverter.ToInt16(rawData, offset + 22);
        }

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
                    if (Samples[c][sampleIndex] != default(long))
                    {
                        var y = 1 + 1;
                    }
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

        public async Task FinalizeData()
        {
            await Update();
            for (var c = 0; c < Channels; c++)
            {
                Array.Resize(ref Samples[c], TotalSamples); //Throw in an extra second for kicks
            }
        }

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
                return -1;
            }
        }

        public void FilterAndExtractRollingSamples(int windowWidthMs, int windowOffsetMs,
            Func<double[], int, int, double[]> featureExtractor,
            bool finishing = false, int thresholdAmplitude = 250, double thresholdOfSample = 0.3, int numFilters = 26,
            int numFeatures = 13, int channel = 0, bool keepOrder = false)
        {
            if (keepOrder) throw new NotImplementedException();

            var windowWidth = windowWidthMs * (SampleRate / 1000); //(in samples)
            var windowOffset = windowOffsetMs * (SampleRate / 1000); //(in samples)

            if (!ReadingData || TotalSamples < windowWidth) return;

            var startSample = ExtractionReservedIndexes.Any() ? ExtractionReservedIndexes.Max(a => a.Item2) : 0; //Get the highest sample that another one has ended on
            var startFeatureIndex = ExtractionReservedIndexes.Any() ? ExtractionReservedIndexes.Max(a => a.Item4) : 0;
            var maxNumSamples = TotalSamples - startSample;
            var numWindows = NumWindows(startSample, maxNumSamples, windowWidth, windowOffset);
            if (numWindows == -1) return;
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
                    features[window] = featureExtractor.Invoke(windowSamples, numFilters, numFeatures);
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

        public async Task<double[][]> FinishMFCCSamples(int windowWidthMs, int windowOffsetMs,
            Func<double[], int, int, double[]> featureExtractor, int checkingDelayMs = 250)
        {
            FilterAndExtractRollingSamples(windowWidthMs, windowOffsetMs, featureExtractor, true);
            while (AreExtractionsOngoing.Any(b => b))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(checkingDelayMs));
            }
            Features = Features.Where(feature => feature != null).ToList();
            return Features.ToArray();
        }
    }
}
