# RollingWavReader
A C#/.NET WAV reader that supports reading from a stream that's being updated in real-time.

# Usage
Once you've created a ``RollingWavReader`` instance, simply call the ``Update()`` function at regular intervals and ``FinalizeData()`` once the stream has been closed.

